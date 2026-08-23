using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

/// <summary>Interactive headless shell over the shared selection, map, Ghost, manual, and operator workflows.</summary>
internal static class SessionCommands
{
    private const string HelpText = """
        ROBOT COMMAND CLI
        Interactive session commands. Enter a command, then press Enter.
        Use Ctrl+C or type 'stop' to leave the session.

        SELECTION
          selection show
          selection set --unit <id> [--unit <id> ...] [--anchor <id>]
          selection clear
          team select <team-id> | team deselect | team selection

        MAP AND VIEWS
          map scene [--watch]
          map viewport show | map viewport set --latitude <deg> --longitude <deg> --resolution <m/px>
          map style list | map style select <style-id>
          map jump selected|operator | map follow selected | map stop-follow
          map overlay show|hide <geometries|policy|trails|destinations|labels>
          view list | view save --name <name> | view apply <id> | view remove <id>

        3D
          3d status       Show renderer-independent scene state.

        FORMATIONS
          formation list | formation show <id> | formation create --name <name>
          formation rename <id> --name <name> | formation delete <id>
          formation member add|set|remove ...

        UNITS AND CONTROL
          ghost profile list | ghost profile show <profile-id>
          ghost profile asset show|upload|remove <profile-id> [path]
          ghost profile create --name <name> [--max-speed <m/s>] [--climb-rate <m/s>] ...
          ghost profile update <profile-id> [options] | ghost profile delete <profile-id>
          ghost list | ghost watch [--off] | ghost create --profile <profile-id> [--count <n>] | ghost delete <unit-id>
          connection list | connection connect <connection-id> | connection disconnect <connection-id>
          team list | team show <team-id>
          team create --unit <id> [--unit <id> ...] [--name <name>]
          team assign <team-id> --unit <id> [--unit <id> ...]
          team clear --unit <id> [--unit <id> ...]
          team move <team-id> --unit <id> --index <n> | team delete <team-id>
          team formation lock|unlock|status <team-id>
          team formation goto <team-id> --latitude <deg> --longitude <deg>
          team formation altitude <team-id> --agl <metres>
          team formation rotate <team-id> --degrees <signed-degrees>
          team formation scale <team-id> --percent <50-200>
          team formation hold <team-id>
          team formation assign <team-id> --formation <formation-id> --heading <degrees>
          team formation assignment <team-id>
          team formation slot <team-id> --unit <unit-id> --slot <slot-id>
          team formation clear <team-id>
          team formation enter <team-id> [--execute]
          manual devices | manual select-device <id> | manual status | manual watch
          manual take --unit <id> | manual release [message]
          operator ...  Queue, inspect, execute, cancel, or watch operations.

        LIBRARIES AND DEVICES
          mission, task, behaviour, geometry, autonomy, px4 params, ardupilot params, sik, plan ...
          media, evidence, team, settings ...

        SESSION
          status       Show current shared Runtime state.
          help         Show this command reference.
          stop         Close this terminal session; the GUI remains running.

        Values use metres, degrees, and WGS84 coordinates. Commands that mutate
        device or remote state use the shared plan/execute safety workflow.
        Type a command followed by --help where supported for more detail.
        """;

