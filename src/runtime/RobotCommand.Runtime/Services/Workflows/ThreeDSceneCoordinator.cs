using RobotCommand.Core;
using RobotCommand.Rendering;

namespace RobotCommand.Services.Workflows;

/// <summary>
/// Builds a renderer-neutral local scene from the existing observation and
/// formation workflows. It owns no graphics resources and is lazy by design.
/// </summary>
public sealed class ThreeDSceneCoordinator : IThreeDSceneWorkflow, IDisposable
{
    private readonly object _gate = new();
    private readonly IUnitObservationWorkflow _units;
    private readonly ITeamWorkflow _teams;
    private readonly ITeamSelectionWorkflow _teamSelection;
    private readonly ISelectionWorkflow _selection;
    private readonly IFormationLockWorkflow _formation;
    private ThreeDRenderBackendPolicy _policy = ThreeDRenderBackendPolicy.Auto;
    private ThreeDCameraSnapshot _camera = new(new(0, 60, -120), 0, -18, 0, 60, 0.1, 5000, 2);
    private ThreeDOriginSnapshot? _origin;
    private long _revision;
    private ThreeDSceneSnapshot _current;

    public ThreeDSceneCoordinator(
        IUnitObservationWorkflow units,
        ITeamWorkflow teams,
        ITeamSelectionWorkflow teamSelection,
        ISelectionWorkflow selection,
        IFormationLockWorkflow formation)
    {
        _units = units;
        _teams = teams;
        _teamSelection = teamSelection;
        _selection = selection;
        _formation = formation;
        _current = BuildSceneLocked();
        _units.Changed += OnSourceChanged;
        _teams.Changed += OnSourceChanged;
        _teamSelection.Changed += OnSourceChanged;
        _selection.Changed += OnSourceChanged;
        _formation.Changed += OnSourceChanged;
    }

    public event EventHandler? Changed;

    public ThreeDSceneSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public ThreeDRenderBackendPolicy BackendPolicy
    {
        get { lock (_gate) return _policy; }
    }

