using System.Diagnostics;
using RobotCommand.Core;

namespace RobotCommand.Simulation;

/// <summary>
/// Portable deterministic Ghost physics.  It owns no stores, UI objects,
/// transports, or platform APIs and can therefore run in the worker or in
/// deterministic tests.
/// </summary>
public sealed class GhostSimulationEngine
{
    private const double EarthRadiusMetres = 6_378_137d;
    private const double TickSeconds = 1d / 60d;
    private readonly object _gate = new();
    private readonly object _metricsGate = new();
    private readonly double[] _tickSamples = new double[256];
    private int _tickSampleCount;
    private readonly Dictionary<string, GhostState> _ghosts = new(StringComparer.Ordinal);
    private readonly string _workerInstanceId;
    private long _sequence;
    private long _physicsTicks;
    private long _missedDeadlines;
    private double _lastTickMilliseconds;
    private double _maxTickMilliseconds;
    private int _nextNumber;

    public GhostSimulationEngine(string? workerInstanceId = null)
    {
        _workerInstanceId = workerInstanceId ?? Guid.NewGuid().ToString("N");
    }

    public int Count { get { lock (_gate) return _ghosts.Count; } }

    private long _snapshotPublications;

    public SimulationPerformanceMetrics Metrics
    {
        get
        {
            double p95;
            lock (_metricsGate)
            {
                var count = _tickSampleCount;
                if (count == 0) p95 = 0;
                else
                {
                    var samples = _tickSamples.Take(count).OrderBy(value => value).ToArray();
                    p95 = samples[Math.Clamp((int)Math.Ceiling(count * .95) - 1, 0, count - 1)];
                }
            }
            return new(
                Interlocked.Read(ref _physicsTicks),
                Interlocked.Read(ref _missedDeadlines),
                Volatile.Read(ref _lastTickMilliseconds),
                Volatile.Read(ref _maxTickMilliseconds),
                20,
                p95,
                Interlocked.Read(ref _snapshotPublications));
        }
    }

    public SimulationCommandResult Apply(SimulationCommand command)
    {
        lock (_gate)
        {
            try
            {
                return command.Kind switch
                {
                    SimulationCommandKind.Create => Create(command),
                    SimulationCommandKind.Delete => Delete(command),
                    SimulationCommandKind.Arm => SetArmed(command, true),
                    SimulationCommandKind.Disarm => SetArmed(command, false),
                    SimulationCommandKind.Takeoff => StartOperation(command, "Takeoff", command.AltitudeAglMetres ?? 5),
                    SimulationCommandKind.Land => StartOperation(command, "Land", 0),
                    SimulationCommandKind.Hold => Hold(command),
                    SimulationCommandKind.GoTo => StartGoTo(command),
                    SimulationCommandKind.ChangeAltitude => StartOperation(command, "ChangeAltitude", command.AltitudeAglMetres ?? 0),
                    SimulationCommandKind.StartRoute => StartRoute(command),
                    SimulationCommandKind.PauseRoute => SetRoutePaused(command, true),
                    SimulationCommandKind.ResumeRoute => SetRoutePaused(command, false),
                    SimulationCommandKind.Shutdown => Result(command, true, "SHUTDOWN_ACCEPTED", "Simulation shutdown accepted."),
                    _ => Result(command, false, "UNSUPPORTED_COMMAND", "The simulation command is not supported.")
                };
            }
            catch (Exception exception)
            {
                return Result(command, false, "COMMAND_FAILED", exception.Message);
            }
        }
    }

