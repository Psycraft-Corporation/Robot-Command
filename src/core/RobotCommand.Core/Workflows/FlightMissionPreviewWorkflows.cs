namespace RobotCommand.Core;

/// <summary>A route-bearing mission step used by preview-only calculations.</summary>
public sealed record FlightMissionPreviewRouteSegment(
    string StepId,
    FlightMissionStepKind StepKind,
    IReadOnlyList<FlightMissionCoordinate> Coordinates,
    double DurationSeconds,
    FlightMissionCameraIntent? CameraIntent = null);

/// <summary>A presentation marker for an expected camera event.</summary>
public sealed record FlightMissionCaptureMarker(
    string? StepId,
    FlightMissionCameraActionKind Action,
    FlightMissionCoordinate Coordinate,
    double DistanceAlongRouteMetres,
    bool GeneratedRouteTrigger = false);

/// <summary>
/// Conservative, presentation-only capture estimates. These values never
/// participate in mission validation or vehicle commands.
/// </summary>
public sealed record FlightMissionCaptureStatistics(
    int ExpectedPhotoCount,
    double ExpectedVideoDurationSeconds,
    int TriggerCommandCount,
    IReadOnlyList<FlightMissionCaptureMarker> Markers)
{
    public static FlightMissionCaptureStatistics Empty { get; } = new(0, 0, 0, []);

    public bool HasCaptures => ExpectedPhotoCount > 0 || ExpectedVideoDurationSeconds > 0 || Markers.Count > 0;
}

/// <summary>
/// Shared preview logic for opt-in survey/corridor photo triggering and the
/// explicit action list. Runtime compilers call the trigger helpers so the
/// preview and emitted MAVLink items describe the same operation.
/// </summary>
public static class FlightMissionPreviewCaptureBuilder
{
    private const int MarkerLimit = 512;

    public static IReadOnlyList<FlightMissionCameraAction> RouteTriggerStartActions(
        FlightMissionStepKind kind,
        FlightMissionCameraIntent? intent)
    {
        if (kind is not (FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan) ||
            intent is null ||
            !intent.AutomaticPhotoCaptureEnabled)
        {
            return [];
        }

        if (intent.TriggerDistanceMetres is { } distance && double.IsFinite(distance) && distance > 0)
            return [FlightMissionCameraAction.PhotoByDistance(distance, intent.CameraName)];

        if (intent.TriggerIntervalSeconds is { } interval && double.IsFinite(interval) && interval > 0)
            return [FlightMissionCameraAction.PhotoByTime(interval, intent.CameraName)];

        return [];
    }

    public static IReadOnlyList<FlightMissionCameraAction> RouteTriggerStopActions(
        FlightMissionStepKind kind,
        FlightMissionCameraIntent? intent)
        => RouteTriggerStartActions(kind, intent).Count == 0
            ? []
            : [FlightMissionCameraAction.StopPhotos(intent?.CameraName)];

    public static IReadOnlyList<FlightMissionCameraAction> RouteTriggerActions(
        FlightMissionStepKind kind,
        FlightMissionCameraIntent? intent)
        => RouteTriggerStartActions(kind, intent)
            .Concat(RouteTriggerStopActions(kind, intent))
            .ToArray();

