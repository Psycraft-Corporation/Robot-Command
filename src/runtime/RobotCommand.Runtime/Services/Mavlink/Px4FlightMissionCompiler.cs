using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

public interface IFlightMissionCompiler
{
    string BackendKey { get; }
    bool SupportsTarget(UnitObservationSnapshot? target);
    IReadOnlyList<WorkflowFinding> Validate(FlightMissionDocument mission, UnitObservationSnapshot? target);
    IReadOnlyList<MavlinkMissionItem> Compile(FlightMissionDocument mission, UnitObservationSnapshot? target = null, FlightMissionTerrainProfile? terrainProfile = null);
    FlightMissionCompilationPreview Preview(FlightMissionDocument mission, UnitObservationSnapshot? target = null);
    FlightMissionDocument Decompile(string name, IReadOnlyList<MavlinkMissionItem> items, DateTimeOffset now);
}

public sealed class Px4FlightMissionCompiler : IFlightMissionCompiler
{
    private const byte GlobalRelativeAltInt = 6;
    // MAV_FRAME_MISSION is required for mission commands that do not carry a
    // geographic target, such as RTL. Land does carry a geographic target and
    // must use a global frame.
    private const byte MissionCommandFrame = 2;
    private const ushort NavWaypoint = 16;

    public string BackendKey => "PX4";

    public bool SupportsTarget(UnitObservationSnapshot? target)
        => target is null || (!target.IsGhost && target.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase) &&
                              target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<WorkflowFinding> Validate(FlightMissionDocument mission, UnitObservationSnapshot? target)
    {
        var findings = new List<WorkflowFinding>();
        try { FlightMissionLibraryStore.Validate(mission); }
        catch (Exception exception) { findings.Add(new("MISSION_DOCUMENT_INVALID", WorkflowFindingSeverity.Blocking, exception.Message)); }
        if (mission.Steps.Count == 0) findings.Add(new("MISSION_EMPTY", WorkflowFindingSeverity.Blocking, "Add at least one mission step before uploading."));
        if (mission.Steps.Count > 0 && mission.Steps.All(step => step.Kind is FlightMissionStepKind.Takeoff or FlightMissionStepKind.ReturnToLaunch or FlightMissionStepKind.Land))
            findings.Add(new("MISSION_NO_NAVIGATION", WorkflowFindingSeverity.Blocking, "Add a saved PoI or waypoint sequence to the mission."));
        var missingGeometrySteps = mission.Steps
            .Where(step => IsNavigation(step.Kind) && step.FrozenCoordinates.Count == 0)
            .Select(step => step.DisplayName)
            .ToArray();
        if (missingGeometrySteps.Length > 0)
            findings.Add(new("MISSION_STEP_GEOMETRY_REQUIRED", WorkflowFindingSeverity.Blocking,
                $"Select geometry for: {string.Join(", ", missingGeometrySteps)}."));
        // Mission end behavior is explicit and backend-neutral.  A mission
        // may end on its last navigation item and then apply Hold (the
        // default) or an automatically appended RTL.  Authored RTL/Land
        // steps remain supported for operators who want them in the outline.
        foreach (var survey in mission.Steps.Where(step => step.Kind == FlightMissionStepKind.SurveyZone))
            if (SurveyRoute(survey).Count < 2)
                findings.Add(new("MISSION_SURVEY_UNUSABLE", WorkflowFindingSeverity.Blocking, "The survey zone is too small or invalid for the selected coverage spacing."));
        foreach (var corridor in mission.Steps.Where(step => step.Kind == FlightMissionStepKind.CorridorScan))
            if (CorridorRoute(corridor).Count < 2)
                findings.Add(new("MISSION_CORRIDOR_UNUSABLE", WorkflowFindingSeverity.Blocking, "The corridor route is too short or its width and spacing are invalid."));
        var hasCameraMetadata = mission.CameraIntent is not null || mission.Steps.Any(step => step.Kind == FlightMissionStepKind.CameraCaptureIntent || step.Survey?.CameraIntent is not null || step.Corridor?.CameraIntent is not null);
        if (hasCameraMetadata && MavlinkCameraCapabilityMatrix.ActionsFor(mission).Count == 0)
            findings.Add(new("MISSION_CAMERA_INTENT_NOT_TRANSMITTED", WorkflowFindingSeverity.Warning, "Camera capture intent is retained in this mission but is not transmitted by the current compiler."));
        findings.AddRange(MavlinkCameraCapabilityMatrix.ValidateMissionActions(MavlinkAutopilotProfile.Px4, mission, target));
        if (target is not null)
        {
            if (!target.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase)) findings.Add(new("MISSION_BACKEND_UNSUPPORTED", WorkflowFindingSeverity.Blocking, "Mission execution is currently available for PX4 MAVLink multicopters only."));
            if (!target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase)) findings.Add(new("MISSION_VEHICLE_UNSUPPORTED", WorkflowFindingSeverity.Blocking, "Mission execution currently requires a PX4 multicopter."));
            if (target.Telemetry is null || target.Telemetry.IsStale) findings.Add(new("MISSION_TELEMETRY_STALE", WorkflowFindingSeverity.Blocking, "Fresh PX4 telemetry is required before mission transfer."));
            if (target.Telemetry?.LatitudeDegrees is null || target.Telemetry.LongitudeDegrees is null)
                findings.Add(new("MISSION_GLOBAL_POSITION_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "PX4 needs a current global position before this mission can be uploaded."));
            if (target.Diagnostics?.NavigationReadiness is "Blocked" or "Stale") findings.Add(new("MISSION_NAVIGATION_UNAVAILABLE", WorkflowFindingSeverity.Blocking, target.Diagnostics.NavigationReadinessDetail));
        }
        return findings;
    }

