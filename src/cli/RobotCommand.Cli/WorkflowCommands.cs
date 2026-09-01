using System.Globalization;
using RobotCommand.Core;

namespace RobotCommand.Cli;

internal static class WorkflowCommands
{
    public static async Task<bool> ExecuteAsync(string group, CliArguments args,
        IAutonomyWorkflow autonomy, IBehaviourWorkflow behaviours, IGeometryWorkflow geometry,
        IFlightMissionWorkflow flightMissions, IFenceWorkflow fences,
        IMavlinkParameterProfileWorkflow parameters, ISikRadioWorkflow sik,
        IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        switch (group)
        {
            case "mission": return await DocumentAsync("mission", args, autonomy, plans, reporter, token);
            case "flight-mission": return await FlightMissionAsync(args, flightMissions, plans, reporter, token);
            case "fence" or "px4-fence": return await FenceAsync(args, fences, plans, reporter, token);
            case "task": return await DocumentAsync("task", args, autonomy, plans, reporter, token);
            case "behaviour": return await BehaviourAsync(args, behaviours, plans, reporter, token);
            case "geometry": return await GeometryAsync(args, geometry, plans, reporter, token);
            case "autonomy": return await BundleAsync(args, autonomy, plans, reporter, token);
            case "px4" or "ardupilot": return await ParametersAsync(group, args, parameters, plans, reporter, token);
            case "sik": return await SikAsync(args, sik, plans, reporter, token);
            case "plan": return await PlanAsync(args, plans, reporter, token);
            default: return false;
        }
    }

    public static string Help => "fence list|show|create-from-zone|validate|import|export|delete|upload|download|clear|watch; flight-mission list|show|create|rename|duplicate|delete|import|export|validate|fence|step (including add-camera-action)|survey|terrain|set-end-action|upload|download|start|pause|continue|resume|retain|remove|watch.";

