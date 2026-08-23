using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Rendering;
using RobotCommand.Services.Location;

namespace RobotCommand.Services.Workflows;

public sealed class ThreeDWorldSceneCoordinator : IThreeDWorldSceneWorkflow, IDisposable
{
    private readonly object _gate = new();
    private readonly IUnitObservationWorkflow _units;
    private readonly ITeamWorkflow _teams;
    private readonly ITeamSelectionWorkflow _teamSelection;
    private readonly ISelectionWorkflow _selection;
    private readonly IFormationLockWorkflow _formation;
    private readonly IOperatorLocationService _operatorLocation;
    private ThreeDRenderBackendPolicy _policy = ThreeDRenderBackendPolicy.Auto;
    private ThreeDOriginSnapshot _origin = new(43.6532, -79.3832, 0, DateTimeOffset.UtcNow);
    private ThreeDCameraSnapshot _camera = ThreeDSceneMath.CreateWorldCamera(ThreeDVector3.Zero, 3);
    private double _resolution = 3;
    private long _revision;
    private ThreeDSceneSnapshot _current;

    public ThreeDWorldSceneCoordinator(
        IUnitObservationWorkflow units,
        ITeamWorkflow teams,
        ITeamSelectionWorkflow teamSelection,
        ISelectionWorkflow selection,
        IFormationLockWorkflow formation,
        IOperatorLocationService operatorLocation)
    {
        _units = units;
        _teams = teams;
        _teamSelection = teamSelection;
        _selection = selection;
        _formation = formation;
        _operatorLocation = operatorLocation;
        _current = BuildSceneLocked();
        _units.Changed += OnSourceChanged;
        _teams.Changed += OnSourceChanged;
        _teamSelection.Changed += OnSourceChanged;
        _selection.Changed += OnSourceChanged;
        _formation.Changed += OnSourceChanged;
        _operatorLocation.Changed += OnSourceChanged;
    }

    public event EventHandler? Changed;

    public ThreeDSceneSnapshot Current { get { lock (_gate) return _current; } }
    public ThreeDRenderBackendPolicy BackendPolicy { get { lock (_gate) return _policy; } }

    public Task ActivateAsync(ThreeDWorldEntryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(request.LatitudeDegrees) || request.LatitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(request.LongitudeDegrees) || request.LongitudeDegrees is < -180 or > 180 ||
            !double.IsFinite(request.ResolutionMetresPerPixel) || request.ResolutionMetresPerPixel <= 0 ||
            !double.IsFinite(request.RotationDegrees))
            throw new ArgumentException("The 3D world entry location is invalid.", nameof(request));

