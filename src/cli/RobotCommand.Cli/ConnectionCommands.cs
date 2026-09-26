using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Media;
using RobotCommand.Services.Operations;

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
        var dataDir = parsed.Get("data-dir") ?? RobotCommandDataDirectory.GetDefaultPath();
        var json = parsed.Has("json");
        var reporter = new ConsoleReporter(json);
        using var stopping = ConsoleCancellation.Create();
        using var host = RobotCommandRuntimeHost.Build(AppContext.BaseDirectory, RobotCommandRuntimeMode.Cli, dataDirectory: dataDir);
        var connections = host.Services.GetRequiredService<IConnectionManagementWorkflow>();
        var units = host.Services.GetRequiredService<IUnitObservationWorkflow>();
        var associations = host.Services.GetRequiredService<IUnitAssociationWorkflow>();
        var routing = host.Services.GetRequiredService<IUnitRoutingWorkflow>();
        var lifecycle = host.Services.GetRequiredService<IConnectionRuntimeLifecycle>();
        var manager = host.Services.GetRequiredService<ILogosConnectionManager>();
        var playback = host.Services.GetRequiredService<IVideoPlaybackAdapter>();
        try
        {
            return group == "connection"
                ? await RunConnectionAsync(arguments[1].ToLowerInvariant(), parsed, connections, units, lifecycle, manager, playback, reporter, stopping.Token)
                : await RunUnitAsync(arguments[1].ToLowerInvariant(), parsed, units, associations, routing, connections, lifecycle, reporter, stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch (Exception ex) { reporter.Error(ex.Message); return 1; }
        finally { await lifecycle.DisposeAsync(); }
    }

    private static async Task<int> RunConnectionAsync(string command, CliArguments args, IConnectionManagementWorkflow workflow,
        IUnitObservationWorkflow units, IConnectionRuntimeLifecycle lifecycle, ILogosConnectionManager manager,
        IVideoPlaybackAdapter playback, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "list": reporter.Event("connection.list", workflow.Connections.Select(item => SafeConnectionSummary(item, args.Has("json") || args.Has("verbose"))).ToArray()); return 0;
            case "show":
            case "status": reporter.Event("connection.show", SafeConnectionSummary(RequireConnection(workflow, args.RequiredPosition(0)), args.Has("json") || args.Has("verbose"))); return 0;
            case "add":
                reporter.Event("connection.created", SafeConnectionSummary(await workflow.CreateAsync(BuildMutation(args), cancellationToken), args.Has("json") || args.Has("verbose"))); return 0;
            case "update":
                {
                    var id = args.RequiredPosition(0);
                    reporter.Event("connection.updated", SafeConnectionSummary(await workflow.UpdateAsync(id, BuildMutation(args, RequireConnection(workflow, id)), cancellationToken), args.Has("json") || args.Has("verbose"))); return 0;
                }
            case "remove":
                {
                    var connection = RequireConnection(workflow, args.RequiredPosition(0));
                    await workflow.RemoveAsync(connection.Id, cancellationToken);
                    reporter.Event("connection.removed", new { connection.Name, connection.Mode });
                    return 0;
                }
            case "disconnect":
                await workflow.DisconnectAsync(args.RequiredPosition(0), cancellationToken); reporter.Event("connection.disconnected", SafeConnectionSummary(RequireConnection(workflow, args.RequiredPosition(0)), args.Has("json") || args.Has("verbose"))); return 0;
            case "refresh":
                await workflow.RefreshAsync(args.RequiredPosition(0), cancellationToken); reporter.Event("connection.refreshed", SafeConnectionSummary(RequireConnection(workflow, args.RequiredPosition(0)), args.Has("json") || args.Has("verbose"))); return 0;
            case "probe":
                await workflow.RefreshAsync(args.RequiredPosition(0), cancellationToken); reporter.Event("connection.probed", SafeConnectionSummary(RequireConnection(workflow, args.RequiredPosition(0)), args.Has("json") || args.Has("verbose"))); return 0;
            case "stream":
                return await StreamAsync(args, workflow, manager, playback, reporter, cancellationToken);
            case "connect":
                {
                    var id = args.RequiredPosition(0);
                    await workflow.ConnectAsync(id, await ReadCredentialsAsync(RequireConnection(workflow, id), args, cancellationToken), cancellationToken);
                    reporter.Event("connection.connecting", SafeConnectionSummary(RequireConnection(workflow, id), args.Has("json") || args.Has("verbose")));
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

    private static async Task<int> StreamAsync(CliArguments args, IConnectionManagementWorkflow workflow,
        ILogosConnectionManager manager, IVideoPlaybackAdapter playback, ConsoleReporter reporter,
        CancellationToken cancellationToken)
    {
        var connectionId = args.RequiredPosition(0);
        var connection = RequireConnection(workflow, connectionId);
        if (connection.Mode != ManagedConnectionMode.Media)
            throw new ArgumentException("Streaming requires a media connection.");
        var seconds = Math.Clamp(args.Int("seconds", 10) ?? 10, 1, 300);
        await workflow.ConnectAsync(connectionId, null, cancellationToken);
        var cameraSourceId = connectionId.StartsWith("media:", StringComparison.Ordinal) ? connectionId[6..] : connectionId;
        var stream = await manager.OpenCameraStreamAsync(connectionId,
            new CameraStreamOpenRequest(cameraSourceId, VideoProtocolPreference.Rtsp), cancellationToken);
        try
        {
            await playback.AttachAsync(stream, cancellationToken);
            reporter.Event("connection.stream.opened", new
            {
                connection.Name,
                Playback = playback.Status.State,
                Protocol = playback.Status.Protocol,
                DurationSeconds = seconds
            });
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            var status = playback.Status;
            reporter.Event("connection.stream.status", new
            {
                connection.Name,
                Playback = status.State,
                status.Summary,
                Detail = GStreamerPipelineArguments.RedactText(status.Detail)
            });
            return status.State is VideoPlaybackState.Live or VideoPlaybackState.Degraded ? 0 : 1;
        }
        finally
        {
            try { await playback.DetachAsync(CancellationToken.None); }
            finally { await manager.CloseCameraStreamAsync(connectionId, stream.StreamId, CancellationToken.None); }
        }
    }

    private static async Task<int> RunUnitAsync(string command, CliArguments args, IUnitObservationWorkflow workflow,
        IUnitAssociationWorkflow associations,
        IUnitRoutingWorkflow routing, IConnectionManagementWorkflow connections,
        IConnectionRuntimeLifecycle lifecycle, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "list": reporter.Event("unit.list", associations.Units); return 0;
            case "show":
                {
                    var id = args.RequiredPosition(0);
                    reporter.Event("unit.show", associations.TryGet(id, out var saved) && saved is not null ? saved : RequireUnit(workflow, id));
                    return 0;
                }
            case "create":
                {
                    var name = args.Required("name");
                    var connectionId = args.Get("connection");
                    var vehicleId = args.Get("vehicle");
                    if (vehicleId is not null && connectionId is null)
                        throw new ArgumentException("--vehicle requires --connection.");
                    var vehicle = vehicleId is null ? null : new UnitVehicleSourceBinding(connectionId!, vehicleId);
                    var saved = vehicle is not null
                        ? await associations.SaveAsync(null, new UnitDefinitionRequest(name, [vehicle]), cancellationToken)
                        : await associations.SaveAsync(null, new UnitDefinitionRequest(name, [], ConnectionIds: [connectionId ?? args.Required("connection")]), cancellationToken);
                    reporter.Event("unit.created", saved);
                    return 0;
                }
            case "update":
                {
                    var id = args.RequiredPosition(0);
                    var existing = RequireAssociation(associations, id);
                    var saved = await associations.SaveAsync(id, RequestFor(existing, args.Get("name") ?? existing.DisplayName), cancellationToken);
                    reporter.Event("unit.updated", saved);
                    return 0;
                }
            case "delete":
                {
                    var id = args.RequiredPosition(0);
                    await associations.DeleteAsync(id, cancellationToken);
                    reporter.Event("unit.deleted", new { id });
                    return 0;
                }
            case "vehicle":
            case "camera":
            case "authority":
            case "route":
                return await RunUnitMutationAsync(command, args, associations, routing, connections, lifecycle, reporter, cancellationToken);
            case "watch":
                await lifecycle.StartAsync(true, null, cancellationToken);
                await WatchUnitsAsync(workflow, associations, args.Positionals.FirstOrDefault(), reporter, cancellationToken);
                return 0;
            default: return Usage();
        }
    }

    private static async Task<int> RunUnitMutationAsync(string group, CliArguments args, IUnitAssociationWorkflow associations,
        IUnitRoutingWorkflow routing, IConnectionManagementWorkflow connections, IConnectionRuntimeLifecycle lifecycle,
        ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var operation = args.RequiredPosition(0).ToLowerInvariant();
        var id = args.RequiredPosition(1);
        var existing = RequireAssociation(associations, id);
        if (group == "route" && operation == "status")
        {
            reporter.Event("unit.route.status", routing.ForUnit(id));
            return 0;
        }
        var vehicles = existing.VehicleSources.ToList();
        var cameras = existing.CameraSources.ToList();
        var logicalCameras = (existing.Cameras ?? []).ToList();
        var routes = (existing.Routes ?? []).ToList();
        string? commandAuthority = existing.CommandAuthorityConnectionId;
        string? telemetryAuthority = existing.TelemetryAuthorityConnectionId;
        string? diagnosticsAuthority = existing.DiagnosticsAuthorityConnectionId;
        switch (group, operation)
        {
            case ("vehicle", "add"):
                vehicles.Add(new UnitVehicleSourceBinding(args.Required("connection"), args.Required("vehicle")));
                break;
            case ("vehicle", "remove"):
                vehicles.RemoveAll(item => item.ConnectionId == args.Required("connection") && item.VehicleId == args.Required("vehicle"));
                break;
            case ("camera", "add"):
                cameras.Add(new UnitCameraSourceBinding(args.Required("connection"), args.Required("camera")));
                break;
            case ("camera", "remove"):
                cameras.RemoveAll(item => item.ConnectionId == args.Required("connection") && item.CameraSourceId == args.Required("camera"));
                break;
            case ("camera", "bind"):
                {
                    var videoConnectionId = args.Get("video-connection");
                    var mediaSourceId = videoConnectionId is null ? null : videoConnectionId.StartsWith("media:", StringComparison.Ordinal) ? videoConnectionId[6..] : videoConnectionId;
                    var logicalCamera = new UnitCameraDeviceBinding(
                            args.Get("camera-id") ?? $"camera-{Guid.NewGuid():N}", args.Required("name"),
                        args.Get("control-connection"), args.Get("control-camera"), mediaSourceId,
                        args.Get("standby-video-connections")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(value => value.StartsWith("media:", StringComparison.Ordinal) ? value[6..] : value).ToArray());
                    if (logicalCamera.ControlConnectionId is { Length: > 0 } controlConnectionId)
                    {
                        await connections.ConnectAsync(controlConnectionId, null, cancellationToken);
                        await lifecycle.StartAsync(false, controlConnectionId, cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    }
                    logicalCameras.RemoveAll(item => item.Id == logicalCamera.Id);
                    logicalCameras.Add(logicalCamera);
                    if (logicalCamera.ControlConnectionId is { Length: > 0 } controlConnection && !(existing.ConnectionIds ?? []).Contains(controlConnection, StringComparer.Ordinal))
                        existing = existing with { ConnectionIds = (existing.ConnectionIds ?? []).Append(controlConnection).Distinct(StringComparer.Ordinal).ToArray() };
                    var videoIds = new[] { logicalCamera.MediaSourceId }.Concat(logicalCamera.StandbyMediaSourceIds ?? []).ToHashSet(StringComparer.Ordinal);
                    routes.RemoveAll(item => (item.Role is UnitRouteRole.Gimbal or UnitRouteRole.Video) &&
                        (item.CameraSourceId == logicalCamera.ControlCameraSourceId || item.MediaSourceId is not null && videoIds.Contains(item.MediaSourceId)));
                    if (logicalCamera.ControlConnectionId is not null && logicalCamera.ControlCameraSourceId is not null)
                        routes.Add(new UnitRouteCandidate(UnitRouteRole.Gimbal, logicalCamera.ControlConnectionId,
                            vehicles.FirstOrDefault(item => item.ConnectionId == logicalCamera.ControlConnectionId)?.VehicleId,
                            logicalCamera.ControlCameraSourceId));
                    if (logicalCamera.MediaSourceId is not null)
                        routes.Add(new UnitRouteCandidate(UnitRouteRole.Video, MediaSourceId: logicalCamera.MediaSourceId));
                    foreach (var sourceId in logicalCamera.StandbyMediaSourceIds ?? [])
                        routes.Add(new UnitRouteCandidate(UnitRouteRole.Video, MediaSourceId: sourceId));
                    break;
                }
            case ("camera", "unbind"):
                {
                    var cameraId = args.Required("camera-id");
                    var removed = logicalCameras.FirstOrDefault(item => item.Id == cameraId);
                    logicalCameras.RemoveAll(item => item.Id == cameraId);
                    if (removed is not null)
                    {
                        var mediaIds = new[] { removed.MediaSourceId }.Concat(removed.StandbyMediaSourceIds ?? []).ToHashSet(StringComparer.Ordinal);
                        routes.RemoveAll(item => item.CameraSourceId == removed.ControlCameraSourceId || item.MediaSourceId is not null && mediaIds.Contains(item.MediaSourceId));
                    }
                    break;
                }
            case ("authority", "set"):
                var connection = args.Required("connection");
                switch (args.Required("role").ToLowerInvariant())
                {
                    case "command": commandAuthority = connection; SetPrimaryRoute(routes, UnitRouteRole.Command, connection, vehicles); break;
                    case "telemetry": telemetryAuthority = connection; SetPrimaryRoute(routes, UnitRouteRole.Telemetry, connection, vehicles); break;
                    case "diagnostics": diagnosticsAuthority = connection; SetPrimaryRoute(routes, UnitRouteRole.Diagnostics, connection, vehicles); break;
                    default: throw new ArgumentException("--role must be command, telemetry, or diagnostics.");
                }
                break;
            case ("route", "add"):
                {
                    var role = ParseRouteRole(args.Required("role"));
                    var candidate = RouteCandidate(role, args, vehicles, logicalCameras);
                    if (routes.Contains(candidate)) throw new ArgumentException("That route candidate is already configured.");
                    if (role == UnitRouteRole.Gimbal && candidate.ConnectionId is { Length: > 0 } gimbalConnectionId)
                    {
                        await connections.ConnectAsync(gimbalConnectionId, null, cancellationToken);
                        await lifecycle.StartAsync(false, gimbalConnectionId, cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    }
                    routes.Add(candidate);
                    break;
                }
            case ("route", "remove"):
                {
                    var role = ParseRouteRole(args.Required("role"));
                    var index = (args.Int("candidate", 1) ?? 1) - 1;
                    var choices = routes.Where(item => item.Role == role).ToArray();
                    if (index < 0 || index >= choices.Length) throw new ArgumentException("--candidate is a 1-based index in the selected role's routes.");
                    routes.Remove(choices[index]);
                    break;
                }
            case ("route", "select"):
                {
                    var role = ParseRouteRole(args.Required("role"));
                    var candidateIndex = (args.Int("candidate", 1) ?? 1) - 1;
                    var choices = routes.Where(item => item.Role == role).ToArray();
                    if (candidateIndex < 0 || candidateIndex >= choices.Length) throw new ArgumentException("--candidate is a 1-based index in the selected role's routes.");
                    var current = routing.ForUnit(id).FirstOrDefault(item => item.Role == role)?.Active;
                    if ((role is UnitRouteRole.Command or UnitRouteRole.Gimbal) && current != choices[candidateIndex] && !args.Has("confirm"))
                        throw new InvalidOperationException("Switching command or gimbal routes requires --confirm.");
                    if (choices[candidateIndex].ConnectionId is { Length: > 0 } connectionId)
                    {
                        await connections.ConnectAsync(connectionId, null, cancellationToken);
                        await lifecycle.StartAsync(false, connectionId, cancellationToken);
                    }
                    var result = await routing.SelectAsync(id, role, candidateIndex, args.Has("confirm"), cancellationToken);
                    reporter.Event("unit.route.selected", result);
                    return 0;
                }
            default: return Usage();
        }

        var saved = await associations.SaveAsync(id, new UnitDefinitionRequest(existing.DisplayName, vehicles, cameras,
            commandAuthority, telemetryAuthority, diagnosticsAuthority,
            existing.ConnectionIds, logicalCameras, routes), cancellationToken);
        reporter.Event($"unit.{group}.{operation}", saved);
        return 0;
    }

    private static UnitVehicleSourceBinding? OptionalVehicle(CliArguments args)
    {
        var connection = args.Get("connection");
        var vehicle = args.Get("vehicle");
        return string.IsNullOrWhiteSpace(connection) && string.IsNullOrWhiteSpace(vehicle)
            ? null
            : new UnitVehicleSourceBinding(connection ?? throw new ArgumentException("--connection is required with --vehicle."), vehicle ?? throw new ArgumentException("--vehicle is required with --connection."));
    }

    private static UnitDefinitionRequest RequestFor(UnitDefinitionSnapshot snapshot, string name)
        => new(name, snapshot.VehicleSources, snapshot.CameraSources, snapshot.CommandAuthorityConnectionId,
            snapshot.TelemetryAuthorityConnectionId, snapshot.DiagnosticsAuthorityConnectionId,
            snapshot.ConnectionIds, snapshot.Cameras, snapshot.Routes);

    private static void SetPrimaryRoute(List<UnitRouteCandidate> routes, UnitRouteRole role, string connectionId,
        IReadOnlyList<UnitVehicleSourceBinding> vehicles)
    {
        var candidate = vehicles.FirstOrDefault(item => item.ConnectionId == connectionId)
            ?? throw new ArgumentException($"Connection '{connectionId}' has no vehicle source on this unit.");
        routes.RemoveAll(item => item.Role == role);
        routes.Add(new UnitRouteCandidate(role, connectionId, candidate.VehicleId));
    }

    private static UnitRouteCandidate RouteCandidate(UnitRouteRole role, CliArguments args,
        IReadOnlyList<UnitVehicleSourceBinding> vehicles, IReadOnlyList<UnitCameraDeviceBinding> cameras)
    {
        if (role is UnitRouteRole.Command or UnitRouteRole.Telemetry or UnitRouteRole.Diagnostics)
        {
            var connectionId = args.Required("connection");
            var vehicleId = args.Get("vehicle") ?? vehicles.FirstOrDefault(item => item.ConnectionId == connectionId)?.VehicleId
                ?? throw new ArgumentException("--vehicle is required when the connection has no vehicle binding.");
            return new UnitRouteCandidate(role, connectionId, vehicleId);
        }
        if (role == UnitRouteRole.Gimbal)
        {
            var cameraId = args.Required("camera-id");
            var camera = cameras.FirstOrDefault(item => item.Id == cameraId && item.ControlConnectionId is not null && item.ControlCameraSourceId is not null)
                ?? throw new ArgumentException("--camera-id must identify a unit camera with a MAVLink control binding.");
            return new UnitRouteCandidate(role, camera.ControlConnectionId,
                vehicles.FirstOrDefault(item => item.ConnectionId == camera.ControlConnectionId)?.VehicleId,
                camera.ControlCameraSourceId);
        }
        var videoConnectionId = args.Get("video-connection") ?? args.Get("connection");
        var mediaSourceId = videoConnectionId is null
            ? throw new ArgumentException("--video-connection is required for video routes.")
            : videoConnectionId.StartsWith("media:", StringComparison.Ordinal) ? videoConnectionId[6..] : videoConnectionId;
        return new UnitRouteCandidate(UnitRouteRole.Video, MediaSourceId: mediaSourceId);
    }

    private static UnitRouteRole ParseRouteRole(string value) => value.ToLowerInvariant() switch
    {
        "command" => UnitRouteRole.Command,
        "gimbal" or "camera" => UnitRouteRole.Gimbal,
        "telemetry" => UnitRouteRole.Telemetry,
        "diagnostics" => UnitRouteRole.Diagnostics,
        "video" => UnitRouteRole.Video,
        _ => throw new ArgumentException("--role must be command, gimbal, telemetry, diagnostics, or video.")
    };

    private static UnitDefinitionSnapshot RequireAssociation(IUnitAssociationWorkflow workflow, string id)
        => workflow.TryGet(id, out var result) && result is not null ? result : throw new KeyNotFoundException($"Saved unit '{id}' was not found.");

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

    private static async Task WatchUnitsAsync(IUnitObservationWorkflow units, IUnitAssociationWorkflow associations, string? unitId, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        void Changed(object? _, EventArgs __) => reporter.Event("unit.changed", unitId is null ? units.Units : RequireUnit(units, unitId));
        void AssociationChanged(object? _, EventArgs __) => reporter.Event("unit.association.changed", unitId is null ? associations.Units : associations.TryGet(unitId, out var saved) ? saved : null);
        units.Changed += Changed; associations.Changed += AssociationChanged; reporter.Info("Watching unit updates. Press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        finally { units.Changed -= Changed; associations.Changed -= AssociationChanged; }
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
    private static ManagedConnectionMode ParseMode(string value) => value.ToLowerInvariant() switch { "direct" or "logos" => ManagedConnectionMode.Direct, "fieldlink" or "linkd" => ManagedConnectionMode.FieldLink, "mavlink" => ManagedConnectionMode.Mavlink, "media" or "rtsp" => ManagedConnectionMode.Media, _ => throw new ArgumentException("--mode must be direct, fieldlink, mavlink, or media.") };
    private static object SafeConnectionSummary(ManagedConnectionSnapshot connection, bool includeId = false)
    {
        var target = connection.Mode == ManagedConnectionMode.Media && Uri.TryCreate(connection.Target, UriKind.Absolute, out var uri)
            ? $"{uri.Host}:{uri.Port}"
            : connection.Target;
        return includeId
            ? new
            {
                connection.Id,
                connection.Name,
                Mode = connection.Mode,
                State = connection.State,
                Target = target,
                connection.AutoConnect,
                connection.AutoReconnect,
                connection.LastSeen,
                connection.LastError
            }
            : new
            {
                connection.Name,
                Mode = connection.Mode,
                State = connection.State,
                Target = target,
                connection.AutoConnect,
                connection.AutoReconnect,
                connection.LastSeen,
                connection.LastError
            };
    }
    private static byte ParseByte(string? value, byte fallback) => value is null ? fallback : byte.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException($"'{value}' is not a valid MAVLink ID.");
    private static KeyValuePair<byte, string>? ParseAlias(string value)
    {
        var split = value.Split('=', 2, StringSplitOptions.TrimEntries); return split.Length == 2 && byte.TryParse(split[0], out var id) && id > 0 && !string.IsNullOrWhiteSpace(split[1]) ? new(id, split[1]) : null;
    }
    private static int Usage()
    {
        Console.Error.WriteLine("Connection: list, show/status <id>, add --mode media --name <name> --target rtsp://host/path, update <id>, probe <id>, stream <id> [--seconds 10], remove <id>, connect <id> [--watch], disconnect <id>, refresh <id>, watch [id].");
        Console.Error.WriteLine("Unit: list, show <id>, create --name <name> --connection <id> --vehicle <id>, update <id>, delete <id>, vehicle add/remove, camera add/remove/bind/unbind, authority set, route add/remove/select, watch [id]. Use --json for newline-delimited JSON.");
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
            if (key is "json" or "watch" or "execute" or "off" or "verbose" or "confirm" or "auto-connect" or "no-auto-connect" or "auto-reconnect" or "no-auto-reconnect" or "replace" or "local" or "remote" or "reverse" or "reverse-entry" or "images-in-turnarounds" or "enabled") { parsed.Add(key, "true"); continue; }
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