    public static async Task<int> RunAsync(string[] arguments)
    {
        var options = CliArguments.Parse(arguments);
        if (options.Error is not null) { Console.Error.WriteLine(options.Error); return 2; }
        var dataDirectory = options.Get("data-dir") ?? AppContext.BaseDirectory;
        var reporter = new ConsoleReporter(options.Has("json"));
        using var stopping = ConsoleCancellation.Create();
        using var host = RobotCommandRuntimeHost.Build(dataDirectory, RobotCommandRuntimeMode.Cli);
        var selection = host.Services.GetRequiredService<ISelectionWorkflow>();
        var map = host.Services.GetRequiredService<IMapViewWorkflow>();
        var scene = host.Services.GetRequiredService<IMapSceneObservationWorkflow>();
        var threeD = host.Services.GetRequiredService<IThreeDSceneWorkflow>();
        var ghosts = host.Services.GetRequiredService<IGhostUnitWorkflow>();
        var ghostProfiles = host.Services.GetRequiredService<IGhostProfileWorkflow>();
        var ghostAssets = host.Services.GetRequiredService<IGhostProfileAssetWorkflow>();
        var manual = host.Services.GetRequiredService<IManualControlWorkflow>();
        var operatorWorkflow = host.Services.GetRequiredService<IOperatorCommandWorkflow>();
        var autonomy = host.Services.GetRequiredService<IAutonomyWorkflow>();
        var behaviours = host.Services.GetRequiredService<IBehaviourWorkflow>();
        var geometry = host.Services.GetRequiredService<IGeometryWorkflow>();
        var flightMissions = host.Services.GetRequiredService<IFlightMissionWorkflow>();
        var fences = host.Services.GetRequiredService<IFenceWorkflow>();
        var parameters = host.Services.GetRequiredService<IPx4ParameterProfileWorkflow>();
        var sik = host.Services.GetRequiredService<ISikRadioWorkflow>();
        var reviewed = host.Services.GetRequiredService<IReviewedOperationWorkflow>();
        var evidence = host.Services.GetRequiredService<IEvidenceWorkflow>();
        var media = host.Services.GetRequiredService<IMediaWorkflow>();
        var mapLibrary = host.Services.GetRequiredService<IMapLibraryWorkflow>();
        var teamObserver = host.Services.GetRequiredService<ITeamObserverClientWorkflow>();
        var teams = host.Services.GetRequiredService<ITeamWorkflow>();
        var teamSelection = host.Services.GetRequiredService<ITeamSelectionWorkflow>();
        var targetScope = host.Services.GetRequiredService<IOperatorTargetScopeWorkflow>();
        var formation = host.Services.GetRequiredService<IFormationLockWorkflow>();
        var formationAuthoring = host.Services.GetRequiredService<IFormationAuthoringWorkflow>();
        var formationAssignment = host.Services.GetRequiredService<IFormationAssignmentWorkflow>();
        var preferences = host.Services.GetRequiredService<IApplicationPreferencesWorkflow>();
        var connections = host.Services.GetRequiredService<IConnectionManagementWorkflow>();
        var units = host.Services.GetRequiredService<IUnitObservationWorkflow>();
        var lifecycle = host.Services.GetRequiredService<IConnectionRuntimeLifecycle>();
        var watcher = new SessionWatcher(selection, teamSelection, map, scene, ghosts, manual, reporter);
        var operatorWatcher = new OperatorCommands.WorkflowWatcher(operatorWorkflow, reporter);
        operatorWorkflow.Changed += operatorWatcher.OnChanged;
        manual.NotifyHostActivity(true);

        try
        {
            await host.StartAsync(stopping.Token);
            await lifecycle.StartAsync(!options.Has("no-auto-connect"), null, stopping.Token);
            reporter.Info("Robot Command session started. Type 'help' for map, Ghost, manual-control, and operator commands.");
            while (!stopping.IsCancellationRequested)
            {
                string? line;
                try { line = await Console.In.ReadLineAsync(stopping.Token); }
                catch (OperationCanceledException) { break; }
                if (line is null || !await ExecuteAsync(line, selection, teamSelection, targetScope, map, scene, threeD, ghosts, ghostProfiles, ghostAssets, manual, operatorWorkflow, units, autonomy, behaviours, geometry, flightMissions, fences, parameters, sik, reviewed, evidence, media, mapLibrary, teamObserver, teams, formation, formationAuthoring, preferences, reporter, watcher, operatorWatcher, stopping.Token, connections: connections, formationAssignment: formationAssignment)) break;
            }
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception) { reporter.Error(exception.Message); return 1; }
        finally
        {
            manual.NotifyHostActivity(false);
            try
            {
                var current = manual.Session;
                if (current.OwnerKind == ManualControlOwnerKind.Cli)
                    await manual.ReleaseAsync(ManualControlOwnerKind.Cli, current.OwnerId ?? "session", "CLI session ended.", CancellationToken.None);
            }
            catch { /* Shutdown safety is best-effort; the hosted service also releases control. */ }
            operatorWorkflow.Changed -= operatorWatcher.OnChanged;
            watcher.Dispose();
            await lifecycle.DisposeAsync();
            await host.StopAsync(CancellationToken.None);
        }
    }

