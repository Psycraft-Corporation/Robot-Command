using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

/// <summary>Interactive CLI front end for the shared session-owned operator command workflow.</summary>
internal static class OperatorCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var options = CliArguments.Parse(arguments);
        if (options.Error is not null) { Console.Error.WriteLine(options.Error); return 2; }
        var dataDirectory = options.Get("data-dir") ?? AppContext.BaseDirectory;
        var reporter = new ConsoleReporter(options.Has("json"));
        using var stopping = ConsoleCancellation.Create();
        using var host = RobotCommandRuntimeHost.Build(dataDirectory, RobotCommandRuntimeMode.Cli);
        var workflow = host.Services.GetRequiredService<IOperatorCommandWorkflow>();
        var units = host.Services.GetRequiredService<IUnitObservationWorkflow>();
        var targetScope = host.Services.GetRequiredService<IOperatorTargetScopeWorkflow>();
        var lifecycle = host.Services.GetRequiredService<IConnectionRuntimeLifecycle>();
        var watcher = new WorkflowWatcher(workflow, reporter);
        workflow.Changed += watcher.OnChanged;

        try
        {
            await host.StartAsync(stopping.Token);
            await lifecycle.StartAsync(!options.Has("no-auto-connect"), null, stopping.Token);
            reporter.Info("Operator session started. Queue commands explicitly, then execute them. Type 'help' for commands.");
            while (!stopping.IsCancellationRequested)
            {
                string? line;
                try { line = await Console.In.ReadLineAsync(stopping.Token); }
                catch (OperationCanceledException) { break; }
                if (line is null || !await ExecuteAsync(line, workflow, units, targetScope, reporter, watcher, stopping.Token)) break;
            }
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception) { reporter.Error(exception.Message); return 1; }
        finally
        {
            workflow.Changed -= watcher.OnChanged;
            await lifecycle.DisposeAsync();
            await host.StopAsync(CancellationToken.None);
        }
    }

    internal static async Task<bool> ExecuteAsync(string input, IOperatorCommandWorkflow workflow,
        IUnitObservationWorkflow units, IOperatorTargetScopeWorkflow targetScope, ConsoleReporter reporter, WorkflowWatcher watcher, CancellationToken cancellationToken)
    {
        var tokens = CliTokenizer.Tokenize(input);
        if (tokens.Length == 0) return true;
        var command = tokens[0].ToLowerInvariant();
        var args = CliArguments.Parse(tokens[1..]);
        if (args.Error is not null) { reporter.Error(args.Error); return true; }
        try
        {
            switch (command)
            {
                case "help":
                    reporter.Info("units [id]; commands [--unit id | --team id] [--state state]; inspect <queue-or-command-id>; queue <operation> --unit id [--unit id] | --team id [options] [--execute]; execute <queue-or-batch-id>; cancel <queue-or-batch-id> [message]; cancel-active --unit id [--unit id] | --team id; watch [--unit id] [--queue id]; status; stop");
                    return true;
                case "status":
                    reporter.Event("operator.status", new { workflow.Status, Queued = workflow.QueuedCommands.Count, Active = workflow.ActiveCommands.Count });
                    return true;
                case "units":
                    reporter.Event("unit.list", args.Positionals.FirstOrDefault() is { } unitId
                        ? RequireUnit(units, unitId)
                        : units.Units);
                    return true;
                case "commands":
                    reporter.Event("operator.commands", FilterCommands(workflow.Commands, args.Get("unit"), args.Get("team"), args.Get("state"), targetScope));
                    return true;
                case "inspect":
                    {
                        var id = RequirePosition(args, 0, "A queue, batch, or command ID is required.");
                        if (!workflow.TryGet(id, out var snapshot) || snapshot is null) throw new KeyNotFoundException($"Command '{id}' was not found.");
                        reporter.Event("operator.inspect", snapshot);
                        return true;
                    }
                case "queue":
                    await QueueAsync(args, workflow, targetScope, reporter, cancellationToken);
                    return true;
                case "execute":
                    {
                        var results = await workflow.ExecuteAsync(RequirePosition(args, 0, "A queue or batch ID is required."), cancellationToken);
                        reporter.Event("operator.executed", results);
                        return true;
                    }
                case "cancel":
                    {
                        var id = RequirePosition(args, 0, "A queue or batch ID is required.");
                        var message = args.Positionals.Count > 1 ? string.Join(' ', args.Positionals.Skip(1)) : "Queued command cleared by operator.";
                        await workflow.CancelAsync(id, message, cancellationToken);
                        reporter.Event("operator.cancelled", new { Id = id, Message = message });
                        return true;
                    }
                case "cancel-active":
                    {
                        var unitIds = await ResolveTargetsAsync(args, targetScope, cancellationToken);
                        reporter.Event("operator.active-cancelled", await workflow.CancelActiveAsync(unitIds, cancellationToken));
                        return true;
                    }
                case "watch":
                    if (args.Has("off"))
                    {
                        watcher.Enabled = false;
                        reporter.Info("Stopped watching workflow updates.");
                        return true;
                    }
                    watcher.UnitId = args.Get("unit"); watcher.QueueId = args.Get("queue"); watcher.Enabled = true;
                    watcher.Publish(); reporter.Info("Watching workflow updates. Run 'watch --off' to stop watching.");
                    return true;
                case "stop": return false;
                default: reporter.Error("Unknown command. Type 'help' for commands."); return true;
            }
        }
        catch (Exception exception) { reporter.Error(exception.Message); return true; }
    }

    private static async Task<int> QueueAsync(CliArguments args, IOperatorCommandWorkflow workflow, IOperatorTargetScopeWorkflow targetScope,
        ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var operation = ParseOperation(RequirePosition(args, 0, "An operation is required."));
        var targets = (await ResolveTargetsAsync(args, targetScope, cancellationToken)).Select(unitId => new OperatorCommandQueueTarget(unitId, Parameters(operation, args))).ToArray();
        var batch = await workflow.QueueAsync(new OperatorCommandQueueRequest(operation, targets,
            args.Get("reason") ?? "CLI operator request"), cancellationToken);
        reporter.Event("operator.queued", batch);
        if (args.Has("execute")) reporter.Event("operator.executed", await workflow.ExecuteAsync(batch.BatchId, cancellationToken));
        return 0;
    }

    private static OperatorWorkflowParameters Parameters(OperatorWorkflowCommandKind operation, CliArguments args)
    {
        double? Number(string key) => args.Get(key) is { } value
            ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new ArgumentException($"--{key} must be a number.")
            : null;
        return operation switch
        {
            OperatorWorkflowCommandKind.Takeoff => new(TakeoffAltitudeAglMetres: RequiredNumber("agl", Number)),
            OperatorWorkflowCommandKind.GoTo => new(GoToTargetKind: OperatorWorkflowGoToTargetKind.GlobalWgs84,
                GoToLatitudeDegrees: RequiredNumber("latitude", Number), GoToLongitudeDegrees: RequiredNumber("longitude", Number),
                GoToAltitudeAmslMetres: Number("altitude-amsl"), GoToAcceptanceRadiusMetres: Number("acceptance") ?? 2),
            OperatorWorkflowCommandKind.ChangeAltitude => AltitudeParameters(Number),
            OperatorWorkflowCommandKind.SetHeading => HeadingParameters(Number),
            _ => OperatorWorkflowParameters.None
        };
    }

    private static OperatorWorkflowParameters AltitudeParameters(Func<string, double?> number)
    {
        var values = new[] { number("agl"), number("amsl"), number("delta") };
        if (values.Count(value => value is not null) != 1) throw new ArgumentException("Change altitude requires exactly one of --agl, --amsl, or --delta.");
        return values[0] is { } agl ? new(AltitudeTargetKind: OperatorWorkflowAltitudeTargetKind.AltitudeAgl, AltitudeAglMetres: agl)
            : values[1] is { } amsl ? new(AltitudeTargetKind: OperatorWorkflowAltitudeTargetKind.AltitudeAmsl, AltitudeAmslMetres: amsl)
            : new(AltitudeTargetKind: OperatorWorkflowAltitudeTargetKind.RelativeDelta, AltitudeRelativeDeltaMetres: values[2]);
    }

    private static OperatorWorkflowParameters HeadingParameters(Func<string, double?> number)
    {
        var heading = number("heading"); var relative = number("relative-yaw");
        if ((heading is null) == (relative is null)) throw new ArgumentException("Set heading requires exactly one of --heading or --relative-yaw.");
        return heading is { } absolute
            ? new(HeadingTargetKind: OperatorWorkflowHeadingTargetKind.AbsoluteHeading, HeadingDegrees: absolute)
            : new(HeadingTargetKind: OperatorWorkflowHeadingTargetKind.RelativeYaw, RelativeYawDegrees: relative);
    }

    private static double RequiredNumber(string name, Func<string, double?> number)
        => number(name) ?? throw new ArgumentException($"--{name} is required.");

    private static IReadOnlyList<string> RequireUnits(CliArguments args)
    {
        var units = args.Values("unit");
        if (units.Count == 0) throw new ArgumentException("At least one --unit <id> is required.");
        return units;
    }

    private static string RequirePosition(CliArguments args, int index, string message)
        => args.Positionals.Count > index ? args.Positionals[index] : throw new ArgumentException(message);

    private static async Task<IReadOnlyList<string>> ResolveTargetsAsync(CliArguments args, IOperatorTargetScopeWorkflow targetScope, CancellationToken cancellationToken)
    {
        var units = args.Values("unit");
        var teamId = args.Get("team");
        if (units.Count > 0 && !string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("--unit and --team cannot be used together.");
        if (!string.IsNullOrWhiteSpace(teamId))
            return (await targetScope.ResolveTeamAsync(teamId, cancellationToken)).UnitIds;
        return RequireUnits(args);
    }

    private static IReadOnlyList<OperatorCommandQueueSnapshot> FilterCommands(IEnumerable<OperatorCommandQueueSnapshot> commands, string? unitId, string? teamId, string? state, IOperatorTargetScopeWorkflow? targetScope = null)
    {
        var teamIds = string.IsNullOrWhiteSpace(teamId) || targetScope is null ? null : targetScope.ResolveTeamAsync(teamId, CancellationToken.None).GetAwaiter().GetResult().UnitIds.ToHashSet(StringComparer.Ordinal);
        return commands.Where(command => (unitId is null || string.Equals(command.UnitId, unitId, StringComparison.Ordinal)) &&
            (teamIds is null || teamIds.Contains(command.UnitId)) &&
            (state is null || string.Equals(command.State.ToString(), state, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static UnitObservationSnapshot RequireUnit(IUnitObservationWorkflow workflow, string unitId)
        => workflow.TryGet(unitId, out var unit) && unit is not null ? unit : throw new KeyNotFoundException($"Unit '{unitId}' was not found.");

    private static OperatorWorkflowCommandKind ParseOperation(string value) => value.ToLowerInvariant() switch
    {
        "arm" => OperatorWorkflowCommandKind.Arm,
        "disarm" => OperatorWorkflowCommandKind.Disarm,
        "takeoff" => OperatorWorkflowCommandKind.Takeoff,
        "land" => OperatorWorkflowCommandKind.Land,
        "hold" => OperatorWorkflowCommandKind.Hold,
        "rtl" or "return-home" => OperatorWorkflowCommandKind.ReturnHome,
        "goto" or "go-to" => OperatorWorkflowCommandKind.GoTo,
        "altitude" or "change-altitude" => OperatorWorkflowCommandKind.ChangeAltitude,
        "heading" or "set-heading" => OperatorWorkflowCommandKind.SetHeading,
        _ => throw new ArgumentException("Operation must be arm, disarm, takeoff, land, hold, rtl, goto, altitude, or heading.")
    };


    internal sealed class WorkflowWatcher(IOperatorCommandWorkflow workflow, ConsoleReporter reporter)
    {
        public bool Enabled { get; set; }
        public string? UnitId { get; set; }
        public string? QueueId { get; set; }
        public void OnChanged(object? sender, EventArgs e) { if (Enabled) Publish(); }
        public void Publish()
        {
            var commands = FilterCommands(workflow.Commands, UnitId, null, null);
            if (QueueId is not null) commands = commands.Where(command => command.QueueId == QueueId || command.BatchId == QueueId || command.CommandId == QueueId).ToArray();
            reporter.Event("operator.changed", commands);
        }
    }
}