    public void Tick(double elapsedSeconds = TickSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        var started = Stopwatch.GetTimestamp();
        // A periodic timer can wake a little late under normal desktop load.
        // Count a deadline miss only when the scheduler lost a substantial
        // fraction of a tick, rather than turning ordinary timer jitter into a
        // false fleet-health alarm.
        if (elapsedSeconds > TickSeconds * 2) Interlocked.Increment(ref _missedDeadlines);
        lock (_gate)
        {
            foreach (var ghost in _ghosts.Values)
            {
                if (ghost.Operation is not { } operation) continue;
                switch (operation.Kind)
                {
                    case SimulationCommandKind.GoTo:
                    case SimulationCommandKind.StartRoute:
                        StepNavigation(ghost, elapsedSeconds);
                        break;
                    case SimulationCommandKind.Takeoff:
                    case SimulationCommandKind.Land:
                    case SimulationCommandKind.ChangeAltitude:
                        if (MoveAltitude(ghost, operation.AltitudeAglMetres, elapsedSeconds))
                        {
                            if (operation.Kind == SimulationCommandKind.Land) ghost.Armed = false;
                            ghost.Operation = null;
                        }
                        break;
                }
            }
        }
        var milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        var tickNumber = Interlocked.Increment(ref _physicsTicks);
        Volatile.Write(ref _lastTickMilliseconds, milliseconds);
        lock (_metricsGate)
        {
            _tickSamples[(tickNumber - 1) % _tickSamples.Length] = milliseconds;
            _tickSampleCount = Math.Min(_tickSamples.Length, _tickSampleCount + 1);
        }
        while (true)
        {
            var current = Volatile.Read(ref _maxTickMilliseconds);
            if (milliseconds <= current || Interlocked.CompareExchange(ref _maxTickMilliseconds, milliseconds, current) == current) break;
        }
    }