    internal static async Task<bool> ExecuteAsync(
        string input,
        ISelectionWorkflow selection,
        ITeamSelectionWorkflow teamSelection,
        IOperatorTargetScopeWorkflow targetScope,
        IMapViewWorkflow map,
        IMapSceneObservationWorkflow scene,
        IThreeDSceneWorkflow threeD,
        IGhostUnitWorkflow ghosts,
        IGhostProfileWorkflow ghostProfiles,
        IGhostProfileAssetWorkflow ghostAssets,
        IManualControlWorkflow manual,
        IOperatorCommandWorkflow operatorWorkflow,
        IUnitObservationWorkflow units,
        IAutonomyWorkflow autonomy,
        IBehaviourWorkflow behaviours,
        IGeometryWorkflow geometry,
        IFlightMissionWorkflow flightMissions,
        IFenceWorkflow fences,
        IPx4ParameterProfileWorkflow parameters,
        ISikRadioWorkflow sik,
        IReviewedOperationWorkflow reviewed,
        IEvidenceWorkflow evidence,
        IMediaWorkflow media,
        IMapLibraryWorkflow mapLibrary,
        ITeamObserverClientWorkflow teamObserver,
        ITeamWorkflow teams,
        IFormationLockWorkflow formation,
        IFormationAuthoringWorkflow formationAuthoring,
        IApplicationPreferencesWorkflow preferences,
        ConsoleReporter reporter,
        SessionWatcher watcher,
        OperatorCommands.WorkflowWatcher operatorWatcher,
        CancellationToken cancellationToken,
        ManualControlOwnerKind manualOwner = ManualControlOwnerKind.Cli,
        string manualOwnerId = "interactive-session",
        IConnectionManagementWorkflow? connections = null,
        IFormationAssignmentWorkflow? formationAssignment = null)
    {
        var tokens = CliTokenizer.Tokenize(input);
        if (tokens.Length == 0) return true;
        var group = tokens[0].ToLowerInvariant();
        var args = CliArguments.Parse(tokens[1..]);
        if (args.Error is not null) { reporter.Error(args.Error); return true; }
        try
        {
            switch (group)
            {
                case "help":
                    reporter.Info(HelpText);
                    return true;
                case "selection":
                    return await SelectionAsync(args, selection, reporter, cancellationToken);
                case "map":
                    if (args.Positionals.FirstOrDefault()?.Equals("package", StringComparison.OrdinalIgnoreCase) == true)
                        return await FinalWorkflowCommands.ExecuteAsync("map", args, evidence, media, mapLibrary, teamObserver, preferences, reporter, cancellationToken);
                    return await MapAsync(args, map, scene, reporter, watcher, cancellationToken);
                case "view":
                    return await ViewAsync(args, map, reporter, cancellationToken);
                case "3d":
                    return ThreeDStatus(args, threeD, reporter);
                case "formation":
                    return await FormationAuthoringAsync(args, formationAuthoring, reporter, cancellationToken);
                case "ghost":
                    return await GhostAsync(args, ghosts, ghostProfiles, ghostAssets, reporter, watcher, cancellationToken);
                case "connection":
                    return await ConnectionAsync(args, connections, reporter, cancellationToken);
                case "manual":
                    return await ManualAsync(args, manual, reporter, watcher, cancellationToken, manualOwner, manualOwnerId);
                case "operator":
                    return await OperatorCommands.ExecuteAsync(string.Join(' ', tokens.Skip(1)), operatorWorkflow, units, targetScope, reporter, operatorWatcher, cancellationToken);
                case "team":
                    if (IsLocalTeamCommand(args))
                        return await LocalTeamAsync(args, teams, teamSelection, formation, formationAssignment, reviewed, reporter, cancellationToken);
                    return await FinalWorkflowCommands.ExecuteAsync(group, args, evidence, media, mapLibrary, teamObserver, preferences, reporter, cancellationToken);
                case "mission" or "flight-mission" or "task" or "behaviour" or "geometry" or "autonomy" or "px4" or "ardupilot" or "fence" or "px4-fence" or "sik" or "plan":
                    return await Phase5Commands.ExecuteAsync(group, args, autonomy, behaviours, geometry, flightMissions, fences, parameters, sik, reviewed, reporter, cancellationToken);
                case "media" or "evidence" or "settings":
                    return await FinalWorkflowCommands.ExecuteAsync(group, args, evidence, media, mapLibrary, teamObserver, preferences, reporter, cancellationToken);
                case "status":
                    reporter.Event("session.status", new { Selection = selection.Current, TeamSelection = teamSelection.Current, TargetScope = targetScope.Current, Map = map.Current, Ghosts = ghosts.Ghosts.Count, Manual = manual.Session, QueuedCommands = operatorWorkflow.QueuedCommands.Count, ActiveCommands = operatorWorkflow.ActiveCommands.Count });
                    return true;
                case "stop": return false;
                default: reporter.Error("Unknown command. Type 'help' for commands."); return true;
            }
        }
        catch (Exception exception) { reporter.Error(exception.Message); return true; }
    }

    private static async Task<bool> FormationAuthoringAsync(CliArguments args, IFormationAuthoringWorkflow workflow, ConsoleReporter reporter, CancellationToken token)
    {
        var command = RequirePosition(args, 0, "Formation command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("formation.list", new { workflow.Formations, workflow.LibraryIssues }); break;
            case "show": reporter.Event("formation.show", workflow.TryGet(RequirePosition(args, 1, "A formation ID is required."), out var shown) && shown is not null ? shown : throw new KeyNotFoundException("Formation was not found.")); break;
            case "create": reporter.Event("formation.created", await workflow.CreateAsync(new(args.Required("name")), token)); break;
            case "rename": reporter.Event("formation.renamed", await workflow.RenameAsync(RequirePosition(args, 1, "A formation ID is required."), args.Required("name"), token)); break;
            case "delete": var deleteId = RequirePosition(args, 1, "A formation ID is required."); await workflow.RemoveAsync(deleteId, token); reporter.Event("formation.deleted", new { Id = deleteId }); break;
            case "member":
                var action = RequirePosition(args, 1, "A member command is required.").ToLowerInvariant();
                var id = RequirePosition(args, 2, "A formation ID is required.");
                if (action == "remove") { await workflow.RemoveMemberAsync(id, RequirePosition(args, 3, "A member ID is required."), token); reporter.Event("formation.member.removed", new { FormationId = id }); break; }
                var request = new FormationMemberRequest(args.Get("name"), ParseNumber(args, "x"), ParseNumber(args, "y"), ParseNumber(args, "z"));
                reporter.Event(action == "add" ? "formation.member.added" : "formation.member.updated", action == "add"
                    ? await workflow.AddMemberAsync(id, request, token)
                    : await workflow.UpdateMemberAsync(id, RequirePosition(args, 3, "A member ID is required."), request, token));
                break;
            default: throw new ArgumentException("Formation command must be list, show, create, rename, delete, or member.");
        }
        return true;
    }

