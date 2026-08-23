using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Core;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Simulation;

namespace RobotCommand.Services.Workflows;

internal static class FormationAirborneReadiness
{
    // A vehicle at or near the ground is not a valid formation participant,
    // even if the autopilot still reports it armed. This prevents an
    // interactive SITL takeoff failure from being mistaken for an airborne
    // formation lock and then triggering ArduPilot's landed auto-disarm.
    internal const double MinimumAirborneAltitudeAglMetres = 0.5d;

    internal static bool IsConfirmedAirborne(UnitTelemetryObservation telemetry)
        => telemetry.AltitudeAglMetres is { } altitude &&
           double.IsFinite(altitude) &&
           altitude >= MinimumAirborneAltitudeAglMetres &&
           (string.Equals(telemetry.LandedState, "Flying", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(telemetry.LandedState, "InAir", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(telemetry.LandedState, "Taking off", StringComparison.OrdinalIgnoreCase));

    internal static FormationLockFinding NotConfirmed(string name)
        => new("FORMATION_AIRBORNE_UNCONFIRMED", FormationLockSeverity.Blocking,
            $"{name} must report airborne telemetry at or above {MinimumAirborneAltitudeAglMetres:0.0} m AGL before joining a formation.");
}

internal static class FormationTargetConvergence
{
    internal readonly record struct Evaluation(bool IsConverged, string Reason);

    internal static bool IsWithin(UnitObservationSnapshot unit, FormationMemberTarget target, string expectedMode)
    {
        return Evaluate(unit, target, expectedMode).IsConverged;
    }

    internal static Evaluation Evaluate(UnitObservationSnapshot unit, FormationMemberTarget target, string expectedMode)
    {
        var telemetry = unit.Telemetry;
        if (telemetry is null)
            return new(false, "telemetry unavailable");
        if (telemetry.IsStale)
            return new(false, "telemetry stale");
        if (!string.Equals(telemetry.Mode, expectedMode, StringComparison.OrdinalIgnoreCase))
            return new(false, $"mode {telemetry.Mode ?? "unknown"} (expected {expectedMode})");
        if (telemetry.LatitudeDegrees is not { } latitude || telemetry.LongitudeDegrees is not { } longitude ||
            telemetry.AltitudeAglMetres is not { } altitude)
            return new(false, "position or altitude unavailable");

        var north = (latitude - target.LatitudeDegrees) * Math.PI / 180d * 6378137d;
        var east = (longitude - target.LongitudeDegrees) * Math.PI / 180d * 6378137d *
                   Math.Cos(target.LatitudeDegrees * Math.PI / 180d);
        var horizontalError = Math.Sqrt(north * north + east * east);
        var verticalError = Math.Abs(altitude - target.AltitudeAglMetres);
        var horizontalVelocity = Math.Sqrt(
            Math.Pow(telemetry.VelocityNorthMetresPerSecond ?? double.PositiveInfinity, 2) +
            Math.Pow(telemetry.VelocityEastMetresPerSecond ?? double.PositiveInfinity, 2));
        var verticalVelocity = Math.Abs(telemetry.VelocityDownMetresPerSecond ?? double.PositiveInfinity);
        if (!double.IsFinite(horizontalError) || horizontalError > 1.5d)
            return new(false, $"horizontal error {horizontalError:0.00} m");
        if (!double.IsFinite(verticalError) || verticalError > 0.75d)
            return new(false, $"vertical error {verticalError:0.00} m");
        if (!double.IsFinite(horizontalVelocity) || horizontalVelocity > 0.75d)
            return new(false, $"horizontal velocity {horizontalVelocity:0.00} m/s");
        if (!double.IsFinite(verticalVelocity) || verticalVelocity > 0.5d)
            return new(false, $"vertical velocity {verticalVelocity:0.00} m/s");
        return new(true, "converged");
    }
}

public sealed class GhostFormationLockExecutor : IFormationLockExecutor
{
    public string Backend => "Ghost simulator";
    public FormationControlCapabilities Capabilities =>
        FormationControlCapabilities.Translation |
        FormationControlCapabilities.Altitude |
        FormationControlCapabilities.Rotation |
        FormationControlCapabilities.Scale;
    public bool CanHandle(UnitObservationSnapshot unit) => unit.IsGhost;

    public Task<IReadOnlyList<FormationLockFinding>> ValidateAsync(IReadOnlyList<UnitObservationSnapshot> units, CancellationToken cancellationToken = default)
    {
        var findings = new List<FormationLockFinding>();
        foreach (var unit in units)
        {
            if (!unit.IsGhost)
                findings.Add(new("FORMATION_BACKEND_UNSUPPORTED", FormationLockSeverity.Blocking, $"{unit.Name} is not a Ghost simulator unit."));
            if (unit.State == ManagedConnectionState.Offline || unit.Telemetry is { IsStale: true } || unit.Telemetry is null)
                findings.Add(new("FORMATION_TELEMETRY_STALE", FormationLockSeverity.Blocking, $"{unit.Name} does not have current telemetry."));
            else
            {
                if (!unit.Telemetry.Armed)
                    findings.Add(new("FORMATION_NOT_ARMED", FormationLockSeverity.Blocking, $"{unit.Name} must be armed before locking formation."));
                if (string.Equals(unit.Telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase))
                    findings.Add(new("FORMATION_LANDED", FormationLockSeverity.Blocking, $"{unit.Name} must be airborne before locking formation."));
                if (unit.Telemetry.LatitudeDegrees is null || unit.Telemetry.LongitudeDegrees is null || unit.Telemetry.AltitudeAglMetres is null)
                    findings.Add(new("FORMATION_POSITION_UNAVAILABLE", FormationLockSeverity.Blocking, $"{unit.Name} does not have a valid 3D position."));
                else if (!FormationAirborneReadiness.IsConfirmedAirborne(unit.Telemetry))
                    findings.Add(FormationAirborneReadiness.NotConfirmed(unit.Name));
                if (!string.Equals(unit.Telemetry.Mode, "Simulated", StringComparison.OrdinalIgnoreCase))
                    findings.Add(new("FORMATION_MOTION_OWNED", FormationLockSeverity.Blocking, $"{unit.Name} is currently controlled by {unit.Telemetry.Mode}."));
            }
        }
        return Task.FromResult<IReadOnlyList<FormationLockFinding>>(findings);
    }
}

public sealed class UnsupportedFormationLockExecutor(string backend, Func<UnitObservationSnapshot, bool> matches) : IFormationLockExecutor
{
    public string Backend => backend;
    public bool CanHandle(UnitObservationSnapshot unit) => matches(unit);
    public Task<IReadOnlyList<FormationLockFinding>> ValidateAsync(IReadOnlyList<UnitObservationSnapshot> units, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FormationLockFinding>>([
            new("FORMATION_BACKEND_UNSUPPORTED", FormationLockSeverity.Blocking, $"{backend} formation lock is not implemented yet.")]);
}

/// <summary>PX4-specific bridge from the backend-neutral formation target to a local-NED Offboard stream.</summary>
public sealed class Px4FormationLockExecutor : IFormationLockExecutor
{
    private const double EarthRadiusMetres = 6378137d;
    private readonly IMavlinkConnectionRegistry _connections;
    private readonly ConcurrentDictionary<(string LockId, string UnitId), Px4FormationReference> _references = new();

    public Px4FormationLockExecutor(IMavlinkConnectionRegistry connections) => _connections = connections;
    public string Backend => "PX4 Offboard";
    public FormationControlCapabilities Capabilities =>
        FormationControlCapabilities.Translation |
        FormationControlCapabilities.Altitude |
        FormationControlCapabilities.Rotation |
        FormationControlCapabilities.Scale;
    public bool CanHandle(UnitObservationSnapshot unit) => !unit.IsGhost && unit.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase);
    public bool IsTargetConverged(UnitObservationSnapshot unit, FormationMemberTarget target)
        => FormationTargetConvergence.IsWithin(unit, target, "Offboard");
    public bool IsControlActive(UnitObservationSnapshot unit)
        => unit.Telemetry is { IsStale: false } telemetry &&
           string.Equals(telemetry.Mode, "Offboard", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<FormationLockFinding>> ValidateAsync(IReadOnlyList<UnitObservationSnapshot> units, CancellationToken cancellationToken = default)
    {
        var findings = new List<FormationLockFinding>();
        foreach (var unit in units)
        {
            if (!CanHandle(unit)) findings.Add(new("FORMATION_BACKEND_UNSUPPORTED", FormationLockSeverity.Blocking, $"{unit.Name} is not a PX4 multicopter."));
            if (unit.State == ManagedConnectionState.Offline || unit.Telemetry is null || unit.Telemetry.IsStale)
                findings.Add(new("FORMATION_TELEMETRY_STALE", FormationLockSeverity.Blocking, $"{unit.Name} does not have current PX4 telemetry."));
            else
            {
                if (!unit.Telemetry.Armed) findings.Add(new("FORMATION_NOT_ARMED", FormationLockSeverity.Blocking, $"{unit.Name} must be armed before locking formation."));
                if (string.Equals(unit.Telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase)) findings.Add(new("FORMATION_LANDED", FormationLockSeverity.Blocking, $"{unit.Name} must be airborne before locking formation."));
                if (unit.Telemetry.LatitudeDegrees is null || unit.Telemetry.LongitudeDegrees is null || unit.Telemetry.AltitudeAglMetres is null)
                    findings.Add(new("FORMATION_POSITION_UNAVAILABLE", FormationLockSeverity.Blocking, $"{unit.Name} does not have a valid 3D position."));
                else if (!FormationAirborneReadiness.IsConfirmedAirborne(unit.Telemetry))
                    findings.Add(FormationAirborneReadiness.NotConfirmed(unit.Name));
                if (unit.ConnectionIds.All(id => !_connections.TryGet(id, out _)))
                    findings.Add(new("FORMATION_PX4_CONNECTION_UNAVAILABLE", FormationLockSeverity.Blocking, $"{unit.Name} does not have an active PX4 MAVLink connection."));
            }
        }
        return Task.FromResult<IReadOnlyList<FormationLockFinding>>(findings);
    }

    public async Task<FormationExecutorResult> BeginAsync(UnitObservationSnapshot unit, FormationMemberTarget target, CancellationToken cancellationToken = default)
    {
        var connection = Resolve(unit);
        if (connection is null)
            return FormationExecutorResult.Rejected("PX4 MAVLink connection is unavailable for Offboard formation control.");
        if (!connection.TryGetFormationReference(unit.Id, out var north, out var east, out var down, out var error))
            return FormationExecutorResult.Rejected(error ?? "PX4 local position is unavailable for Offboard formation control.");
        if (unit.Telemetry?.LatitudeDegrees is not { } memberLatitude ||
            unit.Telemetry.LongitudeDegrees is not { } memberLongitude ||
            unit.Telemetry.AltitudeAglMetres is not { } memberAltitude)
            return FormationExecutorResult.Rejected("PX4 member position is unavailable while establishing the Offboard formation reference.");

        // LOCAL_POSITION_NED is anchored to the PX4 vehicle/local estimator
        // origin, not to the virtual Team position. Retain the member's
        // corresponding global pose so later Team-pivot targets convert into
        // the same local-NED frame without adding the member's formation
        // offset twice.
        var reference = new Px4FormationReference(connection, north, east, down,
            memberLatitude, memberLongitude, memberAltitude);
        _references[(target.LockId, unit.Id)] = reference;
        var result = await connection.BeginFormationControlAsync(unit.Id, target.LockId, ToSetpoint(target, reference), cancellationToken);
        return result.Accepted
            ? new(true, FormationMemberControlState.Active, result.Message, result.LastSetpointAt)
            : FormationExecutorResult.Rejected(result.Message);
    }

    public async Task<FormationExecutorResult> UpdateAsync(UnitObservationSnapshot unit, FormationMemberTarget target, CancellationToken cancellationToken = default)
    {
        if (!_references.TryGetValue((target.LockId, unit.Id), out var reference))
            return FormationExecutorResult.Rejected("PX4 Offboard formation reference is unavailable.");
        var result = await reference.Connection.UpdateFormationControlAsync(unit.Id, target.LockId, ToSetpoint(target, reference), cancellationToken);
        return result.Accepted
            ? new(true, result.State == "Offboard active" ? FormationMemberControlState.Active : FormationMemberControlState.Pending, result.Message, result.LastSetpointAt)
            : FormationExecutorResult.Rejected(result.Message);
    }

    public async Task<FormationExecutorResult> HoldAndReleaseAsync(UnitObservationSnapshot unit, string lockId, CancellationToken cancellationToken = default)
    {
        if (!_references.TryRemove((lockId, unit.Id), out var reference)) return FormationExecutorResult.AcceptedActive("PX4 formation session was already released.");
        var result = await reference.Connection.StopFormationControlAsync(unit.Id, lockId, "PX4 formation control released to Hold.", cancellationToken);
        return new(result.Accepted, FormationMemberControlState.Holding, result.Message, result.LastSetpointAt);
    }

    private MavlinkConnection? Resolve(UnitObservationSnapshot unit)
        => unit.ConnectionIds.Select(id => _connections.TryGet(id, out var connection) ? connection : null).FirstOrDefault(connection => connection is not null);

    private static Px4FormationSetpoint ToSetpoint(FormationMemberTarget target, Px4FormationReference reference)
    {
        // The Team position is the transform pivot, not the PX4 member's
        // destination. Rotation, resize, and mixed-backend formation offsets
        // are expressed in FormationMemberTarget and must survive the
        // conversion to PX4 local NED. Using the Team coordinates here made
        // PX4 ignore pure rotation/resize while Ghost members continued to
        // move correctly.
        var northDelta = (target.LatitudeDegrees - reference.MemberLatitude) * Math.PI / 180d * EarthRadiusMetres;
        var eastDelta = (target.LongitudeDegrees - reference.MemberLongitude) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(reference.MemberLatitude * Math.PI / 180d);
        var upDelta = target.AltitudeAglMetres - reference.MemberAltitudeAgl;
        return new((float)(reference.North + northDelta), (float)(reference.East + eastDelta), (float)(reference.Down - upDelta),
            (float)target.VelocityNorthMetresPerSecond, (float)target.VelocityEastMetresPerSecond, (float)-target.VelocityUpMetresPerSecond);
    }

    private sealed record Px4FormationReference(
        MavlinkConnection Connection,
        float North,
        float East,
        float Down,
        double MemberLatitude,
        double MemberLongitude,
        double MemberAltitudeAgl);
}

/// <summary>ArduCopter Guided bridge for transformed 3D formation control.</summary>
public sealed class ArduPilotFormationLockExecutor : IFormationLockExecutor
{
    private readonly IMavlinkConnectionRegistry _connections;

    public ArduPilotFormationLockExecutor(IMavlinkConnectionRegistry connections) => _connections = connections;

    public string Backend => "ArduPilot Guided";
    public FormationControlCapabilities Capabilities =>
        FormationControlCapabilities.Translation |
        FormationControlCapabilities.Altitude |
        FormationControlCapabilities.Rotation |
        FormationControlCapabilities.Scale;

    public bool CanHandle(UnitObservationSnapshot unit)
        => !unit.IsGhost && unit.ProfileKey.Contains("ardupilot", StringComparison.OrdinalIgnoreCase);
    public bool IsTargetConverged(UnitObservationSnapshot unit, FormationMemberTarget target)
        => FormationTargetConvergence.IsWithin(unit, target, "Guided");
    public bool IsControlActive(UnitObservationSnapshot unit)
        => unit.Telemetry is { IsStale: false } telemetry &&
           string.Equals(telemetry.Mode, "Guided", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<FormationLockFinding>> ValidateAsync(
        IReadOnlyList<UnitObservationSnapshot> units,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<FormationLockFinding>();
        foreach (var unit in units)
        {
            if (!CanHandle(unit))
                findings.Add(new("FORMATION_BACKEND_UNSUPPORTED", FormationLockSeverity.Blocking,
                    $"{unit.Name} is not an ArduPilot multicopter."));

            if (unit.State == ManagedConnectionState.Offline || unit.Telemetry is null || unit.Telemetry.IsStale)
            {
                findings.Add(new("FORMATION_TELEMETRY_STALE", FormationLockSeverity.Blocking,
                    $"{unit.Name} does not have current ArduPilot telemetry."));
                continue;
            }

            var telemetry = unit.Telemetry;
            if (!telemetry.Armed)
                findings.Add(new("FORMATION_NOT_ARMED", FormationLockSeverity.Blocking,
                    $"{unit.Name} must be armed before locking formation."));
            if (string.Equals(telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase))
                findings.Add(new("FORMATION_LANDED", FormationLockSeverity.Blocking,
                    $"{unit.Name} must be airborne before locking formation."));
            if (telemetry.LatitudeDegrees is null || telemetry.LongitudeDegrees is null || telemetry.AltitudeAglMetres is null)
                findings.Add(new("FORMATION_POSITION_UNAVAILABLE", FormationLockSeverity.Blocking,
                    $"{unit.Name} requires current global position and relative altitude telemetry."));
            else if (!FormationAirborneReadiness.IsConfirmedAirborne(telemetry))
                findings.Add(FormationAirborneReadiness.NotConfirmed(unit.Name));
            if (unit.ConnectionIds.All(id => !_connections.TryGet(id, out _)))
                findings.Add(new("FORMATION_ARDUPILOT_CONNECTION_UNAVAILABLE", FormationLockSeverity.Blocking,
                    $"{unit.Name} does not have an active ArduPilot MAVLink connection."));
            // Brake is the stable post-command Hold state used by Field
            // Console. Formation activation will transition it into Guided;
            // it is not an autonomous mission or safety mode that owns the
            // vehicle, so it must remain eligible for a new formation lock.
            if (string.Equals(telemetry.Mode, "Auto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(telemetry.Mode, "RTL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(telemetry.Mode, "Land", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new("FORMATION_MOTION_OWNED", FormationLockSeverity.Blocking,
                    $"{unit.Name} is currently in {telemetry.Mode}; exit the autonomous or safety mode before locking formation."));
            }
        }

        return Task.FromResult<IReadOnlyList<FormationLockFinding>>(findings);
    }

    public async Task<FormationExecutorResult> BeginAsync(
        UnitObservationSnapshot unit,
        FormationMemberTarget target,
        CancellationToken cancellationToken = default)
    {
        var connection = Resolve(unit);
        if (connection is null)
            return FormationExecutorResult.Rejected("ArduPilot MAVLink connection is unavailable for Guided formation control.");

        var result = await connection.BeginArduPilotFormationControlAsync(
            unit.Id,
            target.LockId,
            ToSetpoint(target),
            cancellationToken);
        return result.Accepted
            ? new(true, FormationMemberControlState.Active, result.Message, result.LastSetpointAt)
            : FormationExecutorResult.Rejected(result.Message);
    }

    public async Task<FormationExecutorResult> UpdateAsync(
        UnitObservationSnapshot unit,
        FormationMemberTarget target,
        CancellationToken cancellationToken = default)
    {
        var connection = Resolve(unit);
        if (connection is null)
            return FormationExecutorResult.Rejected("ArduPilot MAVLink connection is unavailable for Guided formation control.");

        var result = await connection.UpdateArduPilotFormationControlAsync(
            unit.Id,
            target.LockId,
            ToSetpoint(target),
            cancellationToken);
        return result.Accepted
            ? new(true, result.State == "Guided active" ? FormationMemberControlState.Active : FormationMemberControlState.Pending,
                result.Message, result.LastSetpointAt)
            : FormationExecutorResult.Rejected(result.Message);
    }

    public async Task<FormationExecutorResult> HoldAndReleaseAsync(
        UnitObservationSnapshot unit,
        string lockId,
        CancellationToken cancellationToken = default)
    {
        var connection = Resolve(unit);
        if (connection is null)
            return FormationExecutorResult.Rejected("ArduPilot MAVLink connection is unavailable while releasing formation control.");

        var result = await connection.StopArduPilotFormationControlAsync(
            unit.Id,
            lockId,
            "ArduPilot formation control released to stable Hold (Brake).",
            cancellationToken);
        return new(result.Accepted, result.Accepted ? FormationMemberControlState.Holding : FormationMemberControlState.Failed,
            result.Message, result.LastSetpointAt);
    }

    private MavlinkConnection? Resolve(UnitObservationSnapshot unit)
        => unit.ConnectionIds
            .Select(id => _connections.TryGet(id, out var connection) ? connection : null)
            .FirstOrDefault(connection => connection is not null);

    private static ArduPilotFormationSetpoint ToSetpoint(FormationMemberTarget target)
        => new(
            target.LatitudeDegrees,
            target.LongitudeDegrees,
            target.AltitudeAglMetres,
            (float)target.VelocityNorthMetresPerSecond,
            (float)target.VelocityEastMetresPerSecond,
            (float)-target.VelocityUpMetresPerSecond);
}

/// <summary>
/// Owns the stable virtual Team position. It sends absolute member targets to
/// Ghost physics; only GhostUnitService mutates vehicle motion and telemetry.
/// </summary>
public sealed class FormationLockWorkflow : IFormationLockWorkflow, IHostedService, IAsyncDisposable
{
    private const double EarthRadiusMetres = 6378137d;
    private const double HorizontalSpeedMetresPerSecond = 10d;
    private const double VerticalSpeedMetresPerSecond = 2d;
    private const double TickSeconds = 1d / 60d;
    // Keep transform feed-forward below the ordinary navigation limit. The
    // Ghost controller supplies the damped final approach rather than making
    // each member chase a full-speed moving target.
    private const double TransformHorizontalSpeedMetresPerSecond = 4d;
    private const double TransformVerticalSpeedMetresPerSecond = 2d;
    private const double MaximumRotationDegreesPerSecond = 30d;
    private const double MaximumScalePerSecond = 0.3d;
    // The controller is driven by a wall-clock timer and backend dispatch can
    // consume a tick. Snap the last small transform residue to the requested
    // target instead of leaving a maneuver permanently in Moving.
    private const double RotationCompletionDeadbandDegrees = 0.75d;
    private const double ScaleCompletionDeadband = 0.005d;
    // Leave room for the Ghost target controller to correct tracking error.
    // During a combined transform, a member's Team translation and its own
    // rotation/scale velocity must fit within this single motion budget.
    private const double CombinedTransformHorizontalSpeedMetresPerSecond = 7d;
    private const double ManeuverTimeoutMinimumSeconds = 10d;
    private const double ManeuverTimeoutMaximumSeconds = 60d;
    private const double ManeuverTimeoutMarginSeconds = 8d;
    private readonly object _gate = new();
    private readonly ITeamWorkflow _teams;
    private readonly IUnitObservationWorkflow _units;
    private readonly IGhostUnitService _ghosts;
    private readonly ReviewedOperationWorkflow _reviewed;
    private readonly IReadOnlyList<IFormationLockExecutor> _executors;
    private readonly ILogger<FormationLockWorkflow>? _logger;
    private readonly FormationAssignmentFreezeRegistry? _assignmentFreezeRegistry;
    private readonly CancellationTokenSource _shutdown = new();
    // Serialize target capture and dispatch. Operator actions can arrive while
    // the 60 Hz controller is awaiting a backend, and an older batch must not
    // overtake a newer formation action.
    private readonly SemaphoreSlim _pushGate = new(1, 1);
    private Task? _loop;
    private int _loopDispatchPending;
    private FormationState? _state;
    private FormationLockSnapshot _snapshot = FormationLockSnapshot.Unlocked;
    private DateTimeOffset _lastPeriodicPublish = DateTimeOffset.MinValue;
    private int _stopped;
    private int _disposed;

    public FormationLockWorkflow(
        ITeamWorkflow teams,
        IUnitObservationWorkflow units,
        IGhostUnitService ghosts,
        ReviewedOperationWorkflow reviewed,
        IEnumerable<IFormationLockExecutor> executors,
        ILogger<FormationLockWorkflow>? logger = null,
        FormationAssignmentFreezeRegistry? assignmentFreezeRegistry = null)
    {
        _teams = teams;
        _units = units;
        _ghosts = ghosts;
        _reviewed = reviewed;
        _executors = executors.ToArray();
        _logger = logger;
        _assignmentFreezeRegistry = assignmentFreezeRegistry;
        _teams.Changed += OnTeamsChanged;
        _units.Changed += OnUnitsChanged;
    }

    public event EventHandler? Changed;
    public FormationLockSnapshot Current { get { lock (_gate) return _snapshot; } }

    public bool IsUnitLocked(string unitId)
    {
        lock (_gate) return _state?.Members.ContainsKey(unitId) == true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureLoopStarted();
        return Task.CompletedTask;
    }

    private void EnsureLoopStarted()
    {
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested || (_loop is not null && !_loop.IsCompleted)) return;
            _loop = Task.Run(() => LoopAsync(_shutdown.Token), _shutdown.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        await UnlockAsync(null, "Formation controller stopped.", CancellationToken.None);
    }

    public Task<FormationLockSnapshot> LockAsync(string teamId, CancellationToken cancellationToken = default)
        => LockCoreAsync(teamId, allowAssignedFormation: false, cancellationToken: cancellationToken);

    private async Task<FormationLockSnapshot> LockCoreAsync(
        string teamId,
        bool allowAssignedFormation,
        CancellationToken cancellationToken)
    {
        EnsureLoopStarted();
        if (!_teams.TryGet(teamId, out var team) || team is null)
            throw new KeyNotFoundException($"Team '{teamId}' was not found.");
        if (!allowAssignedFormation && _assignmentFreezeRegistry?.IsTeamFrozen(teamId) == true)
            throw new InvalidOperationException("This Team has an authored formation assigned. Clear the authored formation before creating a temporary formation lock.");
        var members = team.Members.OrderBy(member => member.Order).Select(member => RequireUnit(member.UnitId)).ToArray();
        if (members.Length < 2)
            throw new InvalidOperationException("A formation lock requires at least two Team members.");
        var assignments = members.Select(member => new
        {
            Unit = member,
            Executor = _executors.FirstOrDefault(candidate => candidate.CanHandle(member))
        }).ToArray();
        if (assignments.Any(item => item.Executor is null))
            throw new InvalidOperationException("The selected Team contains an unsupported formation backend.");
        var findings = new List<FormationLockFinding>();
        foreach (var group in assignments.GroupBy(item => item.Executor!))
            findings.AddRange(await group.Key.ValidateAsync(group.Select(item => item.Unit).ToArray(), cancellationToken));
        if (findings.Any(item => item.Severity == FormationLockSeverity.Blocking))
            throw new InvalidOperationException(string.Join(" ", findings.Where(item => item.Severity == FormationLockSeverity.Blocking).Select(item => item.Message)));

        var latitude = members.Average(member => member.Telemetry!.LatitudeDegrees!.Value);
        var longitude = members.Average(member => member.Telemetry!.LongitudeDegrees!.Value);
        var altitude = members.Average(member => member.Telemetry!.AltitudeAglMetres!.Value);
        var lockId = $"formation-{Guid.NewGuid():N}";
        var state = new FormationState(lockId, team.Id, team.Name, latitude, longitude, altitude);
        foreach (var assignment in assignments)
        {
            var telemetry = assignment.Unit.Telemetry!;
            state.Members[assignment.Unit.Id] = new FormationMember(assignment.Unit.Id, assignment.Unit.Name, assignment.Executor!,
                North(latitude, telemetry.LatitudeDegrees!.Value),
                East(latitude, longitude, telemetry.LongitudeDegrees!.Value),
                telemetry.AltitudeAglMetres!.Value - altitude);
        }
        // Lock activation is itself a convergence operation. The first
        // target batch may be accepted before the freshest backend telemetry
        // has been observed, so keep the controller publishing and checking
        // until every member has demonstrated stable control at its captured
        // offset.
        BeginManeuverLocked(state, "formation lock");
        lock (_gate) _state = state;
        try
        {
            await PushTargetsAsync(state, cancellationToken, begin: true);
        }
        catch
        {
            await UnlockAsync(teamId, "Formation lock could not activate every member.", CancellationToken.None);
            throw;
        }
        Publish();
        return Current;
    }

    /// <summary>Capture the current lock and retarget its members to an authored layout.</summary>
    public async Task<FormationLockSnapshot> TransitionToLayoutAsync(
        string teamId,
        IReadOnlyDictionary<string, (double North, double East, double Up)> offsets,
        CancellationToken cancellationToken = default)
    {
        var current = Current;
        if (!current.IsLocked || !string.Equals(current.TeamId, teamId, StringComparison.Ordinal))
            await LockCoreAsync(teamId, allowAssignedFormation: true, cancellationToken: cancellationToken);

        FormationState state;
        lock (_gate)
        {
            state = RequireLocked(teamId);
            if (offsets.Count != state.Members.Count || state.Members.Keys.Any(id => !offsets.ContainsKey(id)))
                throw new InvalidOperationException("The authored formation does not contain exactly one target slot for every Team member.");
            foreach (var member in state.Members.Values)
            {
                var target = offsets[member.UnitId];
                if (!double.IsFinite(target.North) || !double.IsFinite(target.East) || !double.IsFinite(target.Up))
                    throw new ArgumentException("Formation target offsets must be finite.", nameof(offsets));
                member.NorthOffset = target.North;
                member.EastOffset = target.East;
                member.UpOffset = target.Up;
            }
            state.RotationDegrees = 0d;
            state.TargetRotationDegrees = 0d;
            state.Scale = 1d;
            state.TargetScale = 1d;
            state.TargetRevision++;
            BeginManeuverLocked(state, "formation entry");
        }
        await PushActionTargetsAsync(state, cancellationToken);
        Publish(force: true);
        return Current;
    }

    public async Task<FormationLockSnapshot> UnlockAsync(string? teamId = null, string reason = "Operator unlocked the formation.", CancellationToken cancellationToken = default)
    {
        FormationState? state;
        lock (_gate)
        {
            state = _state;
            if (state is null || (teamId is not null && !string.Equals(teamId, state.TeamId, StringComparison.Ordinal))) return _snapshot;
            _state = null;
            _snapshot = FormationLockSnapshot.Unlocked with
            {
                InterruptionReason = reason,
                InterruptionCode = state.InterruptionCode,
                Findings = state.Findings,
                CapturedAt = DateTimeOffset.UtcNow
            };
        }
        foreach (var member in state.Members.Values)
            await ReleaseMemberAsync(state, member, cancellationToken);
        Publish();
        return Current;
    }

    public Task<ReviewedOperationSnapshot> PlanMoveToAsync(string teamId, double latitudeDegrees, double longitudeDegrees, CancellationToken cancellationToken = default)
    {
        var findings = ValidateMove(teamId, latitudeDegrees, longitudeDegrees);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.TeamFormationGoTo, "Move Team formation",
            findings, [$"Team position to {latitudeDegrees:F6}, {longitudeDegrees:F6}"], "Move the locked Team position while preserving member offsets.", [teamId], async token =>
            {
                await MoveToAsync(teamId, latitudeDegrees, longitudeDegrees, token);
                return new(string.Empty, ReviewedOperationState.Succeeded, true, "Formation movement accepted.", []);
            }));
    }

    public Task<ReviewedOperationSnapshot> PlanChangeAltitudeAsync(string teamId, double altitudeAglMetres, CancellationToken cancellationToken = default)
    {
        var findings = ValidateAltitude(teamId, altitudeAglMetres);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.TeamFormationAltitude, "Change Team formation altitude",
            findings, [$"Team position altitude to {altitudeAglMetres:F1} m AGL"], "Change the locked Team position altitude while preserving member offsets.", [teamId], async token =>
            {
                await ChangeAltitudeAsync(teamId, altitudeAglMetres, token);
                return new(string.Empty, ReviewedOperationState.Succeeded, true, "Formation altitude change accepted.", []);
            }));
    }

    public Task<ReviewedOperationSnapshot> PlanRotateAsync(string teamId, double signedDegrees, CancellationToken cancellationToken = default)
    {
        var findings = ValidateTransform(teamId, signedDegrees, isScale: false);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.TeamFormationRotate, "Rotate Team formation",
            findings, [$"Formation rotation by {signedDegrees:F1}° clockwise"], "Rotate the locked formation around its Team position.", [teamId], async token =>
            {
                await RotateAsync(teamId, signedDegrees, token);
                return new(string.Empty, ReviewedOperationState.Succeeded, true, "Formation rotation accepted.", []);
            }));
    }

    public Task<ReviewedOperationSnapshot> PlanScaleAsync(string teamId, double percent, CancellationToken cancellationToken = default)
    {
        var findings = ValidateTransform(teamId, percent, isScale: true);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.TeamFormationScale, "Resize Team formation",
            findings, [$"Formation size to {percent:F0}%"], "Resize the locked formation around its Team position.", [teamId], async token =>
            {
                await ScaleAsync(teamId, percent, token);
                return new(string.Empty, ReviewedOperationState.Succeeded, true, "Formation resize accepted.", []);
            }));
    }

    public Task<ReviewedOperationSnapshot> PlanHoldAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var findings = ValidateHold(teamId);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.TeamFormationHold, "Hold Team formation",
            findings, ["Hold the Team at its current position"], "Stop the locked Team at its current virtual position without unlocking it.", [teamId], async token =>
            {
                await HoldAsync(teamId, token);
                return new(string.Empty, ReviewedOperationState.Succeeded, true, "Formation hold accepted.", []);
            }));
    }

    public async Task<FormationLockSnapshot> MoveToAsync(string teamId, double latitudeDegrees, double longitudeDegrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90 || !double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitudeDegrees), "Team Go To requires valid WGS84 latitude and longitude.");
        FormationState state;
        lock (_gate)
        {
            state = RequireLocked(teamId);
            state.TargetLatitude = latitudeDegrees;
            state.TargetLongitude = longitudeDegrees;
            state.TargetRevision++;
            BeginManeuverLocked(state, "translation");
        }
        // Never send the requested final position directly to a member. The
        // 60 Hz controller is the sole owner of every moving target, so all
        // members receive a position and feed-forward velocity from the same
        // instant of the virtual Team state. This is what keeps a later
        // rotate/resize from reintroducing a stale final-position command.
        await PushActionTargetsAsync(state, cancellationToken);
        Publish();
        return Current;
    }

    public async Task<FormationLockSnapshot> ChangeAltitudeAsync(string teamId, double altitudeAglMetres, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(altitudeAglMetres) || altitudeAglMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(altitudeAglMetres), "Team altitude must be a non-negative finite AGL value.");
        FormationState state;
        lock (_gate)
        {
            state = RequireLocked(teamId);
            state.TargetAltitude = altitudeAglMetres;
            state.TargetRevision++;
            BeginManeuverLocked(state, "altitude change");
        }
        await PushActionTargetsAsync(state, cancellationToken);
        Publish();
        return Current;
    }

    public async Task<FormationLockSnapshot> RotateAsync(string teamId, double signedDegrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(signedDegrees) || signedDegrees is < -360d or > 360d)
            throw new ArgumentOutOfRangeException(nameof(signedDegrees), "Formation rotation must be between -360° and 360°.");
        FormationState state;
        lock (_gate)
        {
            state = RequireTransformLocked(teamId, FormationControlCapabilities.Rotation);
            state.TargetRotationDegrees += signedDegrees;
            state.TargetRevision++;
            BeginManeuverLocked(state, "rotation");
        }
        await PushActionTargetsAsync(state, cancellationToken);
        Publish();
        return Current;
    }

    public async Task<FormationLockSnapshot> ScaleAsync(string teamId, double percent, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(percent) || percent is < 50d or > 200d)
            throw new ArgumentOutOfRangeException(nameof(percent), "Formation size must be between 50% and 200%.");
        FormationState state;
        lock (_gate)
        {
            state = RequireTransformLocked(teamId, FormationControlCapabilities.Scale);
            state.TargetScale = percent / 100d;
            state.TargetRevision++;
            BeginManeuverLocked(state, "resize");
        }
        await PushActionTargetsAsync(state, cancellationToken);
        Publish();
        return Current;
    }

    public async Task<FormationLockSnapshot> HoldAsync(string teamId, CancellationToken cancellationToken = default)
    {
        FormationState state;
        lock (_gate)
        {
            state = RequireLocked(teamId);
            // Hold is a formation-level stop, not a return-to-last-commanded
            // point. Reconstruct the virtual Team position from the live
            // member pose so a late correction cannot pull the Team back to
            // an old virtual location.
            RebaseToObservedPose(state);
            state.TargetLatitude = state.Latitude;
            state.TargetLongitude = state.Longitude;
            state.TargetAltitude = state.Altitude;
            state.TargetRotationDegrees = state.RotationDegrees;
            state.TargetScale = state.Scale;
            state.VelocityNorth = 0d;
            state.VelocityEast = 0d;
            state.VelocityUp = 0d;
            state.RotationVelocityRadiansPerSecond = 0d;
            state.ScaleVelocityPerSecond = 0d;
            state.TargetRevision++;
            state.RequiresConvergence = false;
            state.ManeuverStartedAt = null;
            state.ManeuverDeadline = null;
            state.TargetStableSince = null;
        }
        await PushTargetsAsync(state, cancellationToken);
        Publish(force: true);
        return Current;
    }

    public async Task HandleIndependentOperationAsync(string unitId, OperatorWorkflowCommandKind command, CancellationToken cancellationToken = default)
    {
        FormationState? state;
        lock (_gate) state = _state?.Members.ContainsKey(unitId) == true ? _state : null;
        if (state is null) return;
        if (command is OperatorWorkflowCommandKind.Land or OperatorWorkflowCommandKind.Disarm)
        {
            await DropMemberAsync(unitId, $"{command} removed {unitId} from the formation.", cancellationToken);
            return;
        }
        await UnlockAsync(state.TeamId, $"Formation released for independent {command}.", cancellationToken);
    }

    public async Task HandleUnitDeletedAsync(string unitId, CancellationToken cancellationToken = default)
    {
        FormationState? state;
        lock (_gate) state = _state?.Members.ContainsKey(unitId) == true ? _state : null;
        if (state is not null)
            await UnlockAsync(state.TeamId, "Formation interrupted because a member was deleted.", cancellationToken);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickSeconds));
        var lastTick = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var currentTick = Stopwatch.GetTimestamp();
                    // The loop is scheduled at 60 Hz, but target publication
                    // can be delayed by a busy host. Integrate actual elapsed
                    // time so healthy formations do not run in slow motion.
                    var elapsedSeconds = Math.Clamp((currentTick - lastTick) / (double)Stopwatch.Frequency, 0.001d, 0.1d);
                    lastTick = currentTick;
                    FormationState? state;
                    bool changed;
                    bool isMoving;
                    string? maneuverFailure = null;
                    lock (_gate)
                    {
                        state = _state;
                        if (state is null) continue;
                        // Team actions can arrive on a UI/CLI thread while
                        // the controller is advancing position, rotation, and
                        // scale. Mutate one coherent transform sample so a
                        // target batch can never combine old and new fields.
                        changed = Advance(state, elapsedSeconds);
                        isMoving = IsMoving(state);
                        // IsMoving deliberately has a small presentation
                        // deadband. Do not use it to decide whether the
                        // physical setpoint stream is finished: a Team can be
                        // displayed as settled while it is still a few
                        // centimetres short of its exact target. Only the
                        // exact target state is allowed to publish a final
                        // zero-velocity hold.
                        if (IsAtExactTarget(state))
                        {
                            // A transform can land exactly on its requested
                            // shape in this tick. Do not leave Ghosts with the
                            // final tick's tangential/radial feed-forward
                            // velocity: it would keep moving them after the
                            // Team position and transform have stopped.
                            state.VelocityNorth = 0d;
                            state.VelocityEast = 0d;
                            state.VelocityUp = 0d;
                            state.RotationVelocityRadiansPerSecond = 0d;
                            state.ScaleVelocityPerSecond = 0d;
                        }

                        if (state.RequiresConvergence && state.ManeuverDeadline is { } deadline &&
                            DateTimeOffset.UtcNow >= deadline)
                        {
                            var failed = state.Members.Values.FirstOrDefault(member =>
                                !_units.TryGet(member.UnitId, out var unit) || unit is null ||
                                !IsMemberConverged(state, member, unit).IsConverged);
                            if (failed is not null)
                            {
                                maneuverFailure =
                                    $"Formation {state.ManeuverName} stopped because {failed.Name} did not converge in {failed.Executor.Backend} control before the safety timeout. The entire Team was released to Hold.";
                                state.InterruptionCode = "FORMATION_MEMBER_NOT_CONVERGING";
                                state.Findings =
                                [new("FORMATION_MEMBER_NOT_CONVERGING", FormationLockSeverity.Blocking, maneuverFailure)];
                            }
                        }
                    }
                    if (maneuverFailure is not null)
                    {
                        await UnlockAsync(state!.TeamId, maneuverFailure, CancellationToken.None);
                        continue;
                    }
                    // Continue publishing while the virtual Team pose is
                    // settled but a live backend still has not confirmed the
                    // member target. This keeps the status snapshot honest
                    // and lets telemetry convergence clear Moving without a
                    // new operator command.
                    if (changed)
                    {
                        // Ghost physics owns its own target pursuit, while PX4
                        // receives the moving virtual Team position plus a
                        // velocity feed-forward in its independent Offboard
                        // stream. This avoids asking PX4 to jump directly to
                        // the final Team destination.
                        // The continuous controller always publishes the
                        // current virtual Team state. One-time UI/CLI actions
                        // may use the requested target for immediate visual
                        // response, but doing so from this loop couples a
                        // fixed final position to an in-flight velocity.
                        // Backend dispatch must not stall the 60 Hz virtual
                        // formation controller. Live adapters own their
                        // steady 20 Hz streams; this task only publishes the
                        // latest target into those streams (or the Ghost
                        // simulator). Older queued poses are discarded by
                        // TargetRevision/PoseRevision validation.
                        ScheduleLoopDispatch(state);
                        // The last motion step can fall inside the normal
                        // 20 Hz observer throttle. Force that final snapshot
                        // so a completed transform never leaves the UI/CLI
                        // showing a stale near-target angle or size.
                        // The terminal transform sample must not be lost to
                        // the normal 20 Hz publication throttle. Otherwise
                        // the internal controller can be exactly at the
                        // requested angle/scale while the public snapshot is
                        // left showing the last near-target sample forever.
                        Publish(force: IsAtExactTarget(state));
                    }
                    else if (state.RequiresConvergence)
                    {
                        // A backend pump owns the last target after the
                        // virtual pose settles. Keep publishing convergence
                        // status, but do not rewrite an identical target at
                        // 60 Hz and compete with the backend's own stream.
                        Publish(force: false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Formation controller tick failed.");
                    FormationState? failed;
                    lock (_gate) failed = _state;
                    if (failed is not null)
                    {
                        var reason = $"Formation interrupted because a member controller failed ({ex.GetType().Name}): {ex.Message}";
                        lock (_gate)
                        {
                            if (ReferenceEquals(_state, failed))
                            {
                                failed.InterruptionCode = "FORMATION_BACKEND_FAILURE";
                                failed.Findings =
                                [new("FORMATION_BACKEND_FAILURE", FormationLockSeverity.Blocking, reason)];
                            }
                        }
                        await UnlockAsync(failed.TeamId, reason, CancellationToken.None);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static bool Advance(FormationState state, double elapsedSeconds)
    {
        var changed = false;
        state.VelocityNorth = 0;
        state.VelocityEast = 0;
        state.VelocityUp = 0;

        var maximumRadius = state.Members.Values
            .Select(member => Math.Sqrt(member.NorthOffset * member.NorthOffset + member.EastOffset * member.EastOffset))
            .DefaultIfEmpty(0d)
            .Max();
        var maxAngularDegrees = maximumRadius <= 0.01d
            ? MaximumRotationDegreesPerSecond
            : Math.Min(MaximumRotationDegreesPerSecond,
                TransformHorizontalSpeedMetresPerSecond / maximumRadius * 180d / Math.PI);
        var rotationDifference = state.TargetRotationDegrees - state.RotationDegrees;
        if (Math.Abs(rotationDifference) > RotationCompletionDeadbandDegrees)
        {
            var step = Math.Sign(rotationDifference) * Math.Min(Math.Abs(rotationDifference), maxAngularDegrees * elapsedSeconds);
            state.RotationVelocityRadiansPerSecond = step / elapsedSeconds * Math.PI / 180d;
            state.RotationDegrees += step;
            changed = true;
        }
        else
        {
            state.RotationVelocityRadiansPerSecond = 0d;
            // Snap the presentation/target transform to the requested angle
            // once it is within the motion deadband. Leaving the residual
            // value here caused a locked formation to remain permanently a
            // few thousandths of a degree short of its requested shape.
            changed |= state.RotationDegrees != state.TargetRotationDegrees;
            state.RotationDegrees = state.TargetRotationDegrees;
        }

        var maxVerticalOffset = state.Members.Values.Select(member => Math.Abs(member.UpOffset)).DefaultIfEmpty(0d).Max();
        var maxScaleRate = MaximumScalePerSecond;
        if (maximumRadius > 0.01d)
            maxScaleRate = Math.Min(maxScaleRate, TransformHorizontalSpeedMetresPerSecond / maximumRadius);
        if (maxVerticalOffset > 0.01d)
            maxScaleRate = Math.Min(maxScaleRate, TransformVerticalSpeedMetresPerSecond / maxVerticalOffset);
        var scaleDifference = state.TargetScale - state.Scale;
        if (Math.Abs(scaleDifference) > ScaleCompletionDeadband)
        {
            var step = Math.Sign(scaleDifference) * Math.Min(Math.Abs(scaleDifference), maxScaleRate * elapsedSeconds);
            state.ScaleVelocityPerSecond = step / elapsedSeconds;
            state.Scale += step;
            changed = true;
        }
        else
        {
            state.ScaleVelocityPerSecond = 0d;
            // Scale is absolute relative to lock-time offsets. As with
            // rotation, remove the final deadband residue so 100%, 150%, and
            // 50% are exact stable transforms rather than approximate ones.
            changed |= state.Scale != state.TargetScale;
            state.Scale = state.TargetScale;
        }

        // Compute the virtual Team translation only after the transform
        // velocities are known. Otherwise Team velocity (up to 10 m/s) and an
        // outer member's rotation/scale velocity could be added together into
        // an impossible target, causing the outer Ghosts to trail or orbit.
        var north = North(state.Latitude, state.TargetLatitude);
        var east = East(state.Latitude, state.Longitude, state.TargetLongitude);
        var distance = Math.Sqrt(north * north + east * east);
        if (distance > 0.05)
        {
            var directionNorth = north / distance;
            var directionEast = east / distance;
            var maximumTeamSpeed = MaximumSafeTeamHorizontalSpeed(state, directionNorth, directionEast);
            var step = Math.Min(distance, maximumTeamSpeed * elapsedSeconds);
            if (step > 0)
            {
                state.VelocityNorth = directionNorth * step / elapsedSeconds;
                state.VelocityEast = directionEast * step / elapsedSeconds;
                state.Latitude += directionNorth * step / EarthRadiusMetres * 180d / Math.PI;
                state.Longitude += directionEast * step / (EarthRadiusMetres * Math.Cos(state.Latitude * Math.PI / 180d)) * 180d / Math.PI;
                changed = true;
            }
        }
        else if (state.Latitude != state.TargetLatitude || state.Longitude != state.TargetLongitude)
        {
            // Finish inside the positional deadband at the exact requested
            // virtual Team position. Without this terminal snap no final
            // zero-velocity hold setpoint was emitted after a combined
            // translation/transform, leaving members with stale feed-forward
            // motion.
            state.Latitude = state.TargetLatitude;
            state.Longitude = state.TargetLongitude;
            changed = true;
        }
        var altitudeDifference = state.TargetAltitude - state.Altitude;
        if (Math.Abs(altitudeDifference) > 0.005)
        {
            var direction = Math.Sign(altitudeDifference);
            var maximumTeamSpeed = MaximumSafeTeamVerticalSpeed(state, direction);
            var step = direction * Math.Min(Math.Abs(altitudeDifference), maximumTeamSpeed * elapsedSeconds);
            if (Math.Abs(step) > 0)
            {
                state.VelocityUp = step / elapsedSeconds;
                state.Altitude += step;
                changed = true;
            }
        }
        else if (state.Altitude != state.TargetAltitude)
        {
            state.Altitude = state.TargetAltitude;
            changed = true;
        }
        if (changed)
            state.PoseRevision++;
        return changed;
    }

    private static bool IsMoving(FormationState state) =>
        Distance(state.Latitude, state.Longitude, state.TargetLatitude, state.TargetLongitude) > 0.1d ||
        Math.Abs(state.Altitude - state.TargetAltitude) > 0.01d ||
        Math.Abs(state.RotationDegrees - state.TargetRotationDegrees) > RotationCompletionDeadbandDegrees ||
        Math.Abs(state.Scale - state.TargetScale) > ScaleCompletionDeadband;

    private static bool IsAtExactTarget(FormationState state) =>
        state.Latitude == state.TargetLatitude &&
        state.Longitude == state.TargetLongitude &&
        state.Altitude == state.TargetAltitude &&
        state.RotationDegrees == state.TargetRotationDegrees &&
        state.Scale == state.TargetScale;

    private static double MaximumSafeTeamHorizontalSpeed(FormationState state, double directionNorth, double directionEast)
    {
        var limit = IsTransforming(state)
            ? CombinedTransformHorizontalSpeedMetresPerSecond
            : HorizontalSpeedMetresPerSecond;
        foreach (var member in state.Members.Values)
        {
            var transform = TransformOffset(state, member);
            var velocity = TransformVelocity(state, member, transform);
            var dot = directionNorth * velocity.North + directionEast * velocity.East;
            var magnitudeSquared = velocity.North * velocity.North + velocity.East * velocity.East;
            var discriminant = limit * limit - magnitudeSquared + dot * dot;
            if (discriminant <= 0d)
                return 0d;
            limit = Math.Min(limit, Math.Max(0d, -dot + Math.Sqrt(discriminant)));
        }
        return limit;
    }

    private static double MaximumSafeTeamVerticalSpeed(FormationState state, double direction)
    {
        var limit = VerticalSpeedMetresPerSecond;
        if (Math.Abs(state.ScaleVelocityPerSecond) <= 0.000001d &&
            Math.Abs(state.TargetScale - state.Scale) <= ScaleCompletionDeadband)
            return limit;
        foreach (var member in state.Members.Values)
        {
            var velocity = TransformVelocity(state, member, TransformOffset(state, member));
            limit = Math.Min(limit, Math.Max(0d, direction > 0d
                ? VerticalSpeedMetresPerSecond - velocity.Up
                : VerticalSpeedMetresPerSecond + velocity.Up));
        }
        return limit;
    }

    private static void BeginManeuverLocked(FormationState state, string maneuverName)
    {
        state.ManeuverName = maneuverName;
        state.ManeuverStartedAt = DateTimeOffset.UtcNow;
        state.ManeuverDeadline = state.ManeuverStartedAt.Value + EstimateManeuverTimeout(state);
        state.RequiresConvergence = true;
        state.TargetStableSince = null;
        state.InterruptionCode = null;
        state.Findings = [];
    }

    private static TimeSpan EstimateManeuverTimeout(FormationState state)
    {
        var translationMetres = Distance(state.Latitude, state.Longitude, state.TargetLatitude, state.TargetLongitude);
        var maximumRadius = state.Members.Values
            .Select(member => Math.Sqrt(member.NorthOffset * member.NorthOffset + member.EastOffset * member.EastOffset))
            .DefaultIfEmpty(0d)
            .Max();
        var rotationMetres = maximumRadius * Math.Abs(state.TargetRotationDegrees - state.RotationDegrees) * Math.PI / 180d;
        var scaleMetres = maximumRadius * Math.Abs(state.TargetScale - state.Scale);
        var verticalMetres = Math.Abs(state.TargetAltitude - state.Altitude) +
                             state.Members.Values.Select(member => Math.Abs(member.UpOffset)).DefaultIfEmpty(0d).Max() *
                             Math.Abs(state.TargetScale - state.Scale);
        var horizontalSeconds = (translationMetres + rotationMetres + scaleMetres) /
                                Math.Max(TransformHorizontalSpeedMetresPerSecond, 0.1d);
        var verticalSeconds = verticalMetres / Math.Max(TransformVerticalSpeedMetresPerSecond, 0.1d);
        return TimeSpan.FromSeconds(Math.Clamp(
            ManeuverTimeoutMarginSeconds + Math.Max(horizontalSeconds, verticalSeconds),
            ManeuverTimeoutMinimumSeconds,
            ManeuverTimeoutMaximumSeconds));
    }

    private async Task PushActionTargetsAsync(FormationState state, CancellationToken cancellationToken)
    {
        try
        {
            await PushTargetsAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = $"Formation stopped because a member backend rejected the maneuver: {ex.Message} The entire Team was released to Hold.";
            lock (_gate)
            {
                if (ReferenceEquals(_state, state))
                {
                    state.InterruptionCode = "FORMATION_BACKEND_REJECTED";
                    state.Findings =
                    [new("FORMATION_BACKEND_REJECTED", FormationLockSeverity.Blocking, reason)];
                }
            }
            await UnlockAsync(state.TeamId, reason, CancellationToken.None);
            throw;
        }
    }

    private void ScheduleLoopDispatch(FormationState state)
    {
        // The physics loop runs at 60 Hz while live backends publish at a
        // lower rate. Never enqueue one async task per physics tick: those
        // tasks can queue behind the serialized push gate and leave the
        // public formation snapshot behind the vehicle telemetry. One
        // latest-wins dispatcher is enough; it captures the newest pose after
        // it acquires the gate.
        if (Interlocked.Exchange(ref _loopDispatchPending, 1) == 0)
            _ = DispatchTargetsFromLoopAsync(state);
    }

    private async Task DispatchTargetsFromLoopAsync(FormationState state)
    {
        try
        {
            // The loop intentionally publishes a new pose revision every
            // tick.  Command revisions still invalidate an obsolete action,
            // but a pose revision must not invalidate the loop's own current
            // stream while it is waiting for the serialized push gate.  The
            // target batch is captured after acquiring that gate, so it is
            // already the latest available pose at dispatch time.
            await PushTargetsAsync(state, CancellationToken.None, allowPoseDrift: true);
        }
        catch (OperationCanceledException)
        {
            // Shutdown/unlock owns cancellation and performs the safe release.
        }
        catch (Exception ex)
        {
            var reason = $"Formation stopped because a member backend failed during target dispatch ({ex.GetType().Name}): {ex.Message} The entire Team was released to Hold.";
            lock (_gate)
            {
                if (!ReferenceEquals(_state, state)) return;
                state.InterruptionCode = "FORMATION_BACKEND_FAILURE";
                state.Findings =
                [new("FORMATION_BACKEND_FAILURE", FormationLockSeverity.Blocking, reason)];
            }
            await UnlockAsync(state.TeamId, reason, CancellationToken.None);
        }
        finally
        {
            Volatile.Write(ref _loopDispatchPending, 0);
        }
    }

    private async Task PushTargetsAsync(
        FormationState state,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? onlyMembers = null,
        bool begin = false,
        bool allowPoseDrift = false)
    {
        await _pushGate.WaitAsync(cancellationToken);
        try
        {
            var batch = CaptureTargetBatch(state, onlyMembers, begin);
            foreach (var pending in batch)
            {
                // An operator may replace the transform while a previous
                // backend call is in flight. No member—Ghost or live—may
                // receive a target from an obsolete action generation.
                lock (_gate)
                {
                    if (!ReferenceEquals(_state, state) ||
                        pending.Target.TargetRevision != state.TargetRevision ||
                        (!allowPoseDrift && pending.Target.PoseRevision != state.PoseRevision))
                        return;
                }

                if (pending.Unit.IsGhost)
                {
                    await _ghosts.SetFormationTargetAsync(pending.Member.UnitId, new(pending.Target.LockId, pending.Target.LatitudeDegrees,
                        pending.Target.LongitudeDegrees, pending.Target.AltitudeAglMetres, pending.Target.VelocityNorthMetresPerSecond,
                        pending.Target.VelocityEastMetresPerSecond, pending.Target.VelocityUpMetresPerSecond,
                        state.ManeuverName.Equals("formation entry", StringComparison.Ordinal)), cancellationToken);
                    lock (_gate)
                    {
                        if (ReferenceEquals(_state, state))
                        {
                            pending.Member.ControlState = FormationMemberControlState.Active;
                            pending.Member.Detail = "Ghost formation control active.";
                            pending.Member.LastSetpointAt = DateTimeOffset.UtcNow;
                        }
                    }
                    continue;
                }

                FormationExecutorResult result;
                var activate = pending.Begin;
                lock (_gate)
                {
                    if (!ReferenceEquals(_state, state) || pending.Member.IsActivating)
                        continue;
                    activate |= pending.Member.ControlState == FormationMemberControlState.Pending;
                    if (activate) pending.Member.IsActivating = true;
                }
                try
                {
                    result = activate
                        ? await pending.Member.Executor.BeginAsync(pending.Unit, pending.Target, cancellationToken)
                        : await pending.Member.Executor.UpdateAsync(pending.Unit, pending.Target, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"{pending.Member.Name} ({pending.Member.Executor.Backend}) failed while updating its formation target: {ex.Message}", ex);
                }
                finally
                {
                    if (activate)
                        lock (_gate) pending.Member.IsActivating = false;
                }
                lock (_gate)
                {
                    if (ReferenceEquals(_state, state))
                    {
                        pending.Member.ControlState = result.State;
                        pending.Member.Detail = result.Detail;
                        pending.Member.LastSetpointAt = result.LastSetpointAt;
                    }
                }
                if (!result.Accepted)
                    throw new InvalidOperationException(result.Detail ?? $"{pending.Member.Name} formation control was rejected.");
            }
        }
        finally
        {
            _pushGate.Release();
        }
    }

    private List<PendingFormationTarget> CaptureTargetBatch(FormationState state, IReadOnlySet<string>? onlyMembers, bool begin)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_state, state)) return [];
            var batch = new List<PendingFormationTarget>(state.Members.Count);
            // Capture one complete virtual Team pose. A published batch must
            // never combine a pre-command translation with a post-command
            // rotation or scale.
            var baseLatitude = state.Latitude;
            var baseLongitude = state.Longitude;
            var baseAltitude = state.Altitude;
            var northVelocity = state.VelocityNorth;
            var eastVelocity = state.VelocityEast;
            var upVelocity = state.VelocityUp;
            foreach (var member in state.Members.Values)
            {
                if (onlyMembers is not null && !onlyMembers.Contains(member.UnitId)) continue;
                if (!_units.TryGet(member.UnitId, out var unit) || unit is null)
                    throw new InvalidOperationException($"Formation member {member.Name} is no longer available.");
                var transform = TransformOffset(state, member);
                var transformVelocity = TransformVelocity(state, member, transform);
                var latitude = baseLatitude + transform.North / EarthRadiusMetres * 180d / Math.PI;
                var longitude = baseLongitude + transform.East / (EarthRadiusMetres * Math.Cos(baseLatitude * Math.PI / 180d)) * 180d / Math.PI;
                var (velocityNorth, velocityEast) = LimitHorizontalVelocity(state,
                    northVelocity + transformVelocity.North, eastVelocity + transformVelocity.East);
                var target = new FormationMemberTarget(state.LockId, member.UnitId, baseLatitude, baseLongitude, baseAltitude,
                    latitude, longitude, Math.Max(0, baseAltitude + transform.Up), velocityNorth, velocityEast,
                    Math.Clamp(upVelocity + transformVelocity.Up, -VerticalSpeedMetresPerSecond, VerticalSpeedMetresPerSecond), DateTimeOffset.UtcNow,
                    state.TargetRevision, state.PoseRevision);
                batch.Add(new(member, unit, target, begin));
            }
            return batch;
        }
    }

    private void RebaseToObservedPose(FormationState state)
    {
        var anchors = new List<(double Latitude, double Longitude, double Altitude)>();
        foreach (var member in state.Members.Values)
        {
            if (!_units.TryGet(member.UnitId, out var unit) || unit?.Telemetry is not { } telemetry ||
                telemetry.LatitudeDegrees is not { } latitude || telemetry.LongitudeDegrees is not { } longitude ||
                telemetry.AltitudeAglMetres is not { } altitude || !double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(altitude))
                continue;
            var offset = TransformOffset(state, member);
            var anchorLatitude = latitude - offset.North / EarthRadiusMetres * 180d / Math.PI;
            var anchorLongitude = longitude - offset.East / (EarthRadiusMetres * Math.Cos(anchorLatitude * Math.PI / 180d)) * 180d / Math.PI;
            anchors.Add((anchorLatitude, anchorLongitude, altitude - offset.Up));
        }
        if (anchors.Count == 0) return;
        state.Latitude = anchors.Average(anchor => anchor.Latitude);
        state.Longitude = anchors.Average(anchor => anchor.Longitude);
        state.Altitude = Math.Max(0d, anchors.Average(anchor => anchor.Altitude));
    }

    private static (double North, double East, double Up) TransformOffset(FormationState state, FormationMember member)
    {
        var radians = state.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        // North/East is heading-like: positive angles turn clockwise.
        return (state.Scale * (member.NorthOffset * cosine - member.EastOffset * sine),
                state.Scale * (member.NorthOffset * sine + member.EastOffset * cosine),
                state.Scale * member.UpOffset);
    }

    private static (double North, double East, double Up) TransformVelocity(FormationState state, FormationMember member, (double North, double East, double Up) offset)
    {
        var radians = state.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var unscaledNorth = member.NorthOffset * cosine - member.EastOffset * sine;
        var unscaledEast = member.NorthOffset * sine + member.EastOffset * cosine;
        return (state.ScaleVelocityPerSecond * unscaledNorth - state.RotationVelocityRadiansPerSecond * offset.East,
                state.ScaleVelocityPerSecond * unscaledEast + state.RotationVelocityRadiansPerSecond * offset.North,
                state.ScaleVelocityPerSecond * member.UpOffset);
    }

    private static (bool IsConverged, FormationMemberTarget Target, string Reason) IsMemberConverged(
        FormationState state,
        FormationMember member,
        UnitObservationSnapshot unit)
    {
        var transform = TransformOffset(state, member);
        var transformVelocity = TransformVelocity(state, member, transform);
        var latitude = state.Latitude + transform.North / EarthRadiusMetres * 180d / Math.PI;
        var longitude = state.Longitude + transform.East /
            (EarthRadiusMetres * Math.Cos(state.Latitude * Math.PI / 180d)) * 180d / Math.PI;
        var limitedVelocity = LimitHorizontalVelocity(state,
            state.VelocityNorth + transformVelocity.North,
            state.VelocityEast + transformVelocity.East);
        var target = new FormationMemberTarget(state.LockId, member.UnitId, state.Latitude, state.Longitude, state.Altitude,
            latitude, longitude, Math.Max(0d, state.Altitude + transform.Up),
            limitedVelocity.North,
            limitedVelocity.East,
            Math.Clamp(state.VelocityUp + transformVelocity.Up, -VerticalSpeedMetresPerSecond, VerticalSpeedMetresPerSecond),
            DateTimeOffset.UtcNow, state.TargetRevision);
        var expectedMode = member.Executor.Backend switch
        {
            "ArduPilot Guided" => "Guided",
            "PX4 Offboard" => "Offboard",
            _ => null
        };
        var evaluation = expectedMode is not null
            ? FormationTargetConvergence.Evaluate(unit, target, expectedMode)
            : new FormationTargetConvergence.Evaluation(member.Executor.IsTargetConverged(unit, target), "backend convergence check");
        return (evaluation.IsConverged, target, evaluation.Reason);
    }

    private static (double North, double East, double Up) InverseTransformOffset(FormationState state, double north, double east, double up)
    {
        var radians = state.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var scale = Math.Max(state.Scale, 0.0001d);
        // Inverse of the clockwise North/East rotation, used when a member
        // joins an already transformed lock without changing its target.
        return ((north * cosine + east * sine) / scale,
                (-north * sine + east * cosine) / scale,
                up / scale);
    }

    private static (double North, double East) LimitHorizontalVelocity(FormationState state, double north, double east)
    {
        var magnitude = Math.Sqrt(north * north + east * east);
        var limit = IsTransforming(state)
            ? CombinedTransformHorizontalSpeedMetresPerSecond
            : HorizontalSpeedMetresPerSecond;
        return magnitude <= limit || magnitude <= 0.0001d
            ? (north, east)
            : (north / magnitude * limit, east / magnitude * limit);
    }

    private static bool IsTransforming(FormationState state) =>
        Math.Abs(state.RotationVelocityRadiansPerSecond) > 0.000001d ||
        Math.Abs(state.ScaleVelocityPerSecond) > 0.000001d ||
        Math.Abs(state.TargetRotationDegrees - state.RotationDegrees) > RotationCompletionDeadbandDegrees ||
        Math.Abs(state.TargetScale - state.Scale) > ScaleCompletionDeadband;

    private async Task ReleaseMemberAsync(FormationState state, FormationMember member, CancellationToken cancellationToken)
    {
        if (_units.TryGet(member.UnitId, out var unit) && unit is not null && !unit.IsGhost)
        {
            var result = await member.Executor.HoldAndReleaseAsync(unit, state.LockId, cancellationToken);
            member.ControlState = result.State;
            member.Detail = result.Detail;
            member.LastSetpointAt = result.LastSetpointAt;
        }
        else
        {
            await _ghosts.ClearFormationTargetAsync(member.UnitId, state.LockId, hold: true, cancellationToken);
            member.ControlState = FormationMemberControlState.Holding;
            member.Detail = "Ghost formation control released to Hold.";
        }
    }

    private async void OnTeamsChanged(object? sender, EventArgs args)
    {
        try { await ReconcileMembershipAsync(CancellationToken.None); }
        catch { /* Membership changes must never crash a store notification. */ }
    }

    private async void OnUnitsChanged(object? sender, EventArgs args)
    {
        try
        {
            FormationState? state;
            lock (_gate) state = _state;
            if (state is null)
            {
                Publish();
                return;
            }

            // A Ghost can land or disarm through a path outside the normal
            // operator-command gateway. That telemetry transition is the same
            // safety event as an explicit per-member Land or Disarm.
            string? unsafeMember = null;
            foreach (var memberId in state.Members.Keys.ToArray())
            {
                if (!await ConfirmMemberSafetyLossAsync(state, memberId, CancellationToken.None))
                    continue;

                unsafeMember = memberId;
                break;
            }

            if (unsafeMember is not null)
            {
                var reason = $"Formation interrupted because {unsafeMember} lost airborne telemetry, control mode, or backend authority. The entire Team was released to Hold.";
                lock (_gate)
                {
                    if (ReferenceEquals(_state, state))
                    {
                        state.InterruptionCode = "FORMATION_MEMBER_SAFETY_LOSS";
                        state.Findings =
                        [new("FORMATION_MEMBER_SAFETY_LOSS", FormationLockSeverity.Blocking, reason)];
                    }
                }
                await UnlockAsync(state.TeamId, reason, CancellationToken.None);
                return;
            }

            // Ghost and MAVLink telemetry updates arrive independently of the
            // target controller. Convergence is a formation-status concern,
            // so publish a coalesced status update from the newest authoritative
            // observation instead of leaving the public snapshot at the first
            // target sample.
            bool publishConvergence;
            lock (_gate)
                publishConvergence = ReferenceEquals(_state, state) && state.RequiresConvergence;
            if (publishConvergence)
                Publish(force: false);
        }
        catch
        {
            // Store notifications must not surface into a GUI or headless host.
        }
    }

    private async Task<bool> ConfirmMemberSafetyLossAsync(FormationState state, string memberId, CancellationToken cancellationToken)
    {
        // Store notifications are asynchronous and may be delivered after a
        // newer telemetry sample has already replaced the snapshot that
        // caused them. Never drop a formation member from that stale event.
        if (!IsMemberUnsafe(state, memberId))
            return false;

        // Require the condition to persist across at least one normal
        // observation interval. This filters transient landed/armed/mode
        // transitions while still taking a real dropout out of the lock
        // promptly and safely.
        await Task.Delay(TimeSpan.FromMilliseconds(125), cancellationToken);
        return IsMemberUnsafe(state, memberId);
    }

    private bool IsMemberUnsafe(FormationState state, string memberId)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_state, state) || !state.Members.ContainsKey(memberId))
                return false;
        }

        if (!_units.TryGet(memberId, out var unit) || unit?.Telemetry is not { } telemetry)
            return true;

        if (!telemetry.Armed || telemetry.IsStale || string.Equals(telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase))
            return true;

        lock (_gate)
        {
            return state.Members.TryGetValue(memberId, out var member) &&
                   member.ControlState == FormationMemberControlState.Active &&
                   !member.Executor.IsControlActive(unit);
        }
    }

    private async Task ReconcileMembershipAsync(CancellationToken cancellationToken)
    {
        FormationState? state;
        lock (_gate) state = _state;
        if (state is null) return;
        if (!_teams.TryGet(state.TeamId, out var team) || team is null)
        {
            await UnlockAsync(state.TeamId, "Formation Team was removed.", cancellationToken);
            return;
        }
        var currentIds = team.Members.Select(member => member.UnitId).ToHashSet(StringComparer.Ordinal);
        // UnitTeamWorkflow also publishes when a unit's online/telemetry
        // projection changes. Those notifications do not change formation
        // membership and must not resend the intermediate Team position: doing
        // so would overwrite an active Go To or altitude target.
        if (currentIds.Count == state.Members.Count && currentIds.SetEquals(state.Members.Keys))
            return;
        foreach (var removed in state.Members.Keys.Where(id => !currentIds.Contains(id)).ToArray())
            await DropMemberAsync(removed, $"{removed} was removed from the Team.", cancellationToken);
        var addedMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var added in currentIds.Where(id => !state.Members.ContainsKey(id)))
        {
            if (!_units.TryGet(added, out var unit) || unit is null || unit.Telemetry is not { Armed: true, LatitudeDegrees: { } lat, LongitudeDegrees: { } lon, AltitudeAglMetres: { } altitude } ||
                !FormationAirborneReadiness.IsConfirmedAirborne(unit.Telemetry))
                continue;
            var executor = _executors.FirstOrDefault(candidate => candidate.CanHandle(unit));
            if (executor is null) continue;
            var findings = await executor.ValidateAsync([unit], cancellationToken);
            if (findings.Any(finding => finding.Severity == FormationLockSeverity.Blocking)) continue;
            lock (_gate)
            {
                if (_state != state) return;
                var baseline = InverseTransformOffset(state,
                    North(state.Latitude, lat),
                    East(state.Latitude, state.Longitude, lon),
                    altitude - state.Altitude);
                state.Members[added] = new FormationMember(added, unit.Name, executor, baseline.North, baseline.East, baseline.Up);
                addedMembers.Add(added);
            }
        }
        if (state.Members.Count < 2)
            await UnlockAsync(state.TeamId, "Formation requires at least two active members.", cancellationToken);
        else
        {
            // Existing members retain the targets owned by the formation loop.
            // Only initialize newly admitted members at the current Team
            // position; membership reconciliation must never overwrite an
            // active formation destination.
            if (addedMembers.Count > 0)
                await PushTargetsAsync(state, cancellationToken, onlyMembers: addedMembers, begin: true);
            Publish();
        }
    }

    private async Task DropMemberAsync(string unitId, string reason, CancellationToken cancellationToken)
    {
        FormationState? state;
        FormationMember? member;
        lock (_gate)
        {
            state = _state;
            member = state is not null && state.Members.Remove(unitId, out var removed) ? removed : null;
        }
        if (state is null || member is null) return;
        await ReleaseMemberAsync(state, member, cancellationToken);
        if (state.Members.Count < 2)
            await UnlockAsync(state.TeamId, reason, cancellationToken);
        else
        {
            Publish(reason);
        }
    }

    private FormationState RequireLocked(string teamId)
    {
        if (_state is null || !string.Equals(_state.TeamId, teamId, StringComparison.Ordinal))
            throw new InvalidOperationException("Lock this Team before issuing a formation movement command.");
        return _state;
    }

    private FormationState RequireTransformLocked(string teamId, FormationControlCapabilities capability)
    {
        var state = RequireLocked(teamId);
        var unsupported = state.Members.Values
            .Where(member => (member.Executor.Capabilities & capability) != capability)
            .Select(member => $"{member.Name} ({member.Executor.Backend})")
            .ToArray();
        if (unsupported.Length > 0)
            throw new InvalidOperationException($"Formation {(capability == FormationControlCapabilities.Rotation ? "rotation" : "resize")} is not supported by: {string.Join(", ", unsupported)}.");
        return state;
    }

    private List<WorkflowFinding> ValidateMove(string teamId, double latitudeDegrees, double longitudeDegrees)
    {
        var findings = new List<WorkflowFinding>();
        if (!double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90 || !double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180)
            findings.Add(new("FORMATION_TARGET_INVALID", WorkflowFindingSeverity.Blocking, "Enter valid WGS84 latitude and longitude."));
        lock (_gate)
            if (_state is null || !string.Equals(_state.TeamId, teamId, StringComparison.Ordinal))
                findings.Add(new("FORMATION_NOT_LOCKED", WorkflowFindingSeverity.Blocking, "Lock this Team before moving its formation."));
        return findings;
    }

    private List<WorkflowFinding> ValidateAltitude(string teamId, double altitudeAglMetres)
    {
        var findings = new List<WorkflowFinding>();
        if (!double.IsFinite(altitudeAglMetres) || altitudeAglMetres < 0)
            findings.Add(new("FORMATION_ALTITUDE_INVALID", WorkflowFindingSeverity.Blocking, "Enter a non-negative finite formation altitude."));
        lock (_gate)
            if (_state is null || !string.Equals(_state.TeamId, teamId, StringComparison.Ordinal))
                findings.Add(new("FORMATION_NOT_LOCKED", WorkflowFindingSeverity.Blocking, "Lock this Team before changing its altitude."));
        return findings;
    }

    private IReadOnlyList<WorkflowFinding> ValidateHold(string teamId)
    {
        lock (_gate)
            return _state is not null && string.Equals(_state.TeamId, teamId, StringComparison.Ordinal)
                ? []
                : [new("FORMATION_NOT_LOCKED", WorkflowFindingSeverity.Blocking, "Lock this Team before holding its formation.")];
    }

    private List<WorkflowFinding> ValidateTransform(string teamId, double value, bool isScale)
    {
        var findings = new List<WorkflowFinding>();
        if (!double.IsFinite(value) || (isScale ? value is < 50d or > 200d : value is < -360d or > 360d))
            findings.Add(new(isScale ? "FORMATION_SCALE_INVALID" : "FORMATION_ROTATION_INVALID", WorkflowFindingSeverity.Blocking,
                isScale ? "Enter a formation size between 50% and 200%." : "Enter a formation rotation between -360° and 360°."));
        lock (_gate)
        {
            if (_state is null || !string.Equals(_state.TeamId, teamId, StringComparison.Ordinal))
                findings.Add(new("FORMATION_NOT_LOCKED", WorkflowFindingSeverity.Blocking, "Lock this Team before changing its formation shape."));
            else
            {
                var capability = isScale ? FormationControlCapabilities.Scale : FormationControlCapabilities.Rotation;
                var unsupported = _state.Members.Values
                    .Where(member => (member.Executor.Capabilities & capability) != capability)
                    .Select(member => $"{member.Name} ({member.Executor.Backend})")
                    .ToArray();
                if (unsupported.Length > 0)
                    findings.Add(new("FORMATION_TRANSFORM_BACKEND_UNSUPPORTED", WorkflowFindingSeverity.Blocking,
                        $"Formation {(isScale ? "resize" : "rotation")} is not supported by: {string.Join(", ", unsupported)}."));
            }
        }
        return findings;
    }

    private UnitObservationSnapshot RequireUnit(string unitId)
        => _units.TryGet(unitId, out var unit) && unit is not null ? unit : throw new KeyNotFoundException($"Unit '{unitId}' was not found.");

    private void Publish(string? interruption = null, bool force = true)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!force && now - _lastPeriodicPublish < TimeSpan.FromMilliseconds(50)) return;
            _lastPeriodicPublish = now;
            if (_state is { } state)
            {
                var memberSnapshots = state.Members.Values.Select(member =>
                {
                    var unitAvailable = _units.TryGet(member.UnitId, out var unit) && unit is not null;
                    var convergence = unitAvailable
                        ? IsMemberConverged(state, member, unit!)
                        : (false, new FormationMemberTarget(state.LockId, member.UnitId, state.Latitude, state.Longitude,
                            state.Altitude, state.Latitude, state.Longitude, state.Altitude, 0, 0, 0, DateTimeOffset.UtcNow, state.TargetRevision), "unit unavailable");
                    var converged = unitAvailable && convergence.Item1;
                    var transform = TransformOffset(state, member);
                    var detail = !converged && state.RequiresConvergence && member.ControlState == FormationMemberControlState.Active
                        ? $"{member.Detail ?? member.Executor.Backend}; target not converged: {convergence.Item3}."
                        : member.Detail;
                    return new FormationLockMemberSnapshot(member.UnitId, member.Name, member.NorthOffset, member.EastOffset, member.UpOffset,
                        member.ControlState is FormationMemberControlState.Active or FormationMemberControlState.Pending, detail,
                        member.Executor.Backend, member.ControlState, member.LastSetpointAt, transform.North, transform.East, transform.Up,
                        converged);
                }).ToArray();
                var allConverged = memberSnapshots.Length > 0 && memberSnapshots.All(member => member.IsAtTarget);
                if (state.RequiresConvergence)
                {
                    if (!allConverged)
                        state.TargetStableSince = null;
                    else
                    {
                        state.TargetStableSince ??= now;
                        allConverged = now - state.TargetStableSince >= TimeSpan.FromMilliseconds(500);
                        if (allConverged)
                        {
                            state.RequiresConvergence = false;
                            state.ManeuverStartedAt = null;
                            state.ManeuverDeadline = null;
                        }
                    }
                }
                var moving = IsMoving(state) || !allConverged;
                _snapshot = new(state.TeamId, state.TeamName, moving ? FormationLockState.Moving : FormationLockState.Locked,
                    state.Latitude, state.Longitude, state.Altitude, state.TargetLatitude, state.TargetLongitude, state.TargetAltitude,
                    memberSnapshots, state.Findings, interruption, DateTimeOffset.UtcNow,
                    state.RotationDegrees, state.TargetRotationDegrees, state.Scale * 100d, state.TargetScale * 100d, allConverged,
                    state.InterruptionCode);
            }
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Subscribers include GUI adapters. A presentation failure
                // must not stop the formation control loop or leave vehicles
                // with a stale formation target.
                _logger?.LogError(ex, "Formation state subscriber failed.");
            }
        }
    }

    private static double North(double originLatitude, double latitude) => (latitude - originLatitude) * Math.PI / 180d * EarthRadiusMetres;
    private static double East(double latitude, double originLongitude, double longitude) => (longitude - originLongitude) * Math.PI / 180d * EarthRadiusMetres * Math.Cos(latitude * Math.PI / 180d);
    private static double Distance(double aLat, double aLon, double bLat, double bLon) { var n = North(aLat, bLat); var e = East(aLat, aLon, bLon); return Math.Sqrt(n * n + e * e); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _teams.Changed -= OnTeamsChanged;
        _units.Changed -= OnUnitsChanged;
        await StopAsync(CancellationToken.None);
        _shutdown.Dispose();
        _pushGate.Dispose();
    }

    private sealed class FormationState(string lockId, string teamId, string teamName, double latitude, double longitude, double altitude)
    {
        public string LockId { get; } = lockId;
        public string TeamId { get; } = teamId;
        public string TeamName { get; } = teamName;
        public double Latitude { get; set; } = latitude;
        public double Longitude { get; set; } = longitude;
        public double Altitude { get; set; } = altitude;
        public double TargetLatitude { get; set; } = latitude;
        public double TargetLongitude { get; set; } = longitude;
        public double TargetAltitude { get; set; } = altitude;
        public double VelocityNorth { get; set; }
        public double VelocityEast { get; set; }
        public double VelocityUp { get; set; }
        public double RotationDegrees { get; set; }
        public double TargetRotationDegrees { get; set; }
        public double RotationVelocityRadiansPerSecond { get; set; }
        public double Scale { get; set; } = 1d;
        public double TargetScale { get; set; } = 1d;
        public double ScaleVelocityPerSecond { get; set; }
        public long TargetRevision { get; set; } = 1;
        public long PoseRevision { get; set; }
        public bool RequiresConvergence { get; set; }
        public DateTimeOffset? TargetStableSince { get; set; }
        public string ManeuverName { get; set; } = "movement";
        public DateTimeOffset? ManeuverStartedAt { get; set; }
        public DateTimeOffset? ManeuverDeadline { get; set; }
        public string? InterruptionCode { get; set; }
        public IReadOnlyList<FormationLockFinding> Findings { get; set; } = [];
        public Dictionary<string, FormationMember> Members { get; } = new(StringComparer.Ordinal);
    }

    private sealed record PendingFormationTarget(
        FormationMember Member,
        UnitObservationSnapshot Unit,
        FormationMemberTarget Target,
        bool Begin);

    private sealed class FormationMember(string unitId, string name, IFormationLockExecutor executor, double northOffset, double eastOffset, double upOffset)
    {
        public string UnitId { get; } = unitId;
        public string Name { get; } = name;
        public IFormationLockExecutor Executor { get; } = executor;
        public double NorthOffset { get; set; } = northOffset;
        public double EastOffset { get; set; } = eastOffset;
        public double UpOffset { get; set; } = upOffset;
        public FormationMemberControlState ControlState { get; set; } = FormationMemberControlState.Pending;
        public string? Detail { get; set; }
        public DateTimeOffset? LastSetpointAt { get; set; }
        public bool IsActivating { get; set; }
    }
}