    public IReadOnlyList<MavlinkMissionItem> Compile(FlightMissionDocument mission, UnitObservationSnapshot? target = null, FlightMissionTerrainProfile? terrainProfile = null)
        => CompileForBackend(mission, target, terrainProfile, MavlinkAutopilotProfile.Px4);

    internal IReadOnlyList<MavlinkMissionItem> CompileForBackend(
        FlightMissionDocument mission,
        UnitObservationSnapshot? target,
        FlightMissionTerrainProfile? terrainProfile,
        MavlinkAutopilotProfile profile)
    {
        FlightMissionLibraryStore.Validate(mission);
        MavlinkCameraActionMissionCompiler.EnsureCanCompile(profile, mission, target);
        var takeoffLatitude = target?.Telemetry?.LatitudeDegrees;
        var takeoffLongitude = target?.Telemetry?.LongitudeDegrees;
        var homeCoordinate = takeoffLatitude is { } latitude && takeoffLongitude is { } longitude
            ? new FlightMissionCoordinate(latitude, longitude)
            : null;
        var output = new List<MavlinkMissionItem>();
        var lastSpeed = double.NaN;
        var captureSequence = 0;
        FlightMissionCoordinate? lastNavigationCoordinate = null;
        MavlinkCameraActionMissionCompiler.AppendMissionActions(output, mission.CameraIntent?.Actions ?? [], profile, ref captureSequence);
        FlightMissionStepKind? previousKind = null;
        foreach (var step in mission.Steps)
        {
            if (IsNavigation(step.Kind) && step.FrozenCoordinates.Count == 0)
                throw new InvalidOperationException($"Mission step '{step.DisplayName}' is missing geometry.");
            var speed = step.CruiseSpeedMetresPerSecond ?? mission.CruiseSpeedMetresPerSecond;
            if (IsNavigation(step.Kind) && !NearlyEqual(speed, lastSpeed))
            {
                output.Add(new(checked((ushort)output.Count), MavlinkCommandIds.DoChangeSpeed, MissionCommandFrame, 0, 0, 0,
                    Param1: 1, Param2: (float)speed, Param3: -1));
                lastSpeed = speed;
            }
            var altitude = step.RelativeAltitudeMetres ?? mission.RelativeAltitudeMetres;
            var terrainAltitudes = terrainProfile?.PointsByStepId.TryGetValue(step.Id, out var terrainPoints) == true
                ? terrainPoints
                : null;
            MavlinkCameraActionMissionCompiler.AppendMissionActions(
                output,
                FlightMissionPreviewCaptureBuilder.RouteTriggerStartActions(step.Kind, StepCameraIntent(step)),
                profile,
                ref captureSequence);
            switch (step.Kind)
            {
                case FlightMissionStepKind.Takeoff:
                    // PX4 validates a mission as a whole only after receiving its final
                    // item. Supplying a literal 0,0 here makes that validation fail for
                    // a takeoff at the current vehicle location. Use the fresh target
                    // coordinate that the upload preflight already requires instead.
                    output.Add(new(
                        checked((ushort)output.Count),
                        MavlinkCommandIds.NavTakeoff,
                        GlobalRelativeAltInt,
                        ToE7(takeoffLatitude),
                        ToE7(takeoffLongitude),
                        (float)altitude));
                    break;
                case FlightMissionStepKind.PointOfInterest:
                case FlightMissionStepKind.WaypointSequence:
                    foreach (var (coordinate, index) in NavigationCoordinates(step).Select((coordinate, index) => (coordinate, index)))
                    {
                        output.Add(new(checked((ushort)output.Count), NavWaypoint, GlobalRelativeAltInt,
                            checked((int)Math.Round(coordinate.LatitudeDegrees * 10_000_000)), checked((int)Math.Round(coordinate.LongitudeDegrees * 10_000_000)), (float)(terrainAltitudes is { Count: > 0 } && index < terrainAltitudes.Count ? terrainAltitudes[index].RelativeHomeAltitudeMetres : altitude)));
                        lastNavigationCoordinate = coordinate;
                    }
                    break;
                case FlightMissionStepKind.SurveyZone:
                case FlightMissionStepKind.CorridorScan:
                    foreach (var (coordinate, index) in NavigationCoordinates(step).Select((coordinate, index) => (coordinate, index)))
                    {
                        output.Add(new(checked((ushort)output.Count), NavWaypoint, GlobalRelativeAltInt,
                            checked((int)Math.Round(coordinate.LatitudeDegrees * 10_000_000)), checked((int)Math.Round(coordinate.LongitudeDegrees * 10_000_000)), (float)(terrainAltitudes is { Count: > 0 } && index < terrainAltitudes.Count ? terrainAltitudes[index].RelativeHomeAltitudeMetres : altitude)));
                        lastNavigationCoordinate = coordinate;
                    }
                    break;
                case FlightMissionStepKind.TimedLoiter:
                    var point = step.FrozenCoordinates.Single();
                    var loiterAltitude = terrainAltitudes is { Count: > 0 } ? terrainAltitudes[0].RelativeHomeAltitudeMetres : altitude;
                    output.Add(new(checked((ushort)output.Count), MavlinkCommandIds.NavLoiterTime, GlobalRelativeAltInt,
                        checked((int)Math.Round(point.LatitudeDegrees * 10_000_000)), checked((int)Math.Round(point.LongitudeDegrees * 10_000_000)), (float)loiterAltitude,
                        Param1: (float)step.LoiterDurationSeconds!.Value));
                    lastNavigationCoordinate = point;
                    break;
                case FlightMissionStepKind.CameraCaptureIntent:
                    break;
                case FlightMissionStepKind.ReturnToLaunch:
                    output.Add(new(checked((ushort)output.Count), MavlinkCommandIds.NavReturnToLaunch, MissionCommandFrame, 0, 0, 0));
                    break;
                case FlightMissionStepKind.Land:
                    // NAV_LAND is a location command. A Land directly after RTL
                    // completes at home; otherwise it completes at the last route
                    // point. Relative altitude zero represents ground at home.
                    var landingCoordinate = previousKind == FlightMissionStepKind.ReturnToLaunch
                        ? homeCoordinate
                        : lastNavigationCoordinate ?? homeCoordinate;
                    output.Add(new(checked((ushort)output.Count), MavlinkCommandIds.NavLand, GlobalRelativeAltInt,
                        ToE7(landingCoordinate?.LatitudeDegrees), ToE7(landingCoordinate?.LongitudeDegrees), 0));
                    break;
            }
            MavlinkCameraActionMissionCompiler.AppendMissionActions(output, StepCameraActions(step), profile, ref captureSequence);
            MavlinkCameraActionMissionCompiler.AppendMissionActions(
                output,
                FlightMissionPreviewCaptureBuilder.RouteTriggerStopActions(step.Kind, StepCameraIntent(step)),
                profile,
                ref captureSequence);
            previousKind = step.Kind;
        }
        if (mission.EndAction == FlightMissionEndAction.ReturnToLaunch && previousKind is not (FlightMissionStepKind.ReturnToLaunch or FlightMissionStepKind.Land))
            output.Add(new(checked((ushort)output.Count), MavlinkCommandIds.NavReturnToLaunch, MissionCommandFrame, 0, 0, 0));
        return output;
    }