    public Task SetBackendPolicyAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _policy = policy;
            PublishLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetCameraAsync(ThreeDCameraSnapshot camera, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCamera(camera);
        lock (_gate)
        {
            _camera = camera;
            PublishLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ResetCameraAsync(CancellationToken cancellationToken = default)
        => SetCameraAsync(new(new(0, 60, -120), 0, -18, 0, 60, 0.1, 5000, 2), cancellationToken);

    public Task FitSceneAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scene = Current;
        var points = scene.Primitives.Select(item => item.Transform.Position).ToArray();
        if (points.Length == 0) return ResetCameraAsync(cancellationToken);
        var center = new ThreeDVector3(points.Average(item => item.X), points.Average(item => item.Y), points.Average(item => item.Z));
        var radius = points.Select(item => ThreeDSceneMath.Distance(item, center)).DefaultIfEmpty(20).Max();
        return SetCameraAsync(scene.Camera with
        {
            Position = new(center.X, center.Y + Math.Max(30, radius * 1.5), center.Z - Math.Max(60, radius * 2.2)),
            YawDegrees = 0,
            PitchDegrees = -18,
            MovementSpeed = Math.Clamp(radius / 20, 0.5, 100)
        }, cancellationToken);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        lock (_gate) PublishLocked();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _units.Changed -= OnSourceChanged;
        _teams.Changed -= OnSourceChanged;
        _teamSelection.Changed -= OnSourceChanged;
        _selection.Changed -= OnSourceChanged;
        _formation.Changed -= OnSourceChanged;
    }

    private void PublishLocked() => _current = BuildSceneLocked();

    private ThreeDSceneSnapshot BuildSceneLocked()
    {
        var units = _units.Units
            .Where(unit => unit.Telemetry?.LatitudeDegrees is not null && unit.Telemetry.LongitudeDegrees is not null && unit.Telemetry.AltitudeAglMetres is not null)
            .ToArray();
        if (_origin is null && units.Length > 0)
        {
            _origin = new ThreeDOriginSnapshot(
                units.Average(unit => unit.Telemetry!.LatitudeDegrees!.Value),
                units.Average(unit => unit.Telemetry!.LongitudeDegrees!.Value),
                units.Average(unit => unit.Telemetry!.AltitudeAglMetres!.Value),
                DateTimeOffset.UtcNow);
        }

        var origin = _origin ?? new ThreeDOriginSnapshot(43.6532, -79.3832, 0, DateTimeOffset.UtcNow);

        var selected = _selection.Current.UnitIds.ToHashSet(StringComparer.Ordinal);
        var teamSelected = _teamSelection.Current.MemberUnitIds.ToHashSet(StringComparer.Ordinal);
        var primitives = new List<ThreeDPrimitiveSnapshot>();
        foreach (var unit in units)
        {
            var position = ThreeDSceneMath.ToLocal(unit.Telemetry!.LatitudeDegrees!.Value, unit.Telemetry.LongitudeDegrees!.Value, unit.Telemetry.AltitudeAglMetres!.Value, origin);
            primitives.Add(new($"unit:{unit.Id}", ThreeDPrimitiveKind.Arrow, new(position, new(0, unit.Telemetry.HeadingDegrees ?? 0, 0), new(1, 1, 1)), unit.IsGhost ? "#55B7E8" : "#FFD166", unit.Name, selected.Contains(unit.Id), teamSelected.Contains(unit.Id)));
        }

        var lines = new List<ThreeDLineSnapshot>();
        foreach (var team in _teams.Current.Teams)
        {
            var members = team.Members.Select(member => primitives.FirstOrDefault(item => item.Id == $"unit:{member.UnitId}")).Where(item => item is not null).Cast<ThreeDPrimitiveSnapshot>().ToArray();
            if (members.Length > 1)
            {
                var points = members.Select(item => item.Transform.Position).ToArray();
                lines.Add(new($"team:{team.Id}", [points[0], points[^1]], "#F6B73C", 2, false, false, teamSelected.Count > 0 && teamSelected.IsSupersetOf(team.Members.Select(item => item.UnitId))));
            }
        }

        var formation = _formation.Current;
        if (formation.TeamLatitudeDegrees is { } teamLatitude && formation.TeamLongitudeDegrees is { } teamLongitude && formation.TeamAltitudeAglMetres is { } teamAltitude)
        {
            var teamPosition = ThreeDSceneMath.ToLocal(teamLatitude, teamLongitude, teamAltitude, origin);
            primitives.Add(new("formation:team", ThreeDPrimitiveKind.Marker, new(teamPosition, ThreeDVector3.Zero, new(1, 1, 1)), "#F6B73C", formation.TeamName ?? "Team", false, true));
            foreach (var member in formation.Members.Where(item => item.TargetNorthOffsetMetres.HasValue && item.TargetEastOffsetMetres.HasValue && item.TargetUpOffsetMetres.HasValue))
            {
                var target = new ThreeDVector3(
                    teamPosition.X + member.TargetEastOffsetMetres!.Value,
                    teamPosition.Y + member.TargetUpOffsetMetres!.Value,
                    teamPosition.Z + member.TargetNorthOffsetMetres!.Value);
                lines.Add(new($"formation-target:{member.UnitId}", [teamPosition, target], "#F6B73C", 1, false, false, true));
            }
        }

        var status = new ThreeDRendererStatus("Software", false, true, false, "CPU primitive renderer", 0, 0);
        var hud = new ThreeDHudSnapshot("Software", "Ready", $"Camera {Format(_camera.Position)}", 0, 0);
        return new(++_revision, DateTimeOffset.UtcNow, origin, _camera, new(), new(), primitives, lines, hud, status);
    }

    private static string Format(ThreeDVector3 value) => $"{value.X:0.#}, {value.Y:0.#}, {value.Z:0.#}";

    private static void ValidateCamera(ThreeDCameraSnapshot camera)
    {
        if (!double.IsFinite(camera.Position.X) || !double.IsFinite(camera.Position.Y) || !double.IsFinite(camera.Position.Z) ||
            !double.IsFinite(camera.YawDegrees) || !double.IsFinite(camera.PitchDegrees) || !double.IsFinite(camera.FieldOfViewDegrees) ||
            camera.FieldOfViewDegrees is <= 1 or >= 179 || camera.NearClip <= 0 || camera.FarClip <= camera.NearClip || camera.MovementSpeed <= 0)
            throw new ArgumentException("The 3D camera contains an invalid position or range.", nameof(camera));
    }
}