    private static async Task<bool> FlightMissionAsync(CliArguments args, IFlightMissionWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, "Flight-mission command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("flight-mission.list", workflow.Missions); break;
            case "show": reporter.Event("flight-mission.show", workflow.TryGet(Required(args, 1, "A mission ID is required."), out var shown) ? shown! : throw new KeyNotFoundException("Flight mission was not found.")); break;
            case "create": reporter.Event("flight-mission.create", await workflow.CreateAsync(new(args.Get("name") ?? "New mission", double.TryParse(args.Get("altitude"), out var altitude) ? altitude : 20, args.Get("id")), token)); break;
            case "rename": await workflow.RenameAsync(Required(args, 1, "A mission ID is required."), args.Required("name"), token); reporter.Event("flight-mission.rename", new { Id = Required(args, 1, "A mission ID is required."), Name = args.Required("name") }); break;
            case "duplicate": reporter.Event("flight-mission.duplicate", await workflow.DuplicateAsync(Required(args, 1, "A mission ID is required."), args.Get("name"), token)); break;
            case "delete": await workflow.DeleteAsync(Required(args, 1, "A mission ID is required."), token); reporter.Event("flight-mission.delete", new { Id = Required(args, 1, "A mission ID is required.") }); break;
            case "import": reporter.Event("flight-mission.import", await workflow.ImportAsync(args.Required("path"), args.Has("replace"), token)); break;
            case "export": await workflow.ExportAsync(Required(args, 1, "A mission ID is required."), args.Required("path"), token); reporter.Event("flight-mission.export", new { Id = Required(args, 1, "A mission ID is required."), Path = args.Required("path") }); break;
            case "validate": reporter.Event("flight-mission.validate", await workflow.ValidateAsync(Required(args, 1, "A mission ID is required."), args.Get("vehicle"), token)); break;
            case "set-end-action":
                var endMission = Required(args, 1, "A mission ID is required.");
                var endAction = args.Required("action").Equals("rtl", StringComparison.OrdinalIgnoreCase) ? FlightMissionEndAction.ReturnToLaunch : args.Required("action").Equals("hold", StringComparison.OrdinalIgnoreCase) ? FlightMissionEndAction.Hold : throw new ArgumentException("Mission end action must be hold or rtl.");
                reporter.Event("flight-mission.set-end-action", await workflow.SetEndActionAsync(endMission, endAction, token));
                break;
            case "fence":
                var fenceAction = Required(args, 1, "A fence action is required.").ToLowerInvariant();
                var fenceMissionId = Required(args, 2, "A mission ID is required.");
                if (!workflow.TryGet(fenceMissionId, out var fenceMission) || fenceMission is null) throw new KeyNotFoundException("Flight mission was not found.");
                if (fenceAction == "set")
                    reporter.Event("flight-mission.fence", await workflow.SetTargetAssignmentAsync(fenceMissionId, new FlightMissionTargetAssignment("MAVLink", "Multicopter", args.Required("fence")), token));
                else if (fenceAction == "clear")
                    reporter.Event("flight-mission.fence", await workflow.SetTargetAssignmentAsync(fenceMissionId, null, token));
                else throw new ArgumentException("Flight-mission fence action must be set or clear.");
                break;
            case "step":
                var action = Required(args, 1, "A step action is required.").ToLowerInvariant(); var id = Required(args, 2, "A mission ID is required.");
                if (action == "add-camera-action")
                {
                    reporter.Event("flight-mission.camera-action", await AddCameraActionAsync(workflow, id, args, token));
                    break;
                }
                var step = action switch
                {
                    "add-takeoff" => await workflow.AddTakeoffAsync(id, token),
                    "add-poi" or "add-route" => await workflow.AddGeometryAsync(id, args.Required("geometry"), token),
                    "add-survey" => await workflow.AddSurveyAsync(id, args.Required("geometry"), new(double.TryParse(args.Get("spacing"), out var spacing) ? spacing : 25, double.TryParse(args.Get("bearing"), out var bearing) ? bearing : 0, double.TryParse(args.Get("turnaround"), out var turnaround) ? turnaround : 0, args.Has("reverse-entry")), token),
                    "add-corridor" => await workflow.AddCorridorAsync(id, args.Required("geometry"), new(
                        double.TryParse(args.Get("width"), out var width) ? width : 50,
                        double.TryParse(args.Get("spacing"), out var corridorSpacing) ? corridorSpacing : 25,
                        double.TryParse(args.Get("turnaround"), out var corridorTurnaround) ? corridorTurnaround : 0,
                        args.Has("reverse"),
                        args.Get("entry")?.Equals("right", StringComparison.OrdinalIgnoreCase) == true ? FlightMissionCorridorEntrySide.Right : FlightMissionCorridorEntrySide.Left,
                        double.TryParse(args.Get("front-lap"), out var frontLap) ? frontLap : 70,
                        double.TryParse(args.Get("side-lap"), out var sideLap) ? sideLap : 70,
                        args.Has("images-in-turnarounds"),
                        new FlightMissionCameraIntent(args.Get("mode") ?? "Photo", double.TryParse(args.Get("distance"), out var corridorDistance) ? corridorDistance : null, double.TryParse(args.Get("interval"), out var corridorInterval) ? corridorInterval : null, args.Get("camera"), args.Get("notes"))), token),
                    "add-loiter" => await workflow.AddTimedLoiterAsync(id, args.Required("geometry"), double.Parse(args.Required("seconds"), CultureInfo.InvariantCulture), token),
                    "add-camera-intent" => await workflow.AddCameraIntentAsync(id, new(args.Get("mode") ?? "Photo", double.TryParse(args.Get("distance"), out var distance) ? distance : null, double.TryParse(args.Get("interval"), out var interval) ? interval : null, args.Get("camera"), args.Get("notes")), token),
                    "add-rtl" => await workflow.AddReturnToLaunchAsync(id, token),
                    "add-land" => await workflow.AddLandAsync(id, token),
                    "remove" => await workflow.RemoveStepAsync(id, args.Required("step"), token),
                    "move" => await workflow.MoveStepAsync(id, args.Required("step"), int.Parse(args.Required("index"), CultureInfo.InvariantCulture), token),
                    "set-speed" => await SetSpeedAsync(workflow, id, args, token),
                    "set-altitude" => await SetAltitudeAsync(workflow, id, args, token),
                    "set-terrain" => await SetTerrainAsync(workflow, id, args, token),
                    _ => throw new ArgumentException("Flight-mission step action is invalid.")
                }; reporter.Event("flight-mission.step", step); break;
            case "survey" or "terrain": reporter.Event($"flight-mission.{command}", await workflow.PreviewAsync(Required(args, 1, "A mission ID is required."), args.Get("vehicle"), token)); break;
            case "upload": await PlanOrExecuteAsync(await workflow.PlanUploadAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token, args.Has("allow-terrain-fallback")), args, reporter, plans, token); break;
            case "download": await PlanOrExecuteAsync(await workflow.PlanDownloadAsync(args.Required("connection"), args.Required("vehicle"), args.Get("name") ?? "Downloaded mission", token), args, reporter, plans, token); break;
            case "start": await PlanOrExecuteAsync(await workflow.PlanStartAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token, args.Has("allow-terrain-fallback")), args, reporter, plans, token); break;
            case "pause": await PlanOrExecuteAsync(await workflow.PlanPauseAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "continue": await PlanOrExecuteAsync(await workflow.PlanContinueAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "resume": await PlanOrExecuteAsync(await workflow.PlanResumeAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "retain": await PlanOrExecuteAsync(await workflow.PlanRetainAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "remove": await PlanOrExecuteAsync(await workflow.PlanRemoveAsync(Required(args, 1, "A mission ID is required."), args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "watch":
                reporter.Event("flight-mission.watch", new { Missions = workflow.Missions, Executions = workflow.Executions });
                break;
            default: throw new ArgumentException("Flight-mission command is invalid.");
        }
        return true;
    }

    private static Task<FlightMissionSnapshot> SetSpeedAsync(IFlightMissionWorkflow workflow, string missionId, CliArguments args, CancellationToken token)
    {
        var speed = double.Parse(args.Required("speed"), CultureInfo.InvariantCulture);
        if (args.Get("step") is not { } stepId) return workflow.SetCruiseSpeedAsync(missionId, speed, token);
        var step = MissionStep(workflow, missionId, stepId);
        return workflow.SetStepOverridesAsync(missionId, stepId, step.RelativeAltitudeMetres, speed, step.TerrainFollowing, token);
    }

    private static Task<FlightMissionSnapshot> SetAltitudeAsync(IFlightMissionWorkflow workflow, string missionId, CliArguments args, CancellationToken token)
    {
        var altitude = double.Parse(args.Required("altitude"), CultureInfo.InvariantCulture);
        if (args.Get("step") is not { } stepId) return workflow.SetAltitudeAsync(missionId, altitude, token);
        var step = MissionStep(workflow, missionId, stepId);
        return workflow.SetStepOverridesAsync(missionId, stepId, altitude, step.CruiseSpeedMetresPerSecond, step.TerrainFollowing, token);
    }

    private static Task<FlightMissionSnapshot> SetTerrainAsync(IFlightMissionWorkflow workflow, string missionId, CliArguments args, CancellationToken token)
    {
        var stepId = args.Required("step");
        var step = MissionStep(workflow, missionId, stepId);
        return workflow.SetStepOverridesAsync(missionId, stepId, step.RelativeAltitudeMetres, step.CruiseSpeedMetresPerSecond, args.Has("enabled"), token);
    }

    private static FlightMissionStep MissionStep(IFlightMissionWorkflow workflow, string missionId, string stepId)
        => workflow.TryGet(missionId, out var mission) && mission?.Steps.FirstOrDefault(item => item.Id == stepId) is { } step
            ? step
            : throw new KeyNotFoundException("Mission step was not found.");

    private static async Task<FlightMissionSnapshot> AddCameraActionAsync(
        IFlightMissionWorkflow workflow,
        string missionId,
        CliArguments args,
        CancellationToken token)
    {
        var action = ParseCameraAction(args);
        if (args.Get("step") is { } stepId)
        {
            if (!workflow.TryGet(missionId, out var mission) || mission is null)
                throw new KeyNotFoundException("Flight mission was not found.");
            var step = mission.Steps.FirstOrDefault(item => item.Id == stepId)
                ?? throw new KeyNotFoundException("Mission step was not found.");
            var existing = step.CameraIntent?.Actions ?? [];
            return await workflow.SetStepCameraActionsAsync(missionId, stepId, existing.Append(action).ToArray(), token);
        }

        if (!workflow.TryGet(missionId, out var selected) || selected is null)
            throw new KeyNotFoundException("Flight mission was not found.");
        var missionActions = selected.CameraIntent?.Actions ?? [];
        return await workflow.SetMissionCameraActionsAsync(missionId, missionActions.Append(action).ToArray(), token);
    }

    private static FlightMissionCameraAction ParseCameraAction(CliArguments args)
    {
        var kind = args.Required("action").ToLowerInvariant();
        var camera = args.Get("camera");
        byte? cameraId = byte.TryParse(args.Get("camera-id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCameraId)
            ? parsedCameraId
            : null;
        return kind switch
        {
            "photo" or "photo-once" => FlightMissionCameraAction.PhotoOnce(camera, cameraId),
            "photo-by-time" => FlightMissionCameraAction.PhotoByTime(RequiredNumber(args, "interval"), camera, cameraId),
            "photo-by-distance" => FlightMissionCameraAction.PhotoByDistance(RequiredNumber(args, "distance"), camera, cameraId),
            "stop-photos" => FlightMissionCameraAction.StopPhotos(camera, cameraId),
            "start-video" => FlightMissionCameraAction.StartVideo(camera, cameraId),
            "stop-video" => FlightMissionCameraAction.StopVideo(camera, cameraId),
            "mode-photo" => FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Photo, camera, cameraId),
            "mode-video" => FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Video, camera, cameraId),
            "roi" or "region-of-interest" => FlightMissionCameraAction.SetRegionOfInterest(
                new FlightMissionCoordinate(RequiredNumber(args, "latitude"), RequiredNumber(args, "longitude")), camera, cameraId),
            "gimbal" => FlightMissionCameraAction.SetGimbal(
                Number(args, "pitch"), Number(args, "yaw"), Number(args, "roll"),
                args.Get("frame")?.Equals("earth", StringComparison.OrdinalIgnoreCase) == true
                    ? FlightMissionGimbalFrame.Earth
                    : FlightMissionGimbalFrame.Vehicle,
                camera, cameraId),
            _ => throw new ArgumentException("Camera action must be photo, photo-by-time, photo-by-distance, stop-photos, start-video, stop-video, mode-photo, mode-video, roi, or gimbal.")
        };
    }

    private static double RequiredNumber(CliArguments args, string name)
        => double.Parse(args.Required(name), CultureInfo.InvariantCulture);

    private static double? Number(CliArguments args, string name)
        => double.TryParse(args.Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static async Task<bool> FenceAsync(CliArguments args, IFenceWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, "Fence command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("fence.list", new { Fences = workflow.Fences, Targets = workflow.Targets }); break;
            case "show": reporter.Event("fence.show", workflow.Fences.FirstOrDefault(item => item.Document.FenceId == Required(args, 1, "A fence ID is required.")) ?? throw new KeyNotFoundException("Fence was not found.")); break;
            case "create-from-zone": reporter.Event("fence.create", await workflow.CreateFromZoneAsync(args.Get("zone") ?? args.Required("geometry"), args.Get("name"), args.Get("kind")?.Equals("exclusion", StringComparison.OrdinalIgnoreCase) == true ? FenceKind.Exclusion : FenceKind.Inclusion, token)); break;
            case "validate": reporter.Event("fence.validate", await workflow.ValidateAsync(Required(args, 1, "A fence ID is required."), args.Get("vehicle"), token)); break;
            case "delete": await workflow.DeleteAsync(Required(args, 1, "A fence ID is required."), token); reporter.Event("fence.delete", new { Id = Required(args, 1, "A fence ID is required.") }); break;
            case "import": reporter.Event("fence.import", await workflow.ImportAsync(args.Required("path"), args.Has("replace"), token)); break;
            case "export": await workflow.ExportAsync(Required(args, 1, "A fence ID is required."), args.Required("path"), token); reporter.Event("fence.export", new { Path = args.Required("path") }); break;
            case "upload": await PlanOrExecuteAsync(await workflow.PlanUploadAsync(Required(args, 1, "A fence ID is required."), args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "download": await PlanOrExecuteAsync(await workflow.PlanDownloadAsync(args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "clear": await PlanOrExecuteAsync(await workflow.PlanClearAsync(args.Required("connection"), args.Required("vehicle"), token), args, reporter, plans, token); break;
            case "watch": reporter.Event("fence.watch", new { Fences = workflow.Fences, Targets = workflow.Targets }); break;
            default: throw new ArgumentException("Fence command is invalid.");
        }
        return true;
    }

    private static async Task<bool> DocumentAsync(string type, CliArguments args, IAutonomyWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, $"{type} command is required.").ToLowerInvariant();
        var documents = type == "mission" ? workflow.Current.Missions : workflow.Current.Tasks;
        switch (command)
        {
            case "list": reporter.Event($"{type}.list", documents); break;
            case "show": reporter.Event($"{type}.show", documents.FirstOrDefault(item => item.Id.Equals(Required(args, 1, "An ID is required."), StringComparison.Ordinal)) ?? throw new KeyNotFoundException($"{type} was not found.")); break;
            case "import": reporter.Event($"{type}.import", type == "mission" ? await workflow.ImportMissionAsync(args.Required("path"), token) : await workflow.ImportTaskAsync(args.Required("path"), token)); break;
            case "export": if (type == "mission") await workflow.ExportMissionAsync(Required(args, 1, "An ID is required."), args.Required("path"), token); else await workflow.ExportTaskAsync(Required(args, 1, "An ID is required."), args.Required("path"), token); reporter.Event($"{type}.export", new { Path = args.Required("path") }); break;
            case "validate": reporter.Event($"{type}.validate", type == "mission" ? await workflow.ValidateMissionAsync(Required(args, 1, "An ID is required."), token) : await workflow.ValidateTaskAsync(Required(args, 1, "An ID is required."), token)); break;
            case "refresh": await workflow.RefreshAsync(args.Get("connection"), token); reporter.Event($"{type}.refresh", workflow.Current); break;
            case "publish": await PlanOrExecuteAsync(type == "mission" ? await workflow.PlanMissionPublishAsync(Required(args, 1, "An ID is required."), token) : await workflow.PlanTaskPublishAsync(Required(args, 1, "An ID is required."), token), args, reporter, plans, token); break;
            case "assign" when type == "task": await PlanOrExecuteAsync(await workflow.PlanTaskAssignmentAsync(new(Required(args, 1, "A task ID is required."), args.Required("vehicle")), token), args, reporter, plans, token); break;
            case "command":
                var id = Required(args, 1, $"A {type} ID is required.");
                var requestConnection = args.Required("connection");
                var action = args.Required("action");
                var plan = type == "mission" ? await workflow.PlanMissionCommandAsync(new(id, requestConnection, action, args.Get("reason")), token) : await workflow.PlanTaskCommandAsync(new(id, requestConnection, action, args.Get("reason")), token);
                await PlanOrExecuteAsync(plan, args, reporter, plans, token); break;
            default: throw new ArgumentException(type == "mission" ? "Mission command must be list, show, import, export, validate, refresh, publish, or command." : "Task command must be list, show, import, export, validate, refresh, publish, assign, or command.");
        }
        return true;
    }

    private static async Task<bool> BehaviourAsync(CliArguments args, IBehaviourWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, "Behaviour command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("behaviour.list", workflow.LocalPackages); break;
            case "show": reporter.Event("behaviour.show", workflow.LocalPackages.FirstOrDefault(item => item.Id.Equals(Required(args, 1, "A package ID is required."), StringComparison.Ordinal)) ?? throw new KeyNotFoundException("Behaviour package was not found.")); break;
            case "import": reporter.Event("behaviour.import", await workflow.ImportAsync(args.Required("path"), token)); break;
            case "export": await workflow.ExportAsync(Required(args, 1, "A package ID is required."), args.Required("path"), token); reporter.Event("behaviour.export", new { Path = args.Required("path") }); break;
            case "validate": reporter.Event("behaviour.validate", await workflow.ValidateAsync(Required(args, 1, "A package ID is required."), token)); break;
            case "refresh": await workflow.RefreshAsync(args.Required("connection"), args.Has("local"), args.Has("remote"), token); reporter.Event("behaviour.refresh", workflow.LocalPackages); break;
            case "deploy": await PlanOrExecuteAsync(await workflow.PlanDeployAsync(args.Required("connection"), Required(args, 1, "A package ID is required."), args.Get("operation") ?? "install", token), args, reporter, plans, token); break;
            case "remove": await PlanOrExecuteAsync(await workflow.PlanRemoveAsync(args.Required("connection"), Required(args, 1, "A package ID is required."), token), args, reporter, plans, token); break;
            case "bindings":
                var package = Required(args, 1, "A package ID is required."); var connection = args.Required("connection");
                var bindingAction = args.Get("action") ?? "inspect";
                if (bindingAction == "inspect") reporter.Event("behaviour.bindings", await workflow.InspectBindingsAsync(connection, package, token));
                else await PlanOrExecuteAsync(bindingAction == "clear" ? await workflow.PlanClearBindingAsync(connection, package, args.Required("slot"), token) : await workflow.PlanSetBindingAsync(connection, package, args.Required("slot"), args.Required("geometry"), token), args, reporter, plans, token);
                break;
            default: throw new ArgumentException("Behaviour command is invalid.");
        }
        return true;
    }

    private static async Task<bool> GeometryAsync(CliArguments args, IGeometryWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, "Geometry command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("geometry.list", new { Local = workflow.LocalDocuments, Groups = workflow.Groups, Issues = workflow.LibraryIssues }); break;
            case "show": reporter.Event("geometry.show", workflow.LocalDocuments.FirstOrDefault(item => item.Id.Equals(Required(args, 1, "A geometry ID is required."), StringComparison.Ordinal)) ?? throw new KeyNotFoundException("Geometry was not found.")); break;
            case "import": reporter.Event("geometry.import", await workflow.ImportAsync(args.Required("path"), args.Has("replace"), token)); break;
            case "export": await workflow.ExportAsync(Required(args, 1, "A geometry ID is required."), args.Required("path"), token); reporter.Event("geometry.export", new { Path = args.Required("path") }); break;
            case "set": await GeometrySetAsync(args, workflow, reporter, token); break;
            case "group": await GeometryGroupAsync(args, workflow, reporter, token); break;
            case "remove" or "delete": await workflow.RemoveAsync(Required(args, 1, "A geometry ID is required."), token); reporter.Event("geometry.remove", new { Id = Required(args, 1, "A geometry ID is required.") }); break;
            default: throw new ArgumentException("Geometry command must be list, show, import, export, set, group, or delete.");
        }
        return true;
    }

    private static async Task GeometrySetAsync(CliArguments args, IGeometryWorkflow workflow, ConsoleReporter reporter, CancellationToken token)
    {
        var action = Required(args, 1, "Geometry-set command must be import or export.").ToLowerInvariant();
        switch (action)
        {
            case "import":
                await workflow.ImportSetAsync(args.Required("path"), args.Has("replace"), token);
                reporter.Event("geometry.set.import", new { Path = args.Required("path") });
                break;
            case "export":
                var ids = args.Values("geometry");
                if (ids.Count == 0) throw new ArgumentException("At least one --geometry ID is required to export a geometry set.");
                await workflow.ExportSetAsync(new(args.Required("path"), args.Get("name") ?? "Geometry set", ids), token);
                reporter.Event("geometry.set.export", new { Path = args.Required("path"), GeometryCount = ids.Count });
                break;
            default: throw new ArgumentException("Geometry-set command must be import or export.");
        }
    }

    private static async Task GeometryGroupAsync(CliArguments args, IGeometryWorkflow workflow, ConsoleReporter reporter, CancellationToken token)
    {
        var action = Required(args, 1, "Geometry-group command is required.").ToLowerInvariant();
        var name = args.Get("name") ?? (args.Positionals.Count > 2 ? args.Positionals[2] : null);
        switch (action)
        {
            case "list": reporter.Event("geometry.group.list", workflow.Groups); break;
            case "create": await workflow.CreateGroupAsync(name ?? throw new ArgumentException("A group name is required."), token); reporter.Event("geometry.group.create", new { Name = name }); break;
            case "rename": await workflow.RenameGroupAsync(name ?? throw new ArgumentException("A group name is required."), args.Required("new-name"), token); reporter.Event("geometry.group.rename", new { Name = name }); break;
            case "delete": await workflow.DeleteGroupAsync(name ?? throw new ArgumentException("A group name is required."), token); reporter.Event("geometry.group.delete", new { Name = name }); break;
            case "assign": await workflow.AssignGroupAsync(name ?? throw new ArgumentException("A group name is required."), RequireGeometryIds(args), token); reporter.Event("geometry.group.assign", new { Name = name }); break;
            case "remove": await workflow.RemoveFromGroupAsync(name ?? throw new ArgumentException("A group name is required."), RequireGeometryIds(args), token); reporter.Event("geometry.group.remove", new { Name = name }); break;
            default: throw new ArgumentException("Geometry-group command must be list, create, rename, delete, assign, or remove.");
        }
    }

    private static IReadOnlyList<string> RequireGeometryIds(CliArguments args)
    {
        var ids = args.Values("geometry");
        return ids.Count == 0 ? throw new ArgumentException("At least one --geometry ID is required.") : ids;
    }

    private static async Task<bool> BundleAsync(CliArguments args, IAutonomyWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        if (!Required(args, 0, "Autonomy command is required.").Equals("bundle", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only 'autonomy bundle' is supported.");
        var command = Required(args, 1, "Bundle command is required.").ToLowerInvariant();
        switch (command)
        {
            case "export": reporter.Event("autonomy.bundle.export", await workflow.ExportBundleAsync(args.Required("path"), args.Values("mission"), args.Values("task"), args.Values("behaviour"), args.Values("geometry"), token)); break;
            case "inspect": reporter.Event("autonomy.bundle.inspect", await workflow.InspectBundleAsync(args.Required("path"), token)); break;
            case "import": await PlanOrExecuteAsync(await workflow.PlanBundleImportAsync(args.Required("path"), token), args, reporter, plans, token); break;
            default: throw new ArgumentException("Bundle command must be export, inspect, or import.");
        }
        return true;
    }

    private static async Task<bool> ParametersAsync(string backend, CliArguments args, IMavlinkParameterProfileWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var label = backend.Equals("ardupilot", StringComparison.OrdinalIgnoreCase) ? "ArduPilot" : "PX4";
        if (!Required(args, 0, $"{label} command is required.").Equals("params", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Only '{backend} params' is supported.");
        var command = Required(args, 1, $"{label} params command is required.").ToLowerInvariant();
        var eventPrefix = backend.Equals("ardupilot", StringComparison.OrdinalIgnoreCase) ? "ardupilot" : "px4";
        switch (command)
        {
            case "list": await workflow.RefreshAsync(token); reporter.Event($"{eventPrefix}.params.list", workflow.Profiles); break;
            case "import": reporter.Event($"{eventPrefix}.params.import", await workflow.ImportAsync(args.Required("path"), args.Get("name"), token)); break;
            case "export": await workflow.ExportAsync(Required(args, 2, "A profile ID is required."), args.Required("path"), token); reporter.Event($"{eventPrefix}.params.export", new { Path = args.Required("path") }); break;
            case "download": reporter.Event($"{eventPrefix}.params.download", await workflow.DownloadAsync(args.Required("connection"), args.Required("vehicle"), args.Required("name"), token)); break;
            case "compare": reporter.Event($"{eventPrefix}.params.compare", await workflow.CompareAsync(args.Required("connection"), args.Required("vehicle"), Required(args, 2, "A profile ID is required."), token)); break;
            case "apply": await PlanOrExecuteAsync(await workflow.PlanApplyAsync(args.Required("connection"), args.Required("vehicle"), Required(args, 2, "A profile ID is required."), token), args, reporter, plans, token); break;
            default: throw new ArgumentException($"{label} params command is invalid.");
        }
        return true;
    }

    private static async Task<bool> SikAsync(CliArguments args, ISikRadioWorkflow workflow, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, "SiK command is required.").ToLowerInvariant();
        switch (command)
        {
            case "devices": await workflow.RefreshDevicesAsync(token); reporter.Event("sik.devices", workflow.Devices); break;
            case "probe": reporter.Event("sik.probe", await workflow.ProbeAsync(Required(args, 1, "A serial device ID or COM port is required."), token)); break;
            case "show": if (!workflow.TryGetProbe(Required(args, 1, "A serial device ID or COM port is required."), out var probe) || probe is null) throw new InvalidOperationException("No cached probe exists for this radio. Run 'sik probe' first."); reporter.Event("sik.show", probe); break;
            case "configure": await PlanOrExecuteAsync(await workflow.PlanConfigureAsync(Required(args, 1, "A serial device ID or COM port is required."), ParseSettings(args.Values("setting")), token), args, reporter, plans, token); break;
            case "pair": await PlanOrExecuteAsync(await workflow.PlanPairAsync(args.Required("source"), args.Required("target"), token), args, reporter, plans, token); break;
            default: throw new ArgumentException("SiK command must be devices, probe, show, configure, or pair.");
        }
        return true;
    }

    private static async Task<bool> PlanAsync(CliArguments args, IReviewedOperationWorkflow plans, ConsoleReporter reporter, CancellationToken token)
    {
        var command = Required(args, 0, "Plan command is required.").ToLowerInvariant();
        switch (command)
        {
            case "list": reporter.Event("plan.list", plans.Operations); break;
            case "inspect": reporter.Event("plan.inspect", plans.TryGet(Required(args, 1, "A plan ID is required."), out var plan) ? plan! : throw new KeyNotFoundException("Plan was not found.")); break;
            case "execute": reporter.Event("plan.execute", await plans.ExecuteAsync(Required(args, 1, "A plan ID is required."), token)); break;
            case "cancel": await plans.CancelAsync(Required(args, 1, "A plan ID is required."), args.Get("message") ?? "Operation cancelled by operator.", token); reporter.Event("plan.cancel", new { Id = Required(args, 1, "A plan ID is required.") }); break;
            default: throw new ArgumentException("Plan command must be list, inspect, execute, or cancel.");
        }
        return true;
    }

    private static async Task PlanOrExecuteAsync(ReviewedOperationSnapshot plan, CliArguments args, ConsoleReporter reporter, IReviewedOperationWorkflow? workflow, CancellationToken token)
    {
        reporter.Event("plan.created", plan);
        if (args.Has("execute"))
        {
            if (workflow is null) throw new InvalidOperationException("The reviewed operation workflow is required for --execute.");
            reporter.Event("plan.execute", await workflow.ExecuteAsync(plan.Id, token));
        }
    }
    private static Dictionary<int, int> ParseSettings(IReadOnlyList<string> values)
    {
        var output = new Dictionary<int, int>();
        foreach (var value in values)
        {
            var parts = value.Split('=', 2); if (parts.Length != 2 || !int.TryParse(parts[0].Trim().TrimStart('S', 's'), out var id) || !int.TryParse(parts[1], out var setting)) throw new ArgumentException("Each --setting must be S<number>=<value>.");
            output[id] = setting;
        }
        if (output.Count == 0) throw new ArgumentException("At least one --setting S<number>=<value> is required.");
        return output;
    }
    private static string Required(CliArguments args, int index, string message) => args.Positionals.Count > index ? args.Positionals[index] : throw new ArgumentException(message);
}