    private static async Task<bool> ConnectionAsync(CliArguments args, IConnectionManagementWorkflow? workflow, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        if (workflow is null) { reporter.Error("Connection commands are unavailable in this host."); return true; }
        var action = args.Positionals.FirstOrDefault()?.ToLowerInvariant();
        var id = args.Positionals.Skip(1).FirstOrDefault();
        switch (action)
        {
            case "list": reporter.Event("connection.list", workflow.Connections); return true;
            case "connect":
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A connection ID is required.");
                await workflow.ConnectAsync(id, null, cancellationToken);
                reporter.Event("connection.connected", workflow.Connections.FirstOrDefault(item => item.Id == id));
                return true;
            case "disconnect":
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A connection ID is required.");
                await workflow.DisconnectAsync(id, cancellationToken);
                reporter.Event("connection.disconnected", workflow.Connections.FirstOrDefault(item => item.Id == id));
                return true;
            default:
                reporter.Error("Usage: connection list | connection connect <connection-id> | connection disconnect <connection-id>");
                return true;
        }
    }

    private static bool IsLocalTeamCommand(CliArguments args)
        => args.Positionals.FirstOrDefault()?.ToLowerInvariant() is "list" or "show" or "create" or "assign" or "clear" or "move" or "delete" or "select" or "deselect" or "selection" or "formation";