        lock (_gate)
        {
            _resolution = request.ResolutionMetresPerPixel;
            _origin = new(request.LatitudeDegrees, request.LongitudeDegrees, 0, DateTimeOffset.UtcNow);
            _camera = ThreeDSceneMath.CreateWorldCamera(ThreeDVector3.Zero, _resolution, request.RotationDegrees);
            PublishLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetBackendPolicyAsync(ThreeDRenderBackendPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _policy = policy; PublishLocked(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetCameraAsync(ThreeDCameraSnapshot camera, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCamera(camera);
        lock (_gate) { _camera = camera; PublishLocked(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ResetCameraAsync(CancellationToken cancellationToken = default)
        => SetCameraAsync(ThreeDSceneMath.CreateWorldCamera(ThreeDVector3.Zero, _resolution, _camera.YawDegrees), cancellationToken);

    public Task FitSceneAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scene = Current;
        var points = scene.Primitives
            .Where(item => item.Kind != ThreeDPrimitiveKind.GroundPlane)
            .Select(item => item.Transform.Position)
            .ToArray();
        if (points.Length == 0) return ResetCameraAsync(cancellationToken);

        var center = new ThreeDVector3(points.Average(item => item.X), points.Average(item => item.Y), points.Average(item => item.Z));
        var radius = points.Select(item => ThreeDSceneMath.Distance(item, center)).DefaultIfEmpty(20).Max();
        var distance = Math.Clamp(Math.Max(80, radius * 2.5), 80, 5000);
        var target = scene.Camera.OrbitTarget ?? ThreeDVector3.Zero;
        var camera = scene.Camera.OrbitMode
            ? scene.Camera with
            {
                OrbitTarget = center,
                OrbitDistance = distance,
                Position = ThreeDProjection.OrbitPosition(center, scene.Camera.YawDegrees, scene.Camera.PitchDegrees, distance),
                MovementSpeed = Math.Clamp(radius / 20, 1, 100)
            }
            : scene.Camera with
            {
                Position = new(center.X, center.Y + Math.Max(30, radius * 1.4), center.Z - distance),
                YawDegrees = 0,
                PitchDegrees = 18,
                MovementSpeed = Math.Clamp(radius / 20, 1, 100)
            };
        return SetCameraAsync(camera, cancellationToken);
    }

    public void Dispose()
    {
        _units.Changed -= OnSourceChanged;
        _teams.Changed -= OnSourceChanged;
        _teamSelection.Changed -= OnSourceChanged;
        _selection.Changed -= OnSourceChanged;
        _formation.Changed -= OnSourceChanged;
        _operatorLocation.Changed -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        lock (_gate) PublishLocked();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PublishLocked() => _current = BuildSceneLocked();

    private ThreeDSceneSnapshot BuildSceneLocked()
    {
        var selected = _selection.Current.UnitIds.ToHashSet(StringComparer.Ordinal);
        var teamSelected = _teamSelection.Current.MemberUnitIds.ToHashSet(StringComparer.Ordinal);
        var units = _units.Units
            .Where(unit => unit.Telemetry?.LatitudeDegrees is not null && unit.Telemetry.LongitudeDegrees is not null)
            .ToArray();

        var planeSize = Math.Clamp(Math.Max(500, _resolution * 1400), 500, 10000);
        var primitives = new List<ThreeDPrimitiveSnapshot>
        {
            new("world:ground", ThreeDPrimitiveKind.GroundPlane,
                new(ThreeDVector3.Zero, ThreeDVector3.Zero, new(planeSize, 1, planeSize)), "#173126")
        };
        var unitPositions = new Dictionary<string, ThreeDVector3>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var telemetry = unit.Telemetry!;
            var altitude = telemetry.AltitudeMslMetres ?? telemetry.AltitudeAglMetres ?? 0;
            var position = ThreeDSceneMath.ToLocal(telemetry.LatitudeDegrees!.Value, telemetry.LongitudeDegrees!.Value, altitude, _origin);
            unitPositions[unit.Id] = position;
            primitives.Add(new($"world:unit:{unit.Id}", ThreeDPrimitiveKind.Sphere,
                new(position, ThreeDVector3.Zero, new(2.5, 2.5, 2.5)),
                unit.IsGhost ? "#55B7E8" : "#FFD166", unit.Name,
                selected.Contains(unit.Id), teamSelected.Contains(unit.Id)));
        }

        var operatorSnapshot = _operatorLocation.Snapshot;
        if (operatorSnapshot.IsAvailable && operatorSnapshot.LatitudeDegrees is { } operatorLatitude && operatorSnapshot.LongitudeDegrees is { } operatorLongitude)
        {
            var operatorPosition = ThreeDSceneMath.ToLocal(operatorLatitude, operatorLongitude, 0, _origin);
            primitives.Add(new("world:operator", ThreeDPrimitiveKind.Sphere,
                new(operatorPosition, ThreeDVector3.Zero, new(3.5, 3.5, 3.5)), "#D875FF", "Operator"));
        }

        var lines = new List<ThreeDLineSnapshot>();
        foreach (var team in _teams.Current.Teams)
        {
            var points = team.Members
                .Select(member => unitPositions.TryGetValue(member.UnitId, out var point) ? point : (ThreeDVector3?)null)
                .Where(point => point is not null).Select(point => point!).ToArray();
            if (points.Length > 1)
                lines.Add(new($"world:team:{team.Id}", [points[0], points[^1]], "#F6B73C", 1, false, false, true));
        }

        var formation = _formation.Current;
        if (formation.TeamLatitudeDegrees is { } teamLatitude && formation.TeamLongitudeDegrees is { } teamLongitude && formation.TeamAltitudeAglMetres is { } teamAltitude)
        {
            var teamPosition = ThreeDSceneMath.ToLocal(teamLatitude, teamLongitude, teamAltitude, _origin);
            primitives.Add(new("world:formation-team", ThreeDPrimitiveKind.Marker,
                new(teamPosition, ThreeDVector3.Zero, new(1, 1, 1)), "#F6B73C", formation.TeamName ?? "Team", false, true));
        }

        var status = new ThreeDRendererStatus("Software", false, true, false, "CPU primitive renderer", 0, 0);
        var hud = new ThreeDHudSnapshot("3D", "Ready", $"Camera {_camera.Position.X:0.#}, {_camera.Position.Y:0.#}, {_camera.Position.Z:0.#}", 0, 0);
        return new(++_revision, DateTimeOffset.UtcNow, _origin, _camera,
            new(true, planeSize, Math.Max(10, Math.Round(planeSize / 40)), "#26313B"),
            new(false, Math.Max(25, planeSize / 5)), primitives, lines, hud, status);
    }

    private static void ValidateCamera(ThreeDCameraSnapshot camera)
    {
        if (!double.IsFinite(camera.Position.X) || !double.IsFinite(camera.Position.Y) || !double.IsFinite(camera.Position.Z) ||
            !double.IsFinite(camera.YawDegrees) || !double.IsFinite(camera.PitchDegrees) || !double.IsFinite(camera.FieldOfViewDegrees) ||
            camera.FieldOfViewDegrees is <= 1 or >= 179 || camera.NearClip <= 0 || camera.FarClip <= camera.NearClip || camera.MovementSpeed <= 0)
            throw new ArgumentException("The 3D camera contains an invalid position or range.", nameof(camera));
    }
}