    public static FlightMissionCaptureStatistics Build(
        IReadOnlyList<FlightMissionPreviewRouteSegment> segments,
        FlightMissionCameraIntent? missionIntent,
        double defaultSpeedMetresPerSecond)
    {
        var routeSegments = segments
            .Where(segment => segment.Coordinates.Count > 0)
            .ToArray();
        if (routeSegments.Length == 0)
            return FlightMissionCaptureStatistics.Empty;

        var result = new CaptureAccumulator();
        var totalDuration = routeSegments.Sum(segment => SegmentDuration(segment, defaultSpeedMetresPerSecond));
        var firstPoint = routeSegments[0].Coordinates[0];

        ProcessActions(
            missionIntent?.Actions ?? [],
            stepId: null,
            coordinates: routeSegments.SelectMany(segment => segment.Coordinates).ToArray(),
            durationSeconds: 0,
            distanceOffsetMetres: 0,
            generated: false,
            result);

        // A legacy trigger is a route-scoped start/stop pair. The start is
        // emitted before the route and the stop immediately after it.
        foreach (var segment in routeSegments)
        {
            var segmentDuration = SegmentDuration(segment, defaultSpeedMetresPerSecond);
            var segmentDistance = RouteDistance(segment.Coordinates);
            var startActions = RouteTriggerStartActions(segment.StepKind, segment.CameraIntent);
            ProcessActions(startActions, segment.StepId, segment.Coordinates, segmentDuration,
                result.DistanceMetres, generated: true, result, advanceTime: false);
            result.ElapsedSeconds += segmentDuration;
            ProcessActions(segment.CameraIntent?.Actions ?? [], segment.StepId, segment.Coordinates,
                segmentDuration, result.DistanceMetres, generated: false, result, advanceTime: false);
            ProcessActions(RouteTriggerStopActions(segment.StepKind, segment.CameraIntent), segment.StepId,
                segment.Coordinates, segmentDuration, result.DistanceMetres + segmentDistance, generated: true, result, advanceTime: false);
            result.DistanceMetres += segmentDistance;
        }

        result.CloseVideo(totalDuration, firstPoint);
        return result.ToStatistics();
    }

    private static void ProcessActions(
        IEnumerable<FlightMissionCameraAction> actions,
        string? stepId,
        IReadOnlyList<FlightMissionCoordinate> coordinates,
        double durationSeconds,
        double distanceOffsetMetres,
        bool generated,
        CaptureAccumulator result,
        bool advanceTime = true)
    {
        var distance = RouteDistance(coordinates);
        foreach (var action in actions)
        {
            if (!action.IsValid)
                continue;

            switch (action.Kind)
            {
                case FlightMissionCameraActionKind.PhotoOnce:
                    result.PhotoCount++;
                    result.AddMarker(new(stepId, action.Kind, coordinates[0], distanceOffsetMetres, generated));
                    break;
                case FlightMissionCameraActionKind.PhotoByTime:
                    result.TriggerCommandCount++;
                    AddPeriodicMarkers(result, action.Kind, stepId, coordinates, distanceOffsetMetres,
                        durationSeconds, action.IntervalSeconds!.Value, byDistance: false, generated);
                    break;
                case FlightMissionCameraActionKind.PhotoByDistance:
                    result.TriggerCommandCount++;
                    AddPeriodicMarkers(result, action.Kind, stepId, coordinates, distanceOffsetMetres,
                        distance, action.DistanceMetres!.Value, byDistance: true, generated);
                    break;
                case FlightMissionCameraActionKind.StartVideo:
                    result.VideoStartSeconds ??= result.ElapsedSeconds;
                    result.AddMarker(new(stepId, action.Kind, coordinates[0], distanceOffsetMetres, generated));
                    break;
                case FlightMissionCameraActionKind.StopVideo:
                    if (result.VideoStartSeconds is { } start)
                        result.VideoDurationSeconds += Math.Max(0, result.ElapsedSeconds - start);
                    result.VideoStartSeconds = null;
                    result.AddMarker(new(stepId, action.Kind, coordinates[^1], distanceOffsetMetres + distance, generated));
                    break;
                case FlightMissionCameraActionKind.StopPhotos:
                    break;
            }

        }

        if (advanceTime)
            result.ElapsedSeconds += durationSeconds;
    }