    public FlightMissionCompilationPreview Preview(FlightMissionDocument mission, UnitObservationSnapshot? target = null)
    {
        var findings = Validate(mission, target);
        var route = mission.Steps.SelectMany(step => step.Kind switch
        {
            FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.TimedLoiter or FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan => NavigationCoordinates(step),
            _ => []
        }).ToArray();
        var distance = route.Zip(route.Skip(1), DistanceMetres).Sum();
        var speed = mission.CruiseSpeedMetresPerSecond;
        var seconds = speed > 0 ? distance / speed : 0;
        seconds += mission.Steps.Where(step => step.Kind == FlightMissionStepKind.TimedLoiter).Sum(step => step.LoiterDurationSeconds ?? 0);
        var segments = mission.Steps
            .Where(step => IsNavigation(step.Kind))
            .Select(step =>
            {
                var coordinates = NavigationCoordinates(step);
                var stepSpeed = step.CruiseSpeedMetresPerSecond ?? speed;
                var duration = stepSpeed > 0 ? coordinates.Zip(coordinates.Skip(1), DistanceMetres).Sum() / stepSpeed : 0;
                if (step.Kind == FlightMissionStepKind.TimedLoiter)
                    duration += step.LoiterDurationSeconds ?? 0;
                return new FlightMissionPreviewRouteSegment(step.Id, step.Kind, coordinates, duration, StepCameraIntent(step));
            })
            .ToArray();
        var captureStatistics = FlightMissionPreviewCaptureBuilder.Build(segments, mission.CameraIntent, speed);
        // Validation is also used by the authoring UI before a target is
        // selected.  A target-free preview is a document/route preview, not a
        // wire compilation; compiling it would dereference the missing target
        // position for a Takeoff step.
        var items = target is null || findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking)
            ? 0
            : Compile(mission, target).Count;
        var artifact = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", route.Select(point => $"{point.LatitudeDegrees:R},{point.LongitudeDegrees:R}")))));
        var surveys = mission.Steps.Where(step => step.Kind == FlightMissionStepKind.SurveyZone).ToArray();
        var surveyLines = surveys.Sum(step => SurveyRoute(step).Count / 2);
        var surveyArea = surveys.Sum(SurveyAreaSquareMetres);
        return new(mission.MissionId, route, distance, seconds, items, artifact, findings, $"{route.Length} navigation points · {distance:0} m · {seconds / 60:0.0} min", null, surveyLines, surveyArea, captureStatistics);
    }

    private static bool IsNavigation(FlightMissionStepKind kind) => kind is FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan or FlightMissionStepKind.TimedLoiter;

    private static IEnumerable<FlightMissionCameraAction> StepCameraActions(FlightMissionStep step)
        => (step.CameraIntent?.Actions ?? [])
            .Concat(step.Survey?.CameraIntent?.Actions ?? [])
            .Concat(step.Corridor?.CameraIntent?.Actions ?? []);
    private static FlightMissionCameraIntent? StepCameraIntent(FlightMissionStep step)
        => step.CameraIntent ?? step.Survey?.CameraIntent ?? step.Corridor?.CameraIntent;
    private static bool NearlyEqual(double left, double right) => double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) < 0.001;
    private static double DistanceMetres(FlightMissionCoordinate a, FlightMissionCoordinate b)
    {
        const double radius = 6_371_000; var lat = Math.PI / 180d;
        var dLat = (b.LatitudeDegrees - a.LatitudeDegrees) * lat; var dLon = (b.LongitudeDegrees - a.LongitudeDegrees) * lat;
        var value = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(a.LatitudeDegrees * lat) * Math.Cos(b.LatitudeDegrees * lat) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * radius * Math.Atan2(Math.Sqrt(value), Math.Sqrt(1 - value));
    }

    // Local equirectangular grid clipping: deterministic, robust for the small
    // survey zones accepted by the first planner slice, and intentionally kept
    // independent of the PX4 wire compiler.
    public static IReadOnlyList<FlightMissionCoordinate> SurveyRoute(FlightMissionStep step)
    {
        var polygon = step.FrozenCoordinates;
        if (polygon.Count < 3) return [];
        var survey = step.Survey ?? new FlightMissionSurveyOptions();
        var centreLat = polygon.Average(point => point.LatitudeDegrees); var centreLon = polygon.Average(point => point.LongitudeDegrees);
        var metresLat = 111_320d; var metresLon = metresLat * Math.Cos(centreLat * Math.PI / 180d);
        var angle = survey.BearingDegrees * Math.PI / 180d; var sin = Math.Sin(angle); var cos = Math.Cos(angle);
        var points = polygon.Select(point => (X: (point.LongitudeDegrees - centreLon) * metresLon, Y: (point.LatitudeDegrees - centreLat) * metresLat)).ToArray();
        var rotate = points.Select(point => (U: point.X * cos + point.Y * sin, V: -point.X * sin + point.Y * cos)).ToArray();
        var minV = rotate.Min(point => point.V); var maxV = rotate.Max(point => point.V);
        var route = new List<(double U, double V)>(); var reverse = survey.ReverseEntry;
        for (var v = minV; v <= maxV + 0.001; v += survey.LineSpacingMetres)
        {
            var intersections = new List<double>();
            for (var i = 0; i < rotate.Length; i++)
            {
                var a = rotate[i]; var b = rotate[(i + 1) % rotate.Length];
                if ((a.V > v) == (b.V > v) || Math.Abs(b.V - a.V) < 1e-8) continue;
                intersections.Add(a.U + (v - a.V) * (b.U - a.U) / (b.V - a.V));
            }
            intersections.Sort();
            for (var i = 0; i + 1 < intersections.Count; i += 2)
            {
                var first = intersections[i] - survey.TurnaroundDistanceMetres; var second = intersections[i + 1] + survey.TurnaroundDistanceMetres;
                if (reverse) { route.Add((second, v)); route.Add((first, v)); } else { route.Add((first, v)); route.Add((second, v)); }
                reverse = !reverse;
            }
        }
        return route.Select(point => new FlightMissionCoordinate(centreLat + (point.U * sin + point.V * cos) / metresLat, centreLon + (point.U * cos - point.V * sin) / metresLon)).ToArray();
    }

    /// <summary>
    /// Generates evenly-spaced offset passes along a directional polyline. Each
    /// pass traverses the complete source route; successive passes alternate
    /// direction and use extensions at both ends for vehicle turnarounds.
    /// </summary>
    public static IReadOnlyList<FlightMissionCoordinate> CorridorRoute(FlightMissionStep step)
    {
        var centreline = step.FrozenCoordinates;
        if (centreline.Count < 2) return [];
        var corridor = step.Corridor ?? new FlightMissionCorridorOptions();
        if (!double.IsFinite(corridor.CorridorWidthMetres) || corridor.CorridorWidthMetres <= 0 ||
            !double.IsFinite(corridor.LineSpacingMetres) || corridor.LineSpacingMetres <= 0 ||
            !double.IsFinite(corridor.TurnaroundDistanceMetres) || corridor.TurnaroundDistanceMetres < 0)
            return [];

        var centreLatitude = centreline.Average(point => point.LatitudeDegrees);
        var centreLongitude = centreline.Average(point => point.LongitudeDegrees);
        var metresPerLatitude = 111_320d;
        var metresPerLongitude = metresPerLatitude * Math.Cos(centreLatitude * Math.PI / 180d);
        var line = centreline.Select(point => new LocalPoint(
            (point.LongitudeDegrees - centreLongitude) * metresPerLongitude,
            (point.LatitudeDegrees - centreLatitude) * metresPerLatitude)).ToArray();
        var totalLength = LineLength(line);
        if (totalLength < 0.1) return [];

        var passCount = Math.Max(1, (int)Math.Floor(corridor.CorridorWidthMetres / corridor.LineSpacingMetres) + 1);
        var firstOffset = -corridor.CorridorWidthMetres / 2d;
        var offsets = Enumerable.Range(0, passCount)
            .Select(index => Math.Min(corridor.CorridorWidthMetres / 2d, firstOffset + index * corridor.LineSpacingMetres))
            .Append(corridor.CorridorWidthMetres / 2d)
            .DistinctBy(value => Math.Round(value, 6))
            .ToList();
        if (offsets.Count == 0) offsets.Add(0);

        // The selected entry side determines the first pass; reverse direction
        // swaps the source route orientation before the serpentine scan starts.
        if (corridor.EntrySide == FlightMissionCorridorEntrySide.Right) offsets.Reverse();
        var forward = !corridor.ReverseDirection;
        var output = new List<LocalPoint>();
        foreach (var offset in offsets)
        {
            var pass = OffsetPolyline(line, offset, corridor.TurnaroundDistanceMetres);
            if (!forward) pass.Reverse();
            output.AddRange(pass);
            forward = !forward;
        }

        return output.Select(point => new FlightMissionCoordinate(
            centreLatitude + point.Y / metresPerLatitude,
            centreLongitude + point.X / metresPerLongitude)).ToArray();
    }

    public static double SurveyAreaSquareMetres(FlightMissionStep step)
    {
        var points = step.FrozenCoordinates;
        if (points.Count < 3) return 0;
        const double metresPerDegree = 111_320d;
        var latitude = points.Average(point => point.LatitudeDegrees) * Math.PI / 180d;
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            var x1 = points[index].LongitudeDegrees * metresPerDegree * Math.Cos(latitude);
            var y1 = points[index].LatitudeDegrees * metresPerDegree;
            var x2 = next.LongitudeDegrees * metresPerDegree * Math.Cos(latitude);
            var y2 = next.LatitudeDegrees * metresPerDegree;
            area += x1 * y2 - x2 * y1;
        }
        return Math.Abs(area) / 2d;
    }

    /// <summary>
    /// Produces the actual navigation vertices the PX4 compiler will upload.
    /// Explicit routes are densified so a reviewed terrain profile can constrain
    /// altitude changes between finite, bounded legs rather than hiding a sharp
    /// terrain transition inside one long mission leg.
    /// </summary>
    public static IReadOnlyList<FlightMissionCoordinate> NavigationCoordinates(FlightMissionStep step, double maximumSpacingMetres = 50)
    {
        var source = step.Kind switch
        {
            FlightMissionStepKind.SurveyZone => SurveyRoute(step),
            FlightMissionStepKind.CorridorScan => CorridorRoute(step),
            _ => step.FrozenCoordinates
        };
        if (source.Count < 2 || !step.TerrainFollowing)
            return source;

        var output = new List<FlightMissionCoordinate> { source[0] };
        for (var index = 1; index < source.Count; index++)
        {
            var previous = source[index - 1];
            var next = source[index];
            var pieces = Math.Max(1, (int)Math.Ceiling(DistanceMetres(previous, next) / maximumSpacingMetres));
            for (var part = 1; part <= pieces; part++)
            {
                var fraction = (double)part / pieces;
                output.Add(new(
                    previous.LatitudeDegrees + (next.LatitudeDegrees - previous.LatitudeDegrees) * fraction,
                    previous.LongitudeDegrees + (next.LongitudeDegrees - previous.LongitudeDegrees) * fraction));
            }
        }
        return output;
    }

    private static int ToE7(double? coordinate)
    {
        if (coordinate is not { } value || !double.IsFinite(value))
            throw new InvalidOperationException("PX4 mission takeoff requires the target's current global position.");
        return checked((int)Math.Round(value * 10_000_000d));
    }

    private readonly record struct LocalPoint(double X, double Y);
    private static double LineLength(IReadOnlyList<LocalPoint> points)
        => points.Zip(points.Skip(1), (left, right) => Length(right.X - left.X, right.Y - left.Y)).Sum();

    private static List<LocalPoint> OffsetPolyline(IReadOnlyList<LocalPoint> line, double offset, double extension)
    {
        var result = new List<LocalPoint>(line.Count);
        for (var index = 0; index < line.Count; index++)
        {
            var previous = line[Math.Max(0, index - 1)];
            var next = line[Math.Min(line.Count - 1, index + 1)];
            var dx = next.X - previous.X;
            var dy = next.Y - previous.Y;
            var length = Length(dx, dy);
            if (length < 1e-6) { result.Add(line[index]); continue; }
            result.Add(new(line[index].X - dy / length * offset, line[index].Y + dx / length * offset));
        }
        if (result.Count < 2 || extension <= 0) return result;
        var startDirection = Unit(result[1].X - result[0].X, result[1].Y - result[0].Y);
        var endDirection = Unit(result[^1].X - result[^2].X, result[^1].Y - result[^2].Y);
        result[0] = new(result[0].X - startDirection.X * extension, result[0].Y - startDirection.Y * extension);
        result[^1] = new(result[^1].X + endDirection.X * extension, result[^1].Y + endDirection.Y * extension);
        return result;
    }

    private static LocalPoint Unit(double x, double y)
    {
        var length = Length(x, y);
        return length < 1e-6 ? new(0, 0) : new(x / length, y / length);
    }

    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

    public FlightMissionDocument Decompile(string name, IReadOnlyList<MavlinkMissionItem> items, DateTimeOffset now)
    {
        var steps = new List<FlightMissionStep>();
        var missionCameraActions = new List<FlightMissionCameraAction>();
        var defaultSpeed = 5d;
        var activeSpeed = defaultSpeed;
        var navigationSeen = false;
        foreach (var item in items.OrderBy(item => item.Sequence))
        {
            if (item.Frame is not (2 or 3 or 6))
                throw new NotSupportedException($"Vehicle mission item {item.Command} at sequence {item.Sequence} uses unsupported frame {item.Frame}.");
            switch (item.Command)
            {
                case MavlinkCommandIds.ImageStartCapture:
                case MavlinkCommandIds.ImageStopCapture:
                case MavlinkCommandIds.DoSetCameraTriggerDistance:
                case MavlinkCommandIds.VideoStartCapture:
                case MavlinkCommandIds.VideoStopCapture:
                case MavlinkCommandIds.SetCameraMode:
                case MavlinkCommandIds.DoSetRoiLocation:
                case MavlinkCommandIds.DoSetRoi:
                case MavlinkCommandIds.DoGimbalManagerPitchYaw:
                case MavlinkCommandIds.DoMountControl:
                    if (!MavlinkCameraActionMissionCompiler.TryDecompile(item, out var cameraAction) || cameraAction is null)
                        throw new NotSupportedException($"Vehicle camera mission item {item.Command} at sequence {item.Sequence} has invalid parameters.");
                    if (!navigationSeen || steps.Count == 0)
                    {
                        missionCameraActions.Add(cameraAction);
                    }
                    else
                    {
                        var step = steps[^1];
                        steps[^1] = step with
                        {
                            CameraIntent = AppendCameraAction(step.CameraIntent, cameraAction)
                        };
                    }
                    break;
                case MavlinkCommandIds.DoChangeSpeed:
                    if (!float.IsFinite(item.Param2) || item.Param2 <= 0)
                        throw new NotSupportedException("PX4 returned an invalid mission speed constraint.");
                    activeSpeed = item.Param2;
                    if (!navigationSeen)
                        defaultSpeed = activeSpeed;
                    break;
                case MavlinkCommandIds.NavTakeoff:
                    steps.Add(new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.Takeoff, RelativeAltitudeMetres: item.AltitudeMetres));
                    break;
                case NavWaypoint:
                    steps.Add(new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.PointOfInterest,
                        Coordinates: [new(item.LatitudeE7 / 10_000_000d, item.LongitudeE7 / 10_000_000d)],
                        RelativeAltitudeMetres: item.AltitudeMetres,
                        CruiseSpeedMetresPerSecond: navigationSeen && NearlyEqual(activeSpeed, defaultSpeed) ? null : activeSpeed));
                    navigationSeen = true;
                    break;
                case MavlinkCommandIds.NavLoiterTime:
                    if (!float.IsFinite(item.Param1) || item.Param1 <= 0)
                        throw new NotSupportedException("PX4 returned an invalid timed loiter duration.");
                    steps.Add(new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.TimedLoiter,
                        Coordinates: [new(item.LatitudeE7 / 10_000_000d, item.LongitudeE7 / 10_000_000d)],
                        RelativeAltitudeMetres: item.AltitudeMetres,
                        CruiseSpeedMetresPerSecond: navigationSeen && NearlyEqual(activeSpeed, defaultSpeed) ? null : activeSpeed,
                        LoiterDurationSeconds: item.Param1));
                    navigationSeen = true;
                    break;
                case MavlinkCommandIds.NavReturnToLaunch: steps.Add(new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.ReturnToLaunch)); break;
                case MavlinkCommandIds.NavLand: steps.Add(new(Guid.NewGuid().ToString("N"), FlightMissionStepKind.Land)); break;
                default: throw new NotSupportedException($"Vehicle mission item {item.Command} at sequence {item.Sequence} is not supported by the native mission library.");
            }
        }
        var altitude = items.FirstOrDefault(item => item.Command is NavWaypoint or MavlinkCommandIds.NavTakeoff)?.AltitudeMetres ?? 20;
        return new(FlightMissionDocument.CurrentSchemaVersion, $"mission-{Guid.NewGuid():N}", name, altitude, steps, now, now,
            CruiseSpeedMetresPerSecond: defaultSpeed,
            CameraIntent: missionCameraActions.Count == 0
                ? null
                : new FlightMissionCameraIntent(Mode: "Actions", Actions: missionCameraActions));
    }

    private static FlightMissionCameraIntent AppendCameraAction(
        FlightMissionCameraIntent? intent,
        FlightMissionCameraAction action)
        => (intent ?? new FlightMissionCameraIntent(Mode: "Actions")) with
        {
            Actions = (intent?.Actions ?? []).Append(action).ToArray()
        };
}