    private static async Task<bool> LocalTeamAsync(CliArguments args, ITeamWorkflow workflow, ITeamSelectionWorkflow selection, IFormationLockWorkflow formation, IFormationAssignmentWorkflow? assignment, IReviewedOperationWorkflow reviewed, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var command = RequirePosition(args, 0, "Team command is required.").ToLowerInvariant();
        switch (command)
        {
            case "formation":
                var formationCommand = RequirePosition(args, 1, "A formation command is required.").ToLowerInvariant();
                var formationTeamId = formationCommand == "status" && args.Positionals.Count < 3 ? null : ResolveTeamId(workflow, RequirePosition(args, 2, "A Team ID is required."));
                switch (formationCommand)
                {
                    case "lock": reporter.Event("team.formation", await formation.LockAsync(formationTeamId!, cancellationToken)); break;
                    case "unlock":
                        var unlockReason = args.Get("message") ?? (args.Positionals.Count > 3 ? string.Join(' ', args.Positionals.Skip(3)) : "Operator unlocked the formation.");
                        reporter.Event("team.formation", await formation.UnlockAsync(formationTeamId, unlockReason, cancellationToken));
                        break;
                    case "status": reporter.Event("team.formation", formation.Current); break;
                    case "goto":
                        {
                            var plan = await formation.PlanMoveToAsync(formationTeamId!, ParseNumber(args, "latitude"), ParseNumber(args, "longitude"), cancellationToken);
                            reporter.Event("team.formation.plan", plan);
                            if (args.Has("execute")) reporter.Event("team.formation.executed", await reviewed.ExecuteAsync(plan.Id, cancellationToken));
                            break;
                        }
                    case "altitude":
                        {
                            var plan = await formation.PlanChangeAltitudeAsync(formationTeamId!, ParseNumber(args, "agl"), cancellationToken);
                            reporter.Event("team.formation.plan", plan);
                            if (args.Has("execute")) reporter.Event("team.formation.executed", await reviewed.ExecuteAsync(plan.Id, cancellationToken));
                            break;
                        }
                    case "rotate":
                        {
                            var plan = await formation.PlanRotateAsync(formationTeamId!, ParseNumber(args, "degrees"), cancellationToken);
                            reporter.Event("team.formation.plan", plan);
                            if (args.Has("execute")) reporter.Event("team.formation.executed", await reviewed.ExecuteAsync(plan.Id, cancellationToken));
                            break;
                        }
                    case "scale":
                        {
                            var plan = await formation.PlanScaleAsync(formationTeamId!, ParseNumber(args, "percent"), cancellationToken);
                            reporter.Event("team.formation.plan", plan);
                            if (args.Has("execute")) reporter.Event("team.formation.executed", await reviewed.ExecuteAsync(plan.Id, cancellationToken));
                            break;
                        }
                    case "hold":
                        {
                            var plan = await formation.PlanHoldAsync(formationTeamId!, cancellationToken);
                            reporter.Event("team.formation.plan", plan);
                            if (args.Has("execute")) reporter.Event("team.formation.executed", await reviewed.ExecuteAsync(plan.Id, cancellationToken));
                            break;
                        }
                    case "assign":
                        if (assignment is null) throw new InvalidOperationException("Authored formation assignment is unavailable in this host.");
                        reporter.Event("team.formation.assignment", await assignment.AssignAsync(new(formationTeamId!, args.Required("formation"), ParseNumber(args, "heading")), cancellationToken));
                        break;
                    case "assignment":
                        if (assignment is null) throw new InvalidOperationException("Authored formation assignment is unavailable in this host.");
                        reporter.Event("team.formation.assignment", assignment.TryGet(formationTeamId!, out var assigned) && assigned is not null ? assigned : FormationAssignmentSnapshot.Unassigned);
                        break;
                    case "slot":
                        if (assignment is null) throw new InvalidOperationException("Authored formation assignment is unavailable in this host.");
                        reporter.Event("team.formation.assignment", await assignment.SwapSlotAsync(formationTeamId!, args.Required("unit"), args.Required("slot"), cancellationToken));
                        break;
                    case "clear":
                        if (assignment is null) throw new InvalidOperationException("Authored formation assignment is unavailable in this host.");
                        reporter.Event("team.formation.assignment", await assignment.ClearAsync(formationTeamId!, cancellationToken));
                        break;
                    case "enter":
                        if (assignment is null) throw new InvalidOperationException("Authored formation assignment is unavailable in this host.");
                        var entryPlan = await assignment.PlanEnterAsync(formationTeamId!, cancellationToken);
                        reporter.Event("team.formation.plan", entryPlan);
                        if (args.Has("execute")) reporter.Event("team.formation.executed", await reviewed.ExecuteAsync(entryPlan.Id, cancellationToken));
                        break;
                    default: throw new ArgumentException("Formation command must be lock, unlock, status, goto, altitude, rotate, scale, hold, assign, assignment, slot, clear, or enter.");
                }
                break;
            case "select":
                var selected = RequirePosition(args, 1, "A Team ID is required.");
                await selection.SelectAsync(selected, cancellationToken);
                reporter.Event("team-selection.changed", selection.Current);
                break;
            case "deselect":
                await selection.ClearAsync(cancellationToken);
                reporter.Event("team-selection.changed", selection.Current);
                break;
            case "selection":
                reporter.Event("team-selection", selection.Current);
                break;
            case "list":
                reporter.Event("unit-teams", workflow.Current);
                break;
            case "show":
                var shown = RequirePosition(args, 1, "A Team ID is required.");
                if (!workflow.TryGet(shown, out var team) || team is null) throw new KeyNotFoundException($"Team '{shown}' was not found.");
                reporter.Event("unit-team", team);
                break;
            case "create":
                reporter.Event("unit-team", await workflow.CreateAsync(args.Values("unit"), args.Get("name"), cancellationToken));
                break;
            case "assign":
                reporter.Event("unit-team", await workflow.AssignAsync(RequirePosition(args, 1, "A Team ID is required."), args.Values("unit"), cancellationToken));
                break;
            case "clear":
                await workflow.ClearMembershipAsync(args.Values("unit"), cancellationToken);
                reporter.Event("unit-teams", workflow.Current);
                break;
            case "move":
                if (!int.TryParse(args.Get("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
                    throw new ArgumentException("--index must be a non-negative integer.");
                await workflow.ReorderMemberAsync(RequirePosition(args, 1, "A Team ID is required."), args.Required("unit"), index, cancellationToken);
                reporter.Event("unit-teams", workflow.Current);
                break;
            case "delete":
                await workflow.DeleteAsync(RequirePosition(args, 1, "A Team ID is required."), cancellationToken);
                reporter.Event("unit-teams", workflow.Current);
                break;
            default:
                throw new ArgumentException("Team command must be list, show, create, assign, clear, move, delete, select, deselect, or selection.");
        }
        return true;
    }

    private static bool ThreeDStatus(CliArguments args, IThreeDSceneWorkflow workflow, ConsoleReporter reporter)
    {
        var command = args.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (command is not null and not "status")
        {
            reporter.Error("The supported 3D command is: 3d status.");
            return true;
        }

        var scene = workflow.Current;
        reporter.Event("3d.status", new
        {
            scene.Revision,
            scene.CapturedAt,
            scene.Origin,
            scene.Camera,
            PrimitiveCount = scene.Primitives.Count,
            LineCount = scene.Lines.Count,
            workflow.BackendPolicy,
            Renderer = scene.RendererStatus
        });
        return true;
    }

    // IDs remain canonical, but accepting an exact Team name keeps an
    // interactive headless session practical without weakening the workflow.
    private static string ResolveTeamId(ITeamWorkflow workflow, string idOrName)
    {
        if (workflow.TryGet(idOrName, out _)) return idOrName;
        var matches = workflow.Current.Teams.Where(team => string.Equals(team.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new KeyNotFoundException($"Team '{idOrName}' was not found."),
            _ => throw new InvalidOperationException($"Team name '{idOrName}' is ambiguous; use its Team ID.")
        };
    }

    private static async Task<bool> SelectionAsync(CliArguments args, ISelectionWorkflow workflow, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var command = RequirePosition(args, 0, "Selection command is required.").ToLowerInvariant();
        switch (command)
        {
            case "show": reporter.Event("selection", workflow.Current); break;
            case "set": await workflow.SetUnitsAsync(args.Values("unit"), args.Get("anchor"), cancellationToken); reporter.Event("selection", workflow.Current); break;
            case "clear": await workflow.ClearAsync(cancellationToken); reporter.Event("selection", workflow.Current); break;
            default: throw new ArgumentException("Selection command must be show, set, or clear.");
        }
        return true;
    }

    private static async Task<bool> MapAsync(CliArguments args, IMapViewWorkflow map, IMapSceneObservationWorkflow scene, ConsoleReporter reporter, SessionWatcher watcher, CancellationToken cancellationToken)
    {
        var command = RequirePosition(args, 0, "Map command is required.").ToLowerInvariant();
        switch (command)
        {
            case "scene":
                watcher.SceneWatch = !args.Has("off") && args.Has("watch"); reporter.Event("map.scene", scene.Current); break;
            case "viewport":
                var viewportCommand = RequirePosition(args, 1, "Viewport command is required.").ToLowerInvariant();
                if (viewportCommand == "show") reporter.Event("map.viewport", (object?)map.Current.Viewport ?? new { Status = "No viewport has been established yet." });
                else if (viewportCommand == "set")
                    await map.SetViewportAsync(new(ParseNumber(args, "longitude"), ParseNumber(args, "latitude"), ParseNumber(args, "resolution"), ParseNumber(args, "rotation", 0)), cancellationToken);
                else throw new ArgumentException("Viewport command must be show or set.");
                break;
            case "style":
                var styleCommand = RequirePosition(args, 1, "Style command is required.").ToLowerInvariant();
                if (styleCommand == "list") reporter.Event("map.styles", map.Current.Styles);
                else if (styleCommand == "select") await map.SelectStyleAsync(RequirePosition(args, 2, "A style ID is required."), cancellationToken);
                else throw new ArgumentException("Style command must be list or select.");
                break;
            case "overlay":
                var overlayCommand = RequirePosition(args, 1, "Overlay command is required.").ToLowerInvariant();
                await map.SetOverlayVisibleAsync(RequirePosition(args, 2, "An overlay name is required."), overlayCommand switch { "show" => true, "hide" => false, _ => throw new ArgumentException("Overlay command must be show or hide.") }, cancellationToken); break;
            case "jump":
                await (RequirePosition(args, 1, "Jump target is required.").Equals("operator", StringComparison.OrdinalIgnoreCase) ? map.JumpToOperatorAsync(cancellationToken) : map.JumpToSelectedAsync(cancellationToken)); break;
            case "follow":
                if (!RequirePosition(args, 1, "Follow target is required.").Equals("selected", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only 'map follow selected' is supported.");
                await map.FollowSelectedAsync(cancellationToken); break;
            case "stop-follow": await map.StopFollowingAsync(cancellationToken); break;
            default: throw new ArgumentException("Unknown map command.");
        }
        reporter.Event("map", map.Current);
        return true;
    }

    private static async Task<bool> ViewAsync(CliArguments args, IMapViewWorkflow map, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var command = RequirePosition(args, 0, "View command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("view.list", map.SavedViews); break;
            case "save": reporter.Event("view.saved", await map.SaveViewAsync(args.Required("name"), cancellationToken)); break;
            case "apply": await map.ApplySavedViewAsync(RequirePosition(args, 1, "A view ID is required."), cancellationToken); reporter.Event("view.applied", map.Current); break;
            case "remove": await map.RemoveSavedViewAsync(RequirePosition(args, 1, "A view ID is required."), cancellationToken); reporter.Event("view.removed", new { Id = RequirePosition(args, 1, "A view ID is required.") }); break;
            default: throw new ArgumentException("View command must be list, save, apply, or remove.");
        }
        return true;
    }

    private static async Task<bool> GhostAsync(CliArguments args, IGhostUnitWorkflow ghosts, IGhostProfileWorkflow profiles, IGhostProfileAssetWorkflow assets, ConsoleReporter reporter, SessionWatcher watcher, CancellationToken cancellationToken)
    {
        var command = RequirePosition(args, 0, "Ghost command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("ghost.list", ghosts.Ghosts); break;
            case "watch":
                watcher.GhostWatch = !args.Has("off");
                reporter.Event("ghost.list", ghosts.Ghosts);
                break;
            case "profile":
                var profileCommand = RequirePosition(args, 1, "Ghost profile command is required.").ToLowerInvariant();
                if (profileCommand == "asset")
                {
                    var assetCommand = RequirePosition(args, 2, "Ghost profile asset command is required.").ToLowerInvariant();
                    var assetProfileId = RequirePosition(args, 3, "A Ghost profile ID is required.");
                    if (assetCommand == "show") reporter.Event("ghost.profile.asset.show", assets.Find(assetProfileId));
                    else if (assetCommand == "upload") reporter.Event("ghost.profile.asset.uploaded", await assets.ImportAsync(assetProfileId, RequirePosition(args, 4, "An asset path is required."), cancellationToken));
                    else if (assetCommand == "remove") { await assets.RemoveAsync(assetProfileId, cancellationToken); reporter.Event("ghost.profile.asset.removed", new { Id = assetProfileId }); }
                    else throw new ArgumentException("Ghost profile asset command must be show, upload, or remove.");
                }
                else if (profileCommand == "list") reporter.Event("ghost.profile.list", profiles.Profiles);
                else if (profileCommand == "show")
                {
                    var profile = profiles.Find(RequirePosition(args, 2, "A Ghost profile ID is required."))
                        ?? throw new ArgumentException("The requested Ghost profile was not found.");
                    reporter.Event("ghost.profile.show", profile);
                }
                else if (profileCommand == "create")
                {
                    var created = await profiles.CreateAsync(new(
                        args.Required("name"),
                        GhostProfileCliParsing.Simulation(args, GhostProfileDefaults.Dracula.Simulation)), cancellationToken);
                    reporter.Event("ghost.profile.created", created);
                }
                else if (profileCommand == "update")
                {
                    var current = profiles.Find(RequirePosition(args, 2, "A Ghost profile ID is required."))
                        ?? throw new ArgumentException("The requested Ghost profile was not found.");
                    var updated = await profiles.UpdateAsync(current.Id, new(
                        args.Get("name") ?? current.Name,
                        GhostProfileCliParsing.Simulation(args, current.Simulation)), cancellationToken);
                    reporter.Event("ghost.profile.updated", updated);
                }
                else if (profileCommand == "delete")
                {
                    var id = RequirePosition(args, 2, "A Ghost profile ID is required.");
                    await profiles.DeleteAsync(id, cancellationToken);
                    reporter.Event("ghost.profile.deleted", new { Id = id });
                }
                else throw new ArgumentException("Ghost profile command must be list, show, create, update, delete, or asset.");
                break;
            case "create":
                var profileId = args.Get("profile") ?? throw new ArgumentException("--profile is required. Choose a profile with 'ghost profile list'.");
                reporter.Event("ghost.created", await ghosts.CreateAsync(new(profileId, args.Int("count", 1) ?? 1, OptionalNumber(args, "latitude"), OptionalNumber(args, "longitude"), OptionalNumber(args, "heading") ?? 90), cancellationToken));
                break;
            case "delete": await ghosts.DeleteAsync(RequirePosition(args, 1, "A Ghost unit ID is required."), cancellationToken); reporter.Event("ghost.deleted", new { UnitId = RequirePosition(args, 1, "A Ghost unit ID is required.") }); break;
            default: throw new ArgumentException("Ghost command must be profile, list, watch, create, or delete.");
        }
        return true;
    }

    private static async Task<bool> ManualAsync(
        CliArguments args,
        IManualControlWorkflow manual,
        ConsoleReporter reporter,
        SessionWatcher watcher,
        CancellationToken cancellationToken,
        ManualControlOwnerKind ownerKind,
        string ownerId)
    {
        var command = RequirePosition(args, 0, "Manual command is required.").ToLowerInvariant();
        switch (command)
        {
            case "devices": reporter.Event("manual.devices", manual.Devices); break;
            case "select-device": await manual.SelectDeviceAsync(RequirePosition(args, 1, "A device ID is required."), cancellationToken); reporter.Event("manual.devices", manual.Devices); break;
            case "profile":
                var profileCommand = RequirePosition(args, 1, "Profile command is required.").ToLowerInvariant();
                if (profileCommand == "show") reporter.Event("manual.profile", manual.Profile);
                else if (profileCommand == "set")
                {
                    var current = manual.Profile;
                    await manual.SaveProfileAsync(current with { DeadZone = OptionalNumber(args, "dead-zone") ?? current.DeadZone, Expo = OptionalNumber(args, "expo") ?? current.Expo, MaximumHorizontalSpeedMetresPerSecond = OptionalNumber(args, "horizontal-speed") ?? current.MaximumHorizontalSpeedMetresPerSecond, MaximumVerticalSpeedMetresPerSecond = OptionalNumber(args, "vertical-speed") ?? current.MaximumVerticalSpeedMetresPerSecond, MaximumYawRateDegreesPerSecond = OptionalNumber(args, "yaw-rate") ?? current.MaximumYawRateDegreesPerSecond, TakeoffAltitudeAglMetres = OptionalNumber(args, "takeoff-agl") ?? current.TakeoffAltitudeAglMetres }, cancellationToken);
                    reporter.Event("manual.profile", manual.Profile);
                }
                else throw new ArgumentException("Profile command must be show or set.");
                break;
            case "take": reporter.Event("manual.take", new { Accepted = await manual.TakeControlAsync(args.Required("unit"), ownerKind, ownerId, cancellationToken), Session = manual.Session }); break;
            case "release": await manual.ReleaseAsync(ownerKind, ownerId, args.Positionals.Count > 1 ? string.Join(' ', args.Positionals.Skip(1)) : "CLI operator released manual control.", cancellationToken); reporter.Event("manual.release", manual.Session); break;
            case "status": reporter.Event("manual.status", new { Session = manual.Session, Reading = manual.LatestReading }); break;
            case "watch": watcher.ManualWatch = !args.Has("off"); reporter.Event("manual.status", new { Session = manual.Session, Reading = manual.LatestReading }); break;
            default: throw new ArgumentException("Manual command must be devices, select-device, profile, take, release, status, or watch.");
        }
        return true;
    }

    private static string RequirePosition(CliArguments args, int index, string message) => args.Positionals.Count > index ? args.Positionals[index] : throw new ArgumentException(message);
    private static double ParseNumber(CliArguments args, string name, double? fallback = null) => OptionalNumber(args, name) ?? fallback ?? throw new ArgumentException($"--{name} is required.");
    private static double? OptionalNumber(CliArguments args, string name) => args.Get(name) is { } value && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : args.Get(name) is null ? null : throw new ArgumentException($"--{name} must be a finite number.");

    internal sealed class SessionWatcher : IDisposable
    {
        private readonly ISelectionWorkflow _selection; private readonly ITeamSelectionWorkflow _teamSelection; private readonly IMapViewWorkflow _map; private readonly IMapSceneObservationWorkflow _scene; private readonly IGhostUnitWorkflow _ghosts; private readonly IManualControlWorkflow _manual; private readonly ConsoleReporter _reporter;
        public bool SceneWatch { get; set; }
        public bool GhostWatch { get; set; }
        public bool ManualWatch { get; set; }
        public SessionWatcher(ISelectionWorkflow selection, ITeamSelectionWorkflow teamSelection, IMapViewWorkflow map, IMapSceneObservationWorkflow scene, IGhostUnitWorkflow ghosts, IManualControlWorkflow manual, ConsoleReporter reporter)
        {
            _selection = selection; _teamSelection = teamSelection; _map = map; _scene = scene; _ghosts = ghosts; _manual = manual; _reporter = reporter;
            _selection.Changed += SelectionChanged; _teamSelection.Changed += TeamSelectionChanged; _map.Changed += MapChanged; _scene.Changed += SceneChanged; _ghosts.Changed += GhostChanged; _manual.Changed += ManualChanged;
        }
        private void SelectionChanged(object? sender, EventArgs eventArgs) => _reporter.Event("selection.changed", _selection.Current);
        private void TeamSelectionChanged(object? sender, EventArgs eventArgs) => _reporter.Event("team-selection.changed", _teamSelection.Current);
        private void MapChanged(object? sender, EventArgs eventArgs) { if (SceneWatch) _reporter.Event("map.changed", _map.Current); }
        private void SceneChanged(object? sender, EventArgs eventArgs) { if (SceneWatch) _reporter.Event("map.scene.changed", _scene.Current); }
        private void GhostChanged(object? sender, EventArgs eventArgs) { if (GhostWatch) _reporter.Event("ghost.changed", _ghosts.Ghosts); }
        private void ManualChanged(object? sender, EventArgs eventArgs) { if (ManualWatch) _reporter.Event("manual.changed", new { Session = _manual.Session, Reading = _manual.LatestReading }); }
        public void Dispose() { _selection.Changed -= SelectionChanged; _teamSelection.Changed -= TeamSelectionChanged; _map.Changed -= MapChanged; _scene.Changed -= SceneChanged; _ghosts.Changed -= GhostChanged; _manual.Changed -= ManualChanged; }
    }
}