    public SimulationSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _snapshotPublications);
            return new(1, _workerInstanceId, Interlocked.Increment(ref _sequence), now, now,
                _ghosts.Values.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => item.ToSnapshot()).ToArray(), Metrics);
        }
    }

    private SimulationCommandResult Create(SimulationCommand command)
    {
        var number = ++_nextNumber;
        var latitude = command.LatitudeDegrees ?? 43.65;
        var longitude = command.LongitudeDegrees ?? -79.38;
        ValidateCoordinate(latitude, longitude);
        var id = $"ghost-{number}";
        var profile = command.Profile is { } suppliedProfile
            ? ValidateProfile(command.ProfileId, suppliedProfile)
            : string.Equals(command.ProfileId, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase)
                ? GhostProfileDefaults.Dracula
                : throw new InvalidOperationException($"Unknown Ghost profile '{command.ProfileId}'.");
        _ghosts.Add(id, new GhostState(id, $"Ghost {number}", latitude, longitude, profile));
        return Result(command, true, "CREATED", $"Created {id}.", id);
    }

    private static GhostProfileSnapshot ValidateProfile(string requestedId, GhostProfileSnapshot profile)
    {
        if (!string.Equals(requestedId, profile.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The supplied Ghost profile does not match '{requestedId}'.");
        if (!string.Equals(profile.VehicleType, "Multicopter", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Multicopter Ghost profiles are supported.");
        var values = new[]
        {
            profile.Simulation.MaximumHorizontalSpeedMetresPerSecond,
            profile.Simulation.MaximumClimbRateMetresPerSecond,
            profile.Simulation.MaximumDescentRateMetresPerSecond,
            profile.Simulation.HorizontalAccelerationMetresPerSecondSquared,
            profile.Simulation.VerticalAccelerationMetresPerSecondSquared,
            profile.Simulation.MaximumYawRateDegreesPerSecond,
            profile.Simulation.MaximumAltitudeAglMetres,
            profile.Simulation.NominalEnduranceMinutes
        };
        if (values.Any(value => !double.IsFinite(value) || value <= 0))
            throw new InvalidOperationException("The supplied Ghost profile contains invalid simulation values.");
        return profile;
    }

    private SimulationCommandResult Delete(SimulationCommand command)
    {
        var ghost = Require(command);
        _ghosts.Remove(ghost.Id);
        return Result(command, true, "DELETED", $"Deleted {ghost.Name}.");
    }

    private SimulationCommandResult SetArmed(SimulationCommand command, bool armed)
    {
        var ghost = Require(command);
        ghost.Armed = armed;
        if (!armed) ghost.Operation = null;
        return Result(command, true, armed ? "ARMED" : "DISARMED", armed ? $"{ghost.Name} armed." : $"{ghost.Name} disarmed.");
    }

    private SimulationCommandResult StartOperation(SimulationCommand command, string name, double altitude)
    {
        var ghost = Require(command);
        if (!ghost.Armed && !string.Equals(name, "Land", StringComparison.OrdinalIgnoreCase))
            return Result(command, false, "NOT_ARMED", "Arm the Ghost before starting this operation.");
        ghost.Operation = new Operation(name == "Takeoff" ? SimulationCommandKind.Takeoff : name == "Land" ? SimulationCommandKind.Land : SimulationCommandKind.ChangeAltitude, altitude, null);
        return Result(command, true, "ACCEPTED", $"{name} accepted.");
    }

    private SimulationCommandResult StartGoTo(SimulationCommand command)
    {
        var ghost = Require(command);
        if (!ghost.Armed) return Result(command, false, "NOT_ARMED", "Arm the Ghost before moving it.");
        if (command.LatitudeDegrees is not { } latitude || command.LongitudeDegrees is not { } longitude)
            return Result(command, false, "INVALID_TARGET", "A Go To target requires latitude and longitude.");
        ValidateCoordinate(latitude, longitude);
        ghost.Operation = new(SimulationCommandKind.GoTo, command.AltitudeAglMetres ?? ghost.AltitudeAglMetres, new SimulationPoint(latitude, longitude, command.AltitudeAglMetres ?? ghost.AltitudeAglMetres));
        return Result(command, true, "ACCEPTED", "Go To accepted.");
    }

    private SimulationCommandResult StartRoute(SimulationCommand command)
    {
        var ghost = Require(command);
        if (!ghost.Armed) return Result(command, false, "NOT_ARMED", "Arm the Ghost before starting a route.");
        if (command.Route is not { Count: > 0 }) return Result(command, false, "INVALID_ROUTE", "The route contains no points.");
        foreach (var point in command.Route) ValidateCoordinate(point.LatitudeDegrees, point.LongitudeDegrees);
        ghost.Route = command.Route.ToArray();
        ghost.RouteIndex = 0;
        ghost.Operation = new(SimulationCommandKind.StartRoute, command.Route[0].AltitudeAglMetres, command.Route[0]);
        return Result(command, true, "ACCEPTED", $"Route with {ghost.Route.Length} points accepted.");
    }

    private SimulationCommandResult SetRoutePaused(SimulationCommand command, bool paused)
    {
        var ghost = Require(command);
        if (ghost.Operation?.Kind != SimulationCommandKind.StartRoute)
            return Result(command, false, "NO_ROUTE", "The Ghost has no active route.");
        ghost.RoutePaused = paused;
        return Result(command, true, paused ? "PAUSED" : "RESUMED", paused ? "Route paused." : "Route resumed.");
    }

    private SimulationCommandResult Hold(SimulationCommand command)
    {
        var ghost = Require(command);
        ghost.Operation = null;
        ghost.Route = null;
        ghost.NorthVelocityMetresPerSecond = ghost.EastVelocityMetresPerSecond = ghost.VerticalVelocityMetresPerSecond = 0;
        return Result(command, true, "HELD", $"{ghost.Name} is holding position.");
    }

    private static void StepNavigation(GhostState ghost, double elapsedSeconds)
    {
        if (ghost.Operation?.Kind == SimulationCommandKind.StartRoute && ghost.RoutePaused) return;
        var point = ghost.Operation?.Point;
        if (point is null) return;
        var north = (point.LatitudeDegrees - ghost.LatitudeDegrees) * Math.PI / 180d * EarthRadiusMetres;
        var east = (point.LongitudeDegrees - ghost.LongitudeDegrees) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(ghost.LatitudeDegrees * Math.PI / 180d);
        var distance = Math.Sqrt(north * north + east * east);
        var targetAltitude = point.AltitudeAglMetres;
        if (distance <= 1 && MoveAltitude(ghost, targetAltitude, elapsedSeconds))
        {
            if (ghost.Operation is { Kind: SimulationCommandKind.StartRoute } current && ghost.Route is { } route && ++ghost.RouteIndex < route.Length)
            {
                var next = route[ghost.RouteIndex];
                ghost.Operation = current with { Point = next, AltitudeAglMetres = next.AltitudeAglMetres };
            }
            else ghost.Operation = null;
            return;
        }
        if (distance <= 0.01) return;
        var step = Math.Min(distance, ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond * elapsedSeconds);
        ghost.LatitudeDegrees += north / distance * step / EarthRadiusMetres * 180d / Math.PI;
        ghost.LongitudeDegrees += east / distance * step / (EarthRadiusMetres * Math.Cos(ghost.LatitudeDegrees * Math.PI / 180d)) * 180d / Math.PI;
        ghost.NorthVelocityMetresPerSecond = north / distance * ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond;
        ghost.EastVelocityMetresPerSecond = east / distance * ghost.Profile.Simulation.MaximumHorizontalSpeedMetresPerSecond;
        ghost.VerticalVelocityMetresPerSecond = 0;
        ghost.HeadingDegrees = (Math.Atan2(east, north) * 180d / Math.PI + 360) % 360;
        MoveAltitude(ghost, targetAltitude, elapsedSeconds);
    }

    private static bool MoveAltitude(GhostState ghost, double target, double elapsedSeconds)
    {
        target = Math.Max(0, target);
        var difference = target - ghost.AltitudeAglMetres;
        var rate = difference >= 0 ? ghost.Profile.Simulation.MaximumClimbRateMetresPerSecond : ghost.Profile.Simulation.MaximumDescentRateMetresPerSecond;
        var step = Math.Min(Math.Abs(difference), rate * elapsedSeconds);
        ghost.AltitudeAglMetres += Math.Sign(difference) * step;
        ghost.Landed = ghost.AltitudeAglMetres <= 0.001;
        ghost.VerticalVelocityMetresPerSecond = Math.Sign(difference) * step / elapsedSeconds;
        return Math.Abs(target - ghost.AltitudeAglMetres) <= 0.01;
    }

    private GhostState Require(SimulationCommand command)
        => command.TargetId is not null && _ghosts.TryGetValue(command.TargetId, out var ghost)
            ? ghost
            : throw new InvalidOperationException("The target Ghost is unavailable.");

    private static SimulationCommandResult Result(SimulationCommand command, bool accepted, string code, string message, string? createdId = null)
        => new(command.RequestId, accepted, code, createdId is null ? message : $"{message} {createdId}", 0);

    private static void ValidateCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 || !double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude), "The coordinate is outside WGS84 bounds.");
    }

    private sealed class GhostState(string id, string name, double latitude, double longitude, GhostProfileSnapshot profile)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public GhostProfileSnapshot Profile { get; } = profile;
        public double LatitudeDegrees { get; set; } = latitude;
        public double LongitudeDegrees { get; set; } = longitude;
        public double AltitudeAglMetres { get; set; }
        public double HeadingDegrees { get; set; } = 90;
        public double NorthVelocityMetresPerSecond { get; set; }
        public double EastVelocityMetresPerSecond { get; set; }
        public double VerticalVelocityMetresPerSecond { get; set; }
        public bool Armed { get; set; }
        public bool Landed { get; set; } = true;
        public Operation? Operation { get; set; }
        public SimulationPoint[]? Route { get; set; }
        public int RouteIndex { get; set; }
        public bool RoutePaused { get; set; }

        public SimulationGhostSnapshot ToSnapshot() => new(Id, Name, LatitudeDegrees, LongitudeDegrees, AltitudeAglMetres, HeadingDegrees,
            NorthVelocityMetresPerSecond, EastVelocityMetresPerSecond, VerticalVelocityMetresPerSecond, Armed, Landed,
            Operation?.Kind.ToString() ?? "Hold", Operation?.Kind.ToString(), RouteIndex, Route?.Length ?? 0, Profile.Id);
    }

    private sealed record Operation(SimulationCommandKind Kind, double AltitudeAglMetres, SimulationPoint? Point);
}