/// <summary>
/// ArduCopter uses the same native Robot Command mission model and MAVLink
/// mission item primitives as PX4, but validates the target and supported
/// mission association independently. Route generation and wire conversion
/// remain shared with the PX4 compiler so survey/corridor behavior cannot
/// diverge between backends.
/// </summary>
public sealed class ArduPilotFlightMissionCompiler : IFlightMissionCompiler
{
    private readonly Px4FlightMissionCompiler _shared = new();

    public string BackendKey => "ArduPilot";

    public bool SupportsTarget(UnitObservationSnapshot? target)
        => target is null || (!target.IsGhost && target.ProfileKey.Contains("ardupilot", StringComparison.OrdinalIgnoreCase) &&
                              target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<WorkflowFinding> Validate(FlightMissionDocument mission, UnitObservationSnapshot? target)
    {
        var findings = _shared.Validate(mission, null).ToList();
        findings.AddRange(MavlinkCameraCapabilityMatrix.ValidateMissionActions(MavlinkAutopilotProfile.ArduPilot, mission, target));
        if (target is null) return findings;

        if (!SupportsTarget(target))
        {
            findings.Add(new("MISSION_VEHICLE_UNSUPPORTED", WorkflowFindingSeverity.Blocking,
                "ArduPilot mission execution requires an ArduCopter multicopter."));
            return findings;
        }

        if (target.Telemetry is null || target.Telemetry.IsStale)
            findings.Add(new("MISSION_TELEMETRY_STALE", WorkflowFindingSeverity.Blocking,
                "Fresh ArduPilot telemetry is required before mission transfer."));
        if (target.Telemetry?.LatitudeDegrees is null || target.Telemetry.LongitudeDegrees is null)
            findings.Add(new("MISSION_GLOBAL_POSITION_UNAVAILABLE", WorkflowFindingSeverity.Blocking,
                "ArduPilot mission upload requires a current global position."));
        if (target.Diagnostics?.NavigationReadiness is "Blocked" or "Stale")
            findings.Add(new("MISSION_NAVIGATION_UNAVAILABLE", WorkflowFindingSeverity.Blocking,
                target.Diagnostics.NavigationReadinessDetail));
        if (target.Diagnostics is { Blockers.Count: > 0 } diagnostics)
            findings.Add(new("MISSION_PREFLIGHT_BLOCKED", WorkflowFindingSeverity.Blocking,
                diagnostics.Blockers[0]));
        return findings.DistinctBy(item => item.Code).ToArray();
    }

    public IReadOnlyList<MavlinkMissionItem> Compile(
        FlightMissionDocument mission,
        UnitObservationSnapshot? target = null,
        FlightMissionTerrainProfile? terrainProfile = null)
        // ArduCopter rejects NaN in the mission yaw slot (param4), even for
        // commands where yaw is not used. PX4 accepts NaN as the MAVLink
        // "ignore yaw" value, so keep the shared compiler unchanged and
        // normalize only the ArduPilot wire representation.
        => _shared.CompileForBackend(mission, target, terrainProfile, MavlinkAutopilotProfile.ArduPilot)
            .Select(item => float.IsFinite(item.Param4) ? item : item with { Param4 = 0f })
            .ToArray();

    public FlightMissionCompilationPreview Preview(FlightMissionDocument mission, UnitObservationSnapshot? target = null)
    {
        var preview = _shared.Preview(mission, null);
        var findings = Validate(mission, target);
        var itemCount = target is not null && findings.All(item => item.Severity != WorkflowFindingSeverity.Blocking)
            ? Compile(mission, target).Count
            : 0;
        return preview with
        {
            Findings = findings,
            MissionItemCount = itemCount,
            Summary = target is null ? preview.Summary : $"{preview.Summary} · ArduPilot AUTO"
        };
    }

    public FlightMissionDocument Decompile(string name, IReadOnlyList<MavlinkMissionItem> items, DateTimeOffset now)
        => _shared.Decompile(name, items, now);
}
