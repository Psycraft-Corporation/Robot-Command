using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RobotCommand.Bootstrap;
using RobotCommand.Services.Team;

namespace RobotCommand.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 &&
            (string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))))
        {
            PrintUsage();
            return 0;
        }

        if (args.Length >= 2 && string.Equals(args[0], "session", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase))
            return await SessionCommands.RunAsync(args[2..]);

        if (args.Length >= 2 && string.Equals(args[0], "operator", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase))
            return await OperatorCommands.RunAsync(args[2..]);

        if (args.Length >= 2 && string.Equals(args[0], "simulation", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "worker", StringComparison.OrdinalIgnoreCase))
            return await SimulationCommands.RunAsync(args[2..]);

        if (args.Length >= 2 && string.Equals(args[0], "3d", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "status", StringComparison.OrdinalIgnoreCase))
            return await ThreeDCommands.RunAsync(args[2..]);

        if (args.Length >= 1 && string.Equals(args[0], "ghost", StringComparison.OrdinalIgnoreCase))
            return await GhostCommands.RunAsync(args);

        if (args.Length >= 1 && string.Equals(args[0], "formation", StringComparison.OrdinalIgnoreCase))
            return await FormationCommands.RunAsync(args);

        if (args.Length >= 1 && (string.Equals(args[0], "fence", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(args[0], "px4-fence", StringComparison.OrdinalIgnoreCase)))
            return await FenceCommands.RunAsync(args);

        if (args.Length >= 1 && (string.Equals(args[0], "connection", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(args[0], "unit", StringComparison.OrdinalIgnoreCase)))
            return await ConnectionCommands.RunAsync(args);

        if (args.Length < 2 || !string.Equals(args[0], "server", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 2;
        }

        if (!ServerRunOptions.TryParse(args[2..], out var options, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var dataDirectory = options.DataDirectory ?? AppContext.BaseDirectory;
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        using var host = RobotCommandRuntimeHost.Build(dataDirectory, RobotCommandRuntimeMode.Headless);
        await host.StartAsync(stopping.Token);
        var workflow = host.Services.GetRequiredService<ITeamServerWorkflow>();
        var reporter = new ConsoleReporter(options.Json);
        workflow.Changed += (_, _) => reporter.Event("server.changed", workflow.Status);
        var passphrase = options.OpenAccess ? null : options.Passphrase ?? TeamPassphraseService.CreateSuggestedPassphrase();
        var status = await workflow.StartAsync(new(
            options.Name ?? workflow.Status.Settings.DisplayName,
            options.Port ?? workflow.Status.Settings.Port,
            options.MaximumClients ?? workflow.Status.Settings.MaximumClients,
            options.OpenAccess,
            passphrase), stopping.Token);

        if (!status.IsRunning)
        {
            reporter.Error(status.Error ?? "The Robot Command team server did not start.");
            await host.StopAsync(CancellationToken.None);
            return 1;
        }

        reporter.Event("server.started", status);
        if (options.OpenAccess) reporter.Info("Open access is enabled. Every compatible LAN observer still requires manual approval.");
        else reporter.Info($"Session passphrase: {passphrase}");
        reporter.Info($"Listening on {status.Endpoint}. Type 'help' for server administration commands; Ctrl+C stops the server.");

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                string? line;
                try { line = await Console.In.ReadLineAsync(stopping.Token); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;
                if (!await ExecuteAdminCommandAsync(line, workflow, reporter, stopping.Token)) break;
            }
        }
        finally
        {
            await workflow.StopAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        }
        return 0;
    }

    private static void PrintUsage() => Console.WriteLine(
        "Usage: robotcommand session run [...], robotcommand server run [...], robotcommand operator run [...], robotcommand simulation worker [...], robotcommand 3d status [...], robotcommand ghost [...], robotcommand formation [...], robotcommand fence [...], robotcommand connection <command> [...], or robotcommand unit <command> [...].");

    private static async Task<bool> ExecuteAdminCommandAsync(string input, ITeamServerWorkflow workflow, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var parts = input.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return true;
        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                reporter.Info("Commands: status, requests, approve <id>, reject <id> [message], clients, disconnect <id> [message], auth on [passphrase], auth reauthenticate [passphrase], auth off, stop, help");
                return true;
            case "status": reporter.Event("server.status", workflow.Status); return true;
            case "requests": reporter.Event("server.requests", workflow.PendingRequests); return true;
            case "clients": reporter.Event("server.clients", workflow.ConnectedClients); return true;
            case "approve" when parts.Length >= 2:
                reporter.Event("server.approve", new { Id = parts[1], Approved = workflow.Approve(parts[1]) }); return true;
            case "reject" when parts.Length >= 2:
                reporter.Event("server.reject", new { Id = parts[1], Rejected = workflow.Reject(parts[1], parts.Length == 3 ? parts[2] : null) }); return true;
            case "disconnect" when parts.Length >= 2:
                reporter.Event("server.disconnect", new { Id = parts[1], Disconnected = workflow.Disconnect(parts[1], parts.Length == 3 ? parts[2] : null) }); return true;
            case "auth" when parts.Length >= 2 && parts[1].Equals("off", StringComparison.OrdinalIgnoreCase):
                await workflow.SetAuthenticationAsync(false, false, cancellationToken: cancellationToken); reporter.Info("Authentication disabled for new observers."); return true;
            case "auth" when parts.Length >= 2 && parts[1].Equals("on", StringComparison.OrdinalIgnoreCase):
            case "auth" when parts.Length >= 2 && parts[1].Equals("reauthenticate", StringComparison.OrdinalIgnoreCase):
                var reauthenticate = parts[1].Equals("reauthenticate", StringComparison.OrdinalIgnoreCase);
                var passphrase = parts.Length == 3 ? parts[2] : TeamPassphraseService.CreateSuggestedPassphrase();
                await workflow.SetAuthenticationAsync(true, reauthenticate, passphrase, cancellationToken);
                reporter.Info($"Authentication enabled{(reauthenticate ? "; current observers must authenticate again" : string.Empty)}. Session passphrase: {passphrase}");
                return true;
            case "stop": return false;
            default: reporter.Error("Unknown or incomplete command. Type 'help' for commands."); return true;
        }
    }
}

internal sealed record ServerRunOptions(string? Name, int? Port, int? MaximumClients, string? Passphrase, bool OpenAccess, string? DataDirectory, bool Json)
{
    public static bool TryParse(string[] args, out ServerRunOptions options, out string? error)
    {
        string? name = null, passphrase = null, dataDirectory = null;
        int? port = null, maximumClients = null;
        var openAccess = false;
        var json = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? Next() => ++index < args.Length ? args[index] : null;
            switch (argument)
            {
                case "--name": name = Next(); break;
                case "--passphrase": passphrase = Next(); break;
                case "--data-dir": dataDirectory = Next(); break;
                case "--port" when int.TryParse(Next(), out var parsedPort): port = parsedPort; break;
                case "--max-clients" when int.TryParse(Next(), out var parsedClients): maximumClients = parsedClients; break;
                case "--open-access": openAccess = true; break;
                case "--json": json = true; break;
                default: options = default!; error = $"Unknown or invalid option '{argument}'."; return false;
            }
        }
        if (openAccess && passphrase is not null) { options = default!; error = "Use either --open-access or --passphrase, not both."; return false; }
        options = new(name, port, maximumClients, passphrase, openAccess, dataDirectory, json);
        error = null;
        return true;
    }
}

internal sealed class ConsoleReporter(bool json, Action<string>? output = null, Action<string>? error = null)
{
    private readonly Action<string> _output = output ?? Console.WriteLine;
    private readonly Action<string> _error = error ?? Console.Error.WriteLine;

    public void Event(string kind, object? value)
    {
        _output(json
            ? JsonSerializer.Serialize(new { kind, at = DateTimeOffset.UtcNow, value })
            : CliHumanFormatter.Format(kind, value));
    }
    public void Info(string message) => _output(json ? JsonSerializer.Serialize(new { kind = "info", message }) : message);
    public void Error(string message) => _error(json ? JsonSerializer.Serialize(new { kind = "error", message }) : message);
}