    private static void AddPeriodicMarkers(
        CaptureAccumulator result,
        FlightMissionCameraActionKind kind,
        string? stepId,
        IReadOnlyList<FlightMissionCoordinate> coordinates,
        double distanceOffsetMetres,
        double span,
        double interval,
        bool byDistance,
        bool generated)
    {
        if (interval <= 0 || !double.IsFinite(interval)) return;
        var count = Math.Min(MarkerLimit, Math.Max(0, (int)Math.Floor(span / interval)));
        result.PhotoCount += count;
        for (var index = 1; index <= count; index++)
        {
            var offset = interval * index;
            var fraction = span <= 0 ? 0 : offset / span;
            var markerDistance = byDistance
                ? distanceOffsetMetres + offset
                : distanceOffsetMetres + fraction * RouteDistance(coordinates);
            result.AddMarker(new(stepId, kind, Interpolate(coordinates, fraction), markerDistance, generated));
        }
    }

    private static double SegmentDuration(FlightMissionPreviewRouteSegment segment, double defaultSpeed)
    {
        if (double.IsFinite(segment.DurationSeconds) && segment.DurationSeconds >= 0)
            return segment.DurationSeconds;
        var speed = double.IsFinite(defaultSpeed) && defaultSpeed > 0 ? defaultSpeed : 0;
        return speed > 0 ? RouteDistance(segment.Coordinates) / speed : 0;
    }

    private static double RouteDistance(IReadOnlyList<FlightMissionCoordinate> coordinates)
        => coordinates.Zip(coordinates.Skip(1), DistanceMetres).Sum();

    private static FlightMissionCoordinate Interpolate(IReadOnlyList<FlightMissionCoordinate> coordinates, double fraction)
    {
        if (coordinates.Count == 1) return coordinates[0];
        var clamped = Math.Clamp(fraction, 0, 1);
        var distances = new double[coordinates.Count];
        for (var index = 1; index < coordinates.Count; index++)
            distances[index] = distances[index - 1] + DistanceMetres(coordinates[index - 1], coordinates[index]);
        var target = distances[^1] * clamped;
        for (var index = 1; index < distances.Length; index++)
        {
            if (distances[index] < target) continue;
            var span = distances[index] - distances[index - 1];
            var local = span <= 0 ? 0 : (target - distances[index - 1]) / span;
            var from = coordinates[index - 1];
            var to = coordinates[index];
            return new(from.LatitudeDegrees + (to.LatitudeDegrees - from.LatitudeDegrees) * local,
                from.LongitudeDegrees + (to.LongitudeDegrees - from.LongitudeDegrees) * local);
        }
        return coordinates[^1];
    }

    private static double DistanceMetres(FlightMissionCoordinate a, FlightMissionCoordinate b)
    {
        const double radius = 6_371_000;
        const double radians = Math.PI / 180d;
        var dLat = (b.LatitudeDegrees - a.LatitudeDegrees) * radians;
        var dLon = (b.LongitudeDegrees - a.LongitudeDegrees) * radians;
        var value = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(a.LatitudeDegrees * radians) * Math.Cos(b.LatitudeDegrees * radians) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * radius * Math.Atan2(Math.Sqrt(value), Math.Sqrt(Math.Max(0, 1 - value)));
    }

    private sealed class CaptureAccumulator
    {
        public int PhotoCount { get; set; }
        public double VideoDurationSeconds { get; set; }
        public int TriggerCommandCount { get; set; }
        public double DistanceMetres { get; set; }
        public double ElapsedSeconds { get; set; }
        public double? VideoStartSeconds { get; set; }
        public List<FlightMissionCaptureMarker> Markers { get; } = [];

        public void AddMarker(FlightMissionCaptureMarker marker)
        {
            if (Markers.Count < MarkerLimit) Markers.Add(marker);
        }

        public void CloseVideo(double totalDuration, FlightMissionCoordinate fallback)
        {
            if (VideoStartSeconds is not { } start) return;
            VideoDurationSeconds += Math.Max(0, totalDuration - start);
            AddMarker(new(null, FlightMissionCameraActionKind.StopVideo, fallback, DistanceMetres));
            VideoStartSeconds = null;
        }

        public FlightMissionCaptureStatistics ToStatistics()
            => new(PhotoCount, VideoDurationSeconds, TriggerCommandCount, Markers.ToArray());
    }
}
