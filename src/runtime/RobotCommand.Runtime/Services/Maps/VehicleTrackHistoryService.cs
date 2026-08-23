using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Reconciliation;

namespace RobotCommand.Services.Maps;

public sealed class VehicleTrackHistoryService : IVehicleTrackHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<TrackKey, List<VehicleTrackSample>> _tracks = [];
    private readonly TimeSpan _maximumAge;
    private readonly int _maximumPoints;
    private readonly double _minimumDistanceMetres;
    private readonly IUnitDefinitionService? _reconciliation;

    public VehicleTrackHistoryService(
        AppConfiguration configuration,
        IUnitDefinitionService? reconciliation = null)
    {
        _maximumAge = TimeSpan.FromMinutes(configuration.MapTrailMaxAgeMinutes);
        _maximumPoints = configuration.MapTrailMaxPointsPerVehicle;
        _minimumDistanceMetres = configuration.MapTrailMinimumDistanceMetres;
        _reconciliation = reconciliation;
    }

    public void Record(IEnumerable<VehicleTelemetryRecord> telemetry, DateTimeOffset now)
    {
        var candidates = telemetry
            .Where(item => item.LatitudeDegrees is not null && item.LongitudeDegrees is not null)
            .Where(item => IsValidCoordinate(item.LongitudeDegrees!.Value, item.LatitudeDegrees!.Value))
            .Where(item => IsAirborne(item.LandedState))
            .GroupBy(item => new TrackKey(item.VehicleId, item.ConnectionId))
            .Select(group => group
                .OrderByDescending(item => StateRank(item.State))
                .ThenByDescending(item => item.ObservedAt)
                .First())
            .ToArray();

        lock (_gate)
        {
            foreach (var sample in candidates)
            {
                var key = new TrackKey(sample.VehicleId, sample.ConnectionId);
                if (!_tracks.TryGetValue(key, out var history))
                {
                    history = [];
                    _tracks.Add(key, history);
                }

                AddSample(history, new VehicleTrackSample(
                    sample.VehicleId,
                    sample.ConnectionId,
                    sample.LongitudeDegrees!.Value,
                    sample.LatitudeDegrees!.Value,
                    sample.State,
                    sample.ObservedAt));
            }

            Prune(now);
        }
    }

    public IReadOnlyList<MapVehicleTrailVisual> BuildTrails(
        IReadOnlyList<VehicleRecord> vehicles,
        string? selectedVehicleId)
        => BuildTrailsForSelectedVehicles(
            vehicles,
            string.IsNullOrWhiteSpace(selectedVehicleId)
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>([selectedVehicleId], StringComparer.Ordinal));

    public IReadOnlyList<MapVehicleTrailVisual> BuildTrailsForSelectedVehicles(
        IReadOnlyList<VehicleRecord> vehicles,
        IReadOnlySet<string> selectedVehicleIds)
    {
        lock (_gate)
        {
            var result = new List<MapVehicleTrailVisual>();
            foreach (var vehicle in vehicles)
            {
                var telemetrySourceId = _reconciliation?.ResolveTelemetrySource(vehicle.Id) ?? vehicle.Id;
                var candidate = _tracks
                    .Where(pair =>
                        pair.Key.VehicleId == telemetrySourceId &&
                        pair.Value.Count >= 2)
                    .OrderByDescending(pair => StateRank(pair.Value[^1].State))
                    .ThenByDescending(pair => pair.Value[^1].ObservedAt)
                    .FirstOrDefault();
                if (candidate.Value is null || candidate.Value.Count < 2)
                {
                    continue;
                }

                result.Add(new MapVehicleTrailVisual(
                    vehicle.Id,
                    vehicle.Name,
                    candidate.Key.ConnectionId,
                    candidate.Value
                        .Select(item => new OperationalPoint(item.LongitudeDegrees, item.LatitudeDegrees))
                        .ToArray(),
                    candidate.Value[^1].State,
                    selectedVehicleIds.Contains(vehicle.Id)));
            }

            return result
                .OrderByDescending(item => item.Selected)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void Clear(string? vehicleId = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(vehicleId))
            {
                _tracks.Clear();
                return;
            }

            foreach (var key in _tracks.Keys
                         .Where(key => string.Equals(key.VehicleId, vehicleId, StringComparison.Ordinal))
                         .ToArray())
            {
                _tracks.Remove(key);
            }
        }
    }

    private void AddSample(List<VehicleTrackSample> history, VehicleTrackSample sample)
    {
        if (history.Count > 0)
        {
            var previous = history[^1];
            if (sample.ObservedAt <= previous.ObservedAt)
            {
                return;
            }

            var distance = GreatCircleDistanceMetres(
                previous.LongitudeDegrees,
                previous.LatitudeDegrees,
                sample.LongitudeDegrees,
                sample.LatitudeDegrees);
            var elapsed = sample.ObservedAt - previous.ObservedAt;
            if (distance < _minimumDistanceMetres &&
                elapsed < TimeSpan.FromSeconds(10) &&
                sample.State == previous.State)
            {
                return;
            }
        }

        history.Add(sample);
        var overflow = history.Count - _maximumPoints;
        if (overflow > 0)
        {
            history.RemoveRange(0, overflow);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var threshold = now - _maximumAge;
        foreach (var key in _tracks.Keys.ToArray())
        {
            var history = _tracks[key];
            history.RemoveAll(item => item.ObservedAt < threshold);
            if (history.Count == 0)
            {
                _tracks.Remove(key);
            }
        }
    }

    private static bool IsValidCoordinate(double longitude, double latitude)
        => double.IsFinite(longitude) &&
           double.IsFinite(latitude) &&
           longitude is >= -180 and <= 180 &&
           latitude is >= -90 and <= 90;

    private static bool IsAirborne(string? landedState)
        => landedState is not null &&
           (landedState.Equals("Flying", StringComparison.OrdinalIgnoreCase) ||
            landedState.Equals("Taking off", StringComparison.OrdinalIgnoreCase) ||
            landedState.Equals("Landing", StringComparison.OrdinalIgnoreCase) ||
            landedState.Equals("InAir", StringComparison.OrdinalIgnoreCase));

    private static double GreatCircleDistanceMetres(
        double longitudeA,
        double latitudeA,
        double longitudeB,
        double latitudeB)
    {
        const double radius = 6_371_000d;
        var latitudeDelta = DegreesToRadians(latitudeB - latitudeA);
        var longitudeDelta = DegreesToRadians(longitudeB - longitudeA);
        var latitudeARadians = DegreesToRadians(latitudeA);
        var latitudeBRadians = DegreesToRadians(latitudeB);
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2d), 2) +
                        (Math.Cos(latitudeARadians) * Math.Cos(latitudeBRadians) *
                         Math.Pow(Math.Sin(longitudeDelta / 2d), 2));
        return radius * 2d * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(Math.Max(0, 1d - haversine)));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;

    private static int StateRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 4,
            AvailabilityState.Degraded => 3,
            AvailabilityState.Stale => 2,
            AvailabilityState.Offline => 1,
            _ => 0
        };

    private readonly record struct TrackKey(string VehicleId, string ConnectionId);
}
