using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

/// <summary>CLI front end for the shared saved-connection and unit-observation workflows.</summary>
internal static class ConnectionCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length == 0) return Usage();
        var group = arguments[0].ToLowerInvariant();
        if (group is not ("connection" or "unit")) return Usage();
        if (arguments.Length == 1) return Usage();

        var parsed = CliArguments.Parse(arguments[2..]);
        if (parsed.Error is not null) { Console.Error.WriteLine(parsed.Error); return 2; }
        var dataDir = parsed.Get("data-dir") ?? AppContext.BaseDirectory;
        var json = parsed.Has("json");
        var reporter = new ConsoleReporter(json);
        using var stopping = ConsoleCancellation.Create();
        using var host = RobotCommandRuntimeHost.Build(dataDir, RobotCommandRuntimeMode.Cli);
        var connections = host.Services.GetRequiredService<IConnectionManagementWorkflow>();
        var units = host.Services.GetRequiredService<IUnitObservationWorkflow>();
        var lifecycle = host.Services.GetRequiredService<IConnectionRuntimeLifecycle>();
        try
        {
            return group == "connection"
                ? await RunConnectionAsync(arguments[1].ToLowerInvariant(), parsed, connections, units, lifecycle, reporter, stopping.Token)
                : await RunUnitAsync(arguments[1].ToLowerInvariant(), parsed, units, lifecycle, reporter, stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch (Exception ex) { reporter.Error(ex.Message); return 1; }
        finally { await lifecycle.DisposeAsync(); }
    }

    private static async Task<int> RunConnectionAsync(string command, CliArguments args, IConnectionManagementWorkflow workflow,
        IUnitObservationWorkflow units, IConnectionRuntimeLifecycle lifecycle, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "list": reporter.Event("connection.list", workflow.Connections); return 0;
            case "show": reporter.Event("connection.show", RequireConnection(workflow, args.RequiredPosition(0))); return 0;
            case "add":
                reporter.Event("connection.created", await workflow.CreateAsync(BuildMutation(args), cancellationToken)); return 0;
            case "update":
                {
                    var id = args.RequiredPosition(0);
                    reporter.Event("connection.updated", await workflow.UpdateAsync(id, BuildMutation(args, RequireConnection(workflow, id)), cancellationToken)); return 0;
                }
            case "remove":
                await workflow.RemoveAsync(args.RequiredPosition(0), cancellationToken); reporter.Event("connection.removed", new { id = args.RequiredPosition(0) }); return 0;
            case "disconnect":
                await workflow.DisconnectAsync(args.RequiredPosition(0), cancellationToken); reporter.Event("connection.disconnected", RequireConnection(workflow, args.RequiredPosition(0))); return 0;
            case "refresh":
                await workflow.RefreshAsync(args.RequiredPosition(0), cancellationToken); reporter.Event("connection.refreshed", RequireConnection(workflow, args.RequiredPosition(0))); return 0;
            case "connect":
                {
                    var id = args.RequiredPosition(0);
                    await workflow.ConnectAsync(id, await ReadCredentialsAsync(RequireConnection(workflow, id), args, cancellationToken), cancellationToken);
                    reporter.Event("connection.connecting", RequireConnection(workflow, id));
                    if (!args.Has("watch")) return 0;
                    await lifecycle.StartAsync(false, id, cancellationToken);
                    await WatchAsync(workflow, units, id, reporter, cancellationToken);
                    return 0;
                }
            case "watch":
                {
                    var id = args.Positionals.FirstOrDefault();
                    if (id is not null)
                    {
                        await workflow.ConnectAsync(id, null, cancellationToken);
                        await lifecycle.StartAsync(false, id, cancellationToken);
                    }
                    else await lifecycle.StartAsync(true, null, cancellationToken);
                    await WatchAsync(workflow, units, id, reporter, cancellationToken);
                    return 0;
                }
            default: return Usage();
        }
    }

    private static async Task<int> RunUnitAsync(string command, CliArguments args, IUnitObservationWorkflow workflow,
        IConnectionRuntimeLifecycle lifecycle, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "list": reporter.Event("unit.list", workflow.Units); return 0;
            case "show": reporter.Event("unit.show", RequireUnit(workflow, args.RequiredPosition(0))); return 0;
            case "watch":
                await lifecycle.StartAsync(true, null, cancellationToken);
                await WatchUnitsAsync(workflow, args.Positionals.FirstOrDefault(), reporter, cancellationToken);
                return 0;
            default: return Usage();
        }
    }

    private static async Task WatchAsync(IConnectionManagementWorkflow connections, IUnitObservationWorkflow units, string? connectionId,
        ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        void ConnectionChanged(object? _, EventArgs __) => reporter.Event("connection.changed", connectionId is null ? connections.Connections : RequireConnection(connections, connectionId));
        void UnitChanged(object? _, EventArgs __) => reporter.Event("unit.changed", units.Units.Where(unit => connectionId is null || unit.ConnectionIds.Contains(connectionId, StringComparer.Ordinal)).ToArray());
        connections.Changed += ConnectionChanged; units.Changed += UnitChanged;
        reporter.Info("Watching connection and unit updates. Press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { connections.Changed -= ConnectionChanged; units.Changed -= UnitChanged; }
    }

    private static async Task WatchUnitsAsync(IUnitObservationWorkflow units, string? unitId, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        void Changed(object? _, EventArgs __) => reporter.Event("unit.changed", unitId is null ? units.Units : RequireUnit(units, unitId));
        units.Changed += Changed; reporter.Info("Watching unit updates. Press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { units.Changed -= Changed; }
    }

    private static ConnectionMutationRequest BuildMutation(CliArguments args, ManagedConnectionSnapshot? existing = null)
    {
        var mode = ParseMode(args.Get("mode") ?? existing?.Mode.ToString() ?? "direct");
        var existingMavlink = existing?.Mavlink;
        var transport = args.Get("transport") is { } transportValue
            ? string.Equals(transportValue, "serial", StringComparison.OrdinalIgnoreCase) ? ManagedMavlinkTransport.Serial : ManagedMavlinkTransport.UdpListener
            : existingMavlink?.Transport ?? ManagedMavlinkTransport.UdpListener;
        var aliases = args.Values("alias").Count > 0
            ? args.Values("alias").Select(ParseAlias).Where(item => item is not null).Cast<KeyValuePair<byte, string>>().ToDictionary(item => item.Key, item => item.Value)
            : existingMavlink?.SystemAliases ?? new Dictionary<byte, string>();
        var mavlink = mode == ManagedConnectionMode.Mavlink ? new ManagedMavlinkOptions(
            transport,
            args.Get("autopilot") is { } autopilot ? string.Equals(autopilot, "ardupilot", StringComparison.OrdinalIgnoreCase) ? ManagedMavlinkAutopilot.ArduPilot : ManagedMavlinkAutopilot.Px4 : existingMavlink?.Autopilot ?? ManagedMavlinkAutopilot.Px4,
            ParseByte(args.Get("source-system"), existingMavlink?.SourceSystemId ?? 255), ParseByte(args.Get("source-component"), existingMavlink?.SourceComponentId ?? 190), aliases,
            args.Int("baud", existingMavlink?.BaudRate ?? (transport == ManagedMavlinkTransport.Serial ? 57600 : null)), args.Get("serial-device-id") ?? existingMavlink?.SerialDeviceId, args.Get("port-name") ?? existingMavlink?.LastKnownPort) : null;
        var existingLinkd = existing?.Linkd;
        var linkd = mode == ManagedConnectionMode.FieldLink ? new ManagedLinkdOptions(
            args.Get("linkd-plugin") ?? existingLinkd?.TransportPlugin ?? "sik_serial", args.Int("linkd-baud", existingLinkd?.BaudRate ?? 57600) ?? 57600,
            args.Get("serial-device-id") ?? existingLinkd?.SerialDeviceId, args.Get("port-name") ?? existingLinkd?.LastKnownPort,
            args.Get("radio-profile") ?? existingLinkd?.RadioProfileKey, args.Get("wire-profile") ?? existingLinkd?.WireProfilePath) : null;
        return new ConnectionMutationRequest(args.Get("name") ?? existing?.Name ?? args.Required("name"), args.Get("target") ?? existing?.Target ?? args.Required("target"), mode,
            args.Bool("auto-connect", existing?.AutoConnect ?? false), args.Bool("auto-reconnect", existing?.AutoReconnect ?? true), args.Get("description") ?? existing?.Description, mavlink, linkd, args.Get("id") ?? existing?.Id);
    }

    private static async Task<ConnectionCredentialInput?> ReadCredentialsAsync(ManagedConnectionSnapshot connection, CliArguments args, CancellationToken cancellationToken)
    {
        if (connection.Mode != ManagedConnectionMode.Direct) return null;
        string? ReadEnvironment(string key) => string.IsNullOrWhiteSpace(key) ? null : Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException($"Environment variable '{key}' was not found.");
        var apiKey = ReadEnvironment(args.Get("api-key-env") ?? "");
        var token = ReadEnvironment(args.Get("bearer-token-env") ?? "");
        if ((apiKey is not null || token is not null) || Console.IsInputRedirected) return new(apiKey, token);
        if (!Console.IsOutputRedirected)
        {
            Console.Write("API key (optional, Enter to skip): "); apiKey = ReadSecret();
            Console.Write("Bearer token (optional, Enter to skip): "); token = ReadSecret();
            Console.WriteLine();
        }
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new(apiKey, token);
    }

    private static string? ReadSecret()
    {
        var value = new System.Text.StringBuilder(); ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        return value.Length == 0 ? null : value.ToString();
    }

    private static ManagedConnectionSnapshot RequireConnection(IConnectionManagementWorkflow workflow, string id)
        => workflow.TryGet(id, out var result) && result is not null ? result : throw new KeyNotFoundException($"Connection '{id}' was not found.");
    private static UnitObservationSnapshot RequireUnit(IUnitObservationWorkflow workflow, string id)
        => workflow.TryGet(id, out var result) && result is not null ? result : throw new KeyNotFoundException($"Unit '{id}' was not found.");
    private static ManagedConnectionMode ParseMode(string value) => value.ToLowerInvariant() switch { "direct" or "logos" => ManagedConnectionMode.Direct, "fieldlink" or "linkd" => ManagedConnectionMode.FieldLink, "mavlink" => ManagedConnectionMode.Mavlink, _ => throw new ArgumentException("--mode must be direct, fieldlink, or mavlink.") };
    private static byte ParseByte(string? value, byte fallback) => value is null ? fallback : byte.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException($"'{value}' is not a valid MAVLink ID.");
    private static KeyValuePair<byte, string>? ParseAlias(string value)
    {
        var split = value.Split('=', 2, StringSplitOptions.TrimEntries); return split.Length == 2 && byte.TryParse(split[0], out var id) && id > 0 && !string.IsNullOrWhiteSpace(split[1]) ? new(id, split[1]) : null;
    }
    private static int Usage()
    {
        Console.Error.WriteLine("Connection: list, show <id>, add, update <id>, remove <id>, connect <id> [--watch], disconnect <id>, refresh <id>, watch [id].");
        Console.Error.WriteLine("Unit: list, show <id>, watch [id]. Use --json for newline-delimited JSON. Connection add/update require --name, --target and optionally --mode direct|fieldlink|mavlink.");
        return 2;
    }
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];
    public string? Error { get; private set; }
    public static CliArguments Parse(string[] arguments)
    {
        var parsed = new CliArguments();
        for (var index = 0; index < arguments.Length; index++)
        {
            var item = arguments[index];
            if (!item.StartsWith("--", StringComparison.Ordinal)) { parsed.Positionals.Add(item); continue; }
            var key = item[2..];
            if (key is "json" or "watch" or "execute" or "off" or "verbose" or "auto-connect" or "no-auto-connect" or "auto-reconnect" or "no-auto-reconnect" or "replace" or "local" or "remote" or "reverse" or "reverse-entry" or "images-in-turnarounds" or "enabled") { parsed.Add(key, "true"); continue; }
            if (++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal)) { parsed.Error = $"Option '{item}' requires a value."; return parsed; }
            parsed.Add(key, arguments[index]);
        }
        return parsed;
    }
    private void Add(string key, string value) { if (!_options.TryGetValue(key, out var values)) _options[key] = values = []; values.Add(value); }
    public bool Has(string key) => _options.ContainsKey(key);
    public string? Get(string key) => _options.TryGetValue(key, out var values) ? values.LastOrDefault() : null;
    public IReadOnlyList<string> Values(string key) => _options.TryGetValue(key, out var values) ? values : [];
    public string Required(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required.");
    public string RequiredPosition(int index) => Positionals.Count > index ? Positionals[index] : throw new ArgumentException("A connection or unit ID is required.");
    public int? Int(string key, int? fallback) => Get(key) is null ? fallback : int.TryParse(Get(key), out var value) && value > 0 ? value : throw new ArgumentException($"--{key} must be a positive integer.");
    public bool Bool(string positive, bool fallback)
        => Has("no-" + positive) ? false : Has(positive) ? true : fallback;
}

internal static class ConsoleCancellation
{
    public static CancellationTokenSource Create()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; source.Cancel(); };
        return source;
    }
}
