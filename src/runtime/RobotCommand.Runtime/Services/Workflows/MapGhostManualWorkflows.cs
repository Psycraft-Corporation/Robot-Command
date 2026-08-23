using System.Collections.Specialized;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Location;
using RobotCommand.Services.ManualControl;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Simulation;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

/// <summary>Front-end-neutral projection and mutation boundary for unit selection.</summary>
public sealed class SelectionWorkflow : ISelectionWorkflow
{
    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;

    public SelectionWorkflow(ISelectionService selection, IEntityStore<string, VehicleRecord> vehicles)
    {
        _selection = selection;
        _vehicles = vehicles;
        _selection.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public SelectionWorkflowSnapshot Current => new(
        _selection.Current.Kind == SelectionKind.Vehicle ? _selection.Current.Id : _selection.SelectedUnitIds.FirstOrDefault(),
        _selection.SelectedUnitIds.ToArray(),
        _selection.UnitSelectionAnchorId,
        _selection.Current.Kind.ToString(),
        _selection.Current.Id);

    public Task SetUnitsAsync(IReadOnlyList<string> unitIds, string? anchorUnitId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(unitIds);
        var selections = unitIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => _vehicles.TryGet(id, out var vehicle) ? vehicle : null)
            .Where(vehicle => vehicle is not null)
            .Cast<VehicleRecord>()
            .Select(SelectionFactory.From)
            .ToArray();
        _selection.SetUnitSelection(selections, anchorUnitId);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _selection.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Owns non-rendering camera, overlay, saved-view, and follow state.</summary>
public sealed class MapViewWorkflow : IMapViewWorkflow
{
    private readonly object _gate = new();
    private readonly IMapViewportState _viewport;
    private readonly IOperationalMapEngine _engine;
    private readonly ISavedMapViewRepository _savedViews;
    private readonly ISelectionWorkflow _selection;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IOperatorLocationService _operatorLocation;
    private MapOverlayWorkflowSnapshot _overlays = new(true, true, true, true, true);
    private string[] _followUnitIds = [];
    private long _followRevision;
    private readonly CoalescedChangeNotifier _changeNotifier;

    public MapViewWorkflow(
        IMapViewportState viewport,
        IOperationalMapEngine engine,
        ISavedMapViewRepository savedViews,
        ISelectionWorkflow selection,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IOperatorLocationService operatorLocation,
        IUiDispatcher? dispatcher = null)
    {
        _viewport = viewport;
        _engine = engine;
        _savedViews = savedViews;
        _selection = selection;
        _telemetry = telemetry;
        _operatorLocation = operatorLocation;
        _changeNotifier = new(TimeSpan.FromMilliseconds(5), dispatcher);
        _changeNotifier.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _viewport.Changed += (_, _) => _changeNotifier.Request();
        _engine.Changed += (_, _) => _changeNotifier.Request();
        _savedViews.Changed += (_, _) => _changeNotifier.Request();
        _selection.Changed += (_, _) => _changeNotifier.Request();
        ((INotifyCollectionChanged)_telemetry.Items).CollectionChanged += OnTelemetryChanged;
        _operatorLocation.Changed += (_, _) => _changeNotifier.Request();
    }

    public event EventHandler? Changed;

    public MapViewWorkflowSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return new(
                    ToSnapshot(_viewport.Current),
                    _engine.AvailableStyles.Select(style => new MapStyleWorkflowSnapshot(style.StyleId, style.DisplayName, style.Kind.ToString(), string.Equals(style.StyleId, _engine.SelectedStyleId, StringComparison.OrdinalIgnoreCase))).ToArray(),
                    _engine.SelectedStyleId,
                    _overlays,
                    _followUnitIds.Length > 0,
                    _followUnitIds.ToArray(),
                    _followUnitIds.Length switch { 0 => "", 1 => "unit", _ => $"{_followUnitIds.Length} units" },
                    "Ready");
            }
        }
    }

    public IReadOnlyList<SavedMapViewWorkflowSnapshot> SavedViews => _savedViews.Views.Select(ToSnapshot).ToArray();

    public Task SetViewportAsync(MapViewportWorkflowSnapshot viewport, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(viewport);
        _viewport.Update(ToModel(viewport));
        PublishChanged();
        return Task.CompletedTask;
    }

    public async Task SelectStyleAsync(string styleId, CancellationToken cancellationToken = default)
    {
        await _engine.SelectStyleAsync(styleId, cancellationToken);
        PublishChanged();
    }

    public Task SetOverlayVisibleAsync(string overlay, bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _overlays = overlay.Trim().ToLowerInvariant() switch
            {
                "geometries" => _overlays with { GeometriesVisible = visible },
                "policy" => _overlays with { PolicyVisible = visible },
                "trails" => _overlays with { TrailsVisible = visible },
                "destinations" or "goto" => _overlays with { DestinationsVisible = visible },
                "labels" => _overlays with { LabelsVisible = visible },
                _ => throw new ArgumentException("Overlay must be geometries, policy, trails, destinations, or labels.", nameof(overlay))
            };
        }
        PublishChanged();
        return Task.CompletedTask;
    }

    public Task JumpToSelectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var positions = SelectedPositions();
        if (positions.Count == 0) throw new InvalidOperationException("Every selected unit requires fresh global WGS84 telemetry.");
        StopFollowingCore();
        var center = Mean(positions);
        var current = _viewport.Current;
        _viewport.Update(new MapViewportSnapshot(center.Longitude, center.Latitude, ResolutionFor(positions, current?.Resolution), current?.RotationDegrees ?? 0));
        PublishChanged();
        return Task.CompletedTask;
    }

    public Task JumpToOperatorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var location = _operatorLocation.Snapshot;
        if (!location.IsAvailable || location.LongitudeDegrees is not { } longitude || location.LatitudeDegrees is not { } latitude)
            throw new InvalidOperationException("Operator location is unavailable.");
        StopFollowingCore();
        var current = _viewport.Current;
        _viewport.Update(new MapViewportSnapshot(longitude, latitude, MapNavigationMath.SingleTargetResolution, current?.RotationDegrees ?? 0));
        PublishChanged();
        return Task.CompletedTask;
    }

    public Task FollowSelectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = _selection.Current.UnitIds.Distinct(StringComparer.Ordinal).ToArray();
        var positions = Positions(ids);
        if (ids.Length == 0 || positions.Count != ids.Length)
            throw new InvalidOperationException("Every selected unit requires fresh global WGS84 telemetry.");
        lock (_gate) { _followUnitIds = ids; _followRevision++; }
        var center = Mean(positions);
        var current = _viewport.Current;
        _viewport.Update(new MapViewportSnapshot(center.Longitude, center.Latitude, ResolutionFor(positions, current?.Resolution), current?.RotationDegrees ?? 0));
        PublishChanged();
        return Task.CompletedTask;
    }

    public Task StopFollowingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopFollowingCore();
        PublishChanged();
        return Task.CompletedTask;
    }

    public Task NotifyUserPannedAsync(CancellationToken cancellationToken = default) => StopFollowingAsync(cancellationToken);

    public async Task<SavedMapViewWorkflowSnapshot> SaveViewAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A saved-view name is required.", nameof(name));
        var viewport = _viewport.Current ?? _engine.StartupViewport;
        var selected = _selection.Current;
        var now = DateTimeOffset.UtcNow;
        var existing = _savedViews.Views.FirstOrDefault(view => string.Equals(view.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        var state = Current;
        var view = new SavedMapView(
            existing?.Id ?? $"view-{Guid.NewGuid():N}", name.Trim(), null, _engine.SelectedStyleId,
            viewport, MapViewportMode.FitAll, MapOrientationMode.NorthUp,
            state.Overlays.GeometriesVisible, state.Overlays.PolicyVisible, state.Overlays.TrailsVisible,
            state.Overlays.LabelsVisible, ParseSelectionKind(selected.Kind), selected.EntityId,
            existing?.CreatedAt ?? now, now)
        {
            SelectedUnitIds = selected.UnitIds.ToArray(),
            UnitSelectionAnchorId = selected.AnchorUnitId
        };
        await _savedViews.UpsertAsync(view, cancellationToken);
        return ToSnapshot(view);
    }

    public async Task ApplySavedViewAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_savedViews.TryGet(id, out var view) || view is null) throw new KeyNotFoundException($"Saved view '{id}' was not found.");
        if (!string.IsNullOrWhiteSpace(view.StyleId)) await _engine.SelectStyleAsync(view.StyleId, cancellationToken);
        lock (_gate)
        {
            _overlays = new(view.GeometryVisible, view.PolicyVisible, view.TrailsVisible, true, view.VehicleLabelsVisible);
            _followUnitIds = [];
            _followRevision++;
        }
        _viewport.Update(view.Viewport);
        var selected = view.SelectedUnitIds.Count > 0
            ? view.SelectedUnitIds
            : view.SelectionKind == SelectionKind.Vehicle && !string.IsNullOrWhiteSpace(view.SelectionId) ? [view.SelectionId] : [];
        await _selection.SetUnitsAsync(selected, view.UnitSelectionAnchorId ?? selected.FirstOrDefault(), cancellationToken);
        PublishChanged();
    }

    public async Task RemoveSavedViewAsync(string id, CancellationToken cancellationToken = default)
    {
        await _savedViews.RemoveAsync(id, cancellationToken);
        PublishChanged();
    }

    private void OnTelemetryChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        string[] follow;
        lock (_gate) follow = _followUnitIds;
        if (follow.Length == 0) { PublishChanged(); return; }
        var positions = Positions(follow);
        if (positions.Count != follow.Length) { _ = StopFollowingAsync(); return; }
        var center = Mean(positions);
        var current = _viewport.Current;
        if (current is not null)
            _viewport.Update(new MapViewportSnapshot(center.Longitude, center.Latitude, current.Resolution, current.RotationDegrees));
        PublishChanged();
    }

    private IReadOnlyList<(double Longitude, double Latitude)> SelectedPositions() => Positions(_selection.Current.UnitIds);

    private IReadOnlyList<(double Longitude, double Latitude)> Positions(IEnumerable<string> unitIds)
    {
        var results = new List<(double Longitude, double Latitude)>();
        foreach (var unitId in unitIds.Distinct(StringComparer.Ordinal))
        {
            var sample = _telemetry.Items.Where(item => string.Equals(item.VehicleId, unitId, StringComparison.Ordinal))
                .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
            if (sample?.LatitudeDegrees is not { } latitude || sample.LongitudeDegrees is not { } longitude || sample.IsStale ||
                sample.State is AvailabilityState.Offline or AvailabilityState.Faulted || !double.IsFinite(latitude) || !double.IsFinite(longitude))
                return [];
            results.Add((longitude, latitude));
        }
        return results;
    }

    private static (double Longitude, double Latitude) Mean(IReadOnlyList<(double Longitude, double Latitude)> positions)
        => (positions.Average(item => item.Longitude), positions.Average(item => item.Latitude));

    private static double ResolutionFor(IReadOnlyList<(double Longitude, double Latitude)> positions, double? currentResolution)
    {
        if (positions.Count <= 1) return MapNavigationMath.SingleTargetResolution;
        var latitude = positions.Average(item => item.Latitude) * Math.PI / 180;
        var width = (positions.Max(item => item.Longitude) - positions.Min(item => item.Longitude)) * 111_320 * Math.Max(0.1, Math.Cos(latitude));
        var height = (positions.Max(item => item.Latitude) - positions.Min(item => item.Latitude)) * 110_574;
        var fitted = Math.Max(width, height) / 0.65 / 600;
        return Math.Clamp(Math.Max(MapNavigationMath.SingleTargetResolution, fitted), 1, 100_000);
    }

    private void StopFollowingCore() { lock (_gate) { _followUnitIds = []; _followRevision++; } }
    private void PublishChanged() => _changeNotifier.Request();
    private static MapViewportWorkflowSnapshot? ToSnapshot(MapViewportSnapshot? model) => model is null ? null : ToRequiredSnapshot(model);
    private static MapViewportWorkflowSnapshot ToRequiredSnapshot(MapViewportSnapshot model) => new(model.LongitudeDegrees, model.LatitudeDegrees, model.Resolution, model.RotationDegrees);
    private static MapViewportSnapshot ToModel(MapViewportWorkflowSnapshot value) => new(value.LongitudeDegrees, value.LatitudeDegrees, value.ResolutionMetresPerPixel, value.RotationDegrees);
    private static SavedMapViewWorkflowSnapshot ToSnapshot(SavedMapView value) => new(value.Id, value.Name, value.PackageKey, value.StyleId, ToRequiredSnapshot(value.Viewport), value.ViewportMode.ToString(), value.OrientationMode.ToString(), new(value.GeometryVisible, value.PolicyVisible, value.TrailsVisible, true, value.VehicleLabelsVisible), value.SelectedUnitIds.ToArray(), value.UnitSelectionAnchorId, value.SelectionKind.ToString(), value.SelectionId, value.CreatedAt, value.UpdatedAt);
    private static SelectionKind ParseSelectionKind(string kind) => Enum.TryParse<SelectionKind>(kind, true, out var value) ? value : SelectionKind.None;
    private static void Validate(MapViewportWorkflowSnapshot viewport)
    {
        if (!double.IsFinite(viewport.LatitudeDegrees) || viewport.LatitudeDegrees is < -90 or > 90 || !double.IsFinite(viewport.LongitudeDegrees) || viewport.LongitudeDegrees is < -180 or > 180 || !double.IsFinite(viewport.ResolutionMetresPerPixel) || viewport.ResolutionMetresPerPixel <= 0 || !double.IsFinite(viewport.RotationDegrees))
            throw new ArgumentException("Viewport coordinates, resolution, or rotation are invalid.", nameof(viewport));
    }
}

/// <summary>Projects the current Runtime map presentation without exposing rendering objects.</summary>
public sealed class MapSceneObservationWorkflow : IMapSceneObservationWorkflow
{
    private readonly IMapPresentationState _presentation;
    private readonly IMapViewWorkflow _map;
    private readonly ISelectionWorkflow _selection;
    private readonly ITeamSelectionWorkflow _teamSelection;
    private readonly IFormationLockWorkflow _formation;
    private readonly CoalescedChangeNotifier _changeNotifier;

    public MapSceneObservationWorkflow(IMapPresentationState presentation, IMapViewWorkflow map, ISelectionWorkflow selection, ITeamSelectionWorkflow teamSelection, IFormationLockWorkflow formation, IUiDispatcher? dispatcher = null)
    {
        _presentation = presentation;
        _map = map;
        _selection = selection;
        _teamSelection = teamSelection;
        _formation = formation;
        _changeNotifier = new(TimeSpan.FromMilliseconds(5), dispatcher);
        _changeNotifier.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _presentation.Changed += (_, _) => _changeNotifier.Request();
        _map.Changed += (_, _) => _changeNotifier.Request();
        _selection.Changed += (_, _) => _changeNotifier.Request();
        _teamSelection.Changed += (_, _) => _changeNotifier.Request();
        _formation.Changed += (_, _) => _changeNotifier.Request();
    }

    public event EventHandler? Changed;

    public MapSceneSnapshot Current
    {
        get
        {
            var presentation = _presentation.Presentation;
            var scene = presentation.Scene;
            var map = _map.Current;
            var selectedTeamMembers = _teamSelection.Current.MemberUnitIds.ToHashSet(StringComparer.Ordinal);
            return new(
                scene.Frame.ToString(), scene.FrameLabel, MapViewWorkflowSnapshot(scene.RequestedViewport ?? _presentation.CurrentViewport), presentation.Style?.StyleId ?? map.SelectedStyleId,
                presentation.StyleLabel, map.Overlays, _selection.Current,
                scene.Vehicles.Select(item => new MapSceneUnitSnapshot(item.VehicleId, item.Name, item.X, item.Y, item.HeadingDegrees, item.State.ToString(), item.Selected, item.IsGhost)
                {
                    TeamSelected = selectedTeamMembers.Contains(item.VehicleId)
                }).ToArray(),
                scene.Trails.Select(item => new MapSceneTrailSnapshot(item.VehicleId, item.Points.Select(point => new MapScenePoint(point.X, point.Y)).ToArray(), item.State.ToString(), item.Selected)).ToArray(),
                scene.GoToTargets.Select(item => new MapSceneDestinationSnapshot(item.VehicleId, item.LongitudeDegrees, item.LatitudeDegrees, !item.PreviewOnly, item.PreviewOnly)).Concat(scene.FormationPreviewTargets.Select(item => new MapSceneDestinationSnapshot(item.VehicleId, item.LongitudeDegrees, item.LatitudeDegrees, false, true))).ToArray(),
                scene.Geometries.Select(item => new MapSceneGeometrySnapshot(item.GeometryId, item.Name, item.Kind, item.IsPolicy, item.Highlighted)).ToArray(),
                scene.FormationPreviewPaths.Select(path => new MapSceneFormationPathSnapshot(path.Points.Select(point => new MapScenePoint(point.LongitudeDegrees, point.LatitudeDegrees)).ToArray(), path.Closed)).ToArray(),
                scene.OperatorLocation.IsAvailable && scene.OperatorLocation.LatitudeDegrees is { } latitude && scene.OperatorLocation.LongitudeDegrees is { } longitude
                    ? new MapSceneOperatorLocationSnapshot(latitude, longitude, scene.OperatorLocation.AccuracyMeters, scene.OperatorLocation.LastUpdated)
                    : null,
                scene.FormationPreviewTargets.Count > 0 || scene.FormationPreviewPaths.Count > 0, DateTimeOffset.UtcNow)
            {
                TeamSelection = _teamSelection.Current,
                FormationLock = _formation.Current
            };
        }
    }

    private static MapViewportWorkflowSnapshot? MapViewWorkflowSnapshot(MapViewportSnapshot? viewport)
        => viewport is null ? null : new(viewport.LongitudeDegrees, viewport.LatitudeDegrees, viewport.Resolution, viewport.RotationDegrees);
}

public sealed class GhostUnitWorkflow : IGhostUnitWorkflow
{
    private readonly IGhostUnitService _ghosts;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IMapViewWorkflow _map;
    private readonly ISelectionWorkflow _selection;
    private readonly IManualControlService _manual;
    private readonly ITeamWorkflow? _teams;
    private readonly IFormationLockWorkflow? _formation;
    private readonly IGhostProfileWorkflow _profiles;

    public GhostUnitWorkflow(IGhostUnitService ghosts, IEntityStore<string, VehicleRecord> vehicles, IEntityStore<string, VehicleTelemetryRecord> telemetry, IMapViewWorkflow map, ISelectionWorkflow selection, IManualControlService manual, ITeamWorkflow? teams = null, IFormationLockWorkflow? formation = null, IGhostProfileWorkflow? profiles = null)
    {
        _ghosts = ghosts; _vehicles = vehicles; _telemetry = telemetry; _map = map; _selection = selection; _manual = manual; _teams = teams; _formation = formation; _profiles = profiles ?? new GhostProfileWorkflow();
        ((INotifyCollectionChanged)_vehicles.Items).CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        ((INotifyCollectionChanged)_telemetry.Items).CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
    public IReadOnlyList<GhostUnitSnapshot> Ghosts => _ghosts.GhostVehicleIds.Select(Project).Where(item => item is not null).Cast<GhostUnitSnapshot>().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<IReadOnlyList<GhostUnitSnapshot>> CreateAsync(GhostCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Count is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(request.Count), "Create between 1 and 50 Ghosts.");
        if ((request.LatitudeDegrees is null) != (request.LongitudeDegrees is null)) throw new ArgumentException("Provide both latitude and longitude or neither.");
        var viewport = request.LatitudeDegrees is { } latitude && request.LongitudeDegrees is { } longitude
            ? new MapViewportSnapshot(longitude, latitude, _map.Current.Viewport?.ResolutionMetresPerPixel ?? 3, _map.Current.Viewport?.RotationDegrees ?? 0)
            : _map.Current.Viewport is { } current ? new MapViewportSnapshot(current.LongitudeDegrees, current.LatitudeDegrees, current.ResolutionMetresPerPixel, current.RotationDegrees) : null;
        var created = new List<GhostUnitSnapshot>();
        for (var index = 0; index < request.Count; index++)
        {
            if (_profiles.Find(request.ProfileId) is null)
                throw new ArgumentException($"Unknown Ghost profile '{request.ProfileId}'.", nameof(request));
            var vehicle = await _ghosts.CreateAsync(request.ProfileId, viewport, request.HeadingDegrees, cancellationToken);
            created.Add(Project(vehicle.Id) ?? throw new InvalidOperationException("Ghost was created but could not be projected."));
        }
        await _selection.SetUnitsAsync(created.Select(item => item.UnitId).ToArray(), created.FirstOrDefault()?.UnitId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public async Task DeleteAsync(string unitId, CancellationToken cancellationToken = default)
    {
        if (_manual.Snapshot.TargetVehicleId == unitId) await _manual.ReleaseAsync("Ghost unit deleted.", cancellationToken);
        if (_formation is not null) await _formation.HandleUnitDeletedAsync(unitId, cancellationToken);
        await _ghosts.DeleteAsync(unitId, cancellationToken);
        if (_teams is not null) await _teams.RemoveUnitAsync(unitId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private GhostUnitSnapshot? Project(string unitId)
    {
        if (!_vehicles.TryGet(unitId, out var vehicle) || vehicle is null) return null;
        var telemetry = _telemetry.Items.Where(item => item.VehicleId == unitId).OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        if (telemetry?.LatitudeDegrees is not { } latitude || telemetry.LongitudeDegrees is not { } longitude) return null;
        var profile = _profiles.Find(vehicle.ProfileKey);
        return new(unitId, vehicle.Name, vehicle.ConnectionIds.FirstOrDefault() ?? string.Empty, latitude, longitude, telemetry.AltitudeAglMetres ?? 0, telemetry.HeadingDegrees ?? 90, telemetry.Armed && !string.Equals(telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase) ? "Flying" : telemetry.Armed ? "Armed" : "Disarmed", null)
        {
            ProfileId = vehicle.ProfileKey,
            ProfileName = profile?.Name,
            ProfileModel = null
        };
    }
}

public sealed class ManualControlWorkflow : IManualControlWorkflow
{
    private readonly object _gate = new();
    private readonly IManualControlService _manual;
    private ManualControlOwnerKind _ownerKind;
    private string? _ownerId;

    public ManualControlWorkflow(IManualControlService manual)
    {
        _manual = manual;
        _manual.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
    public IReadOnlyList<ManualInputDeviceSnapshot> Devices => _manual.Devices.Select(item => new ManualInputDeviceSnapshot(item.Id, item.Name, item.IsConnected, item.Id == _manual.SelectedDeviceId, item.Kind.ToString(), item.AxisCount, item.ButtonCount)).ToArray();
    public ManualControlProfileSnapshot Profile => ToSnapshot(_manual.Profile);
    public ManualControlReadingSnapshot? LatestReading => _manual.LatestReading is { } reading ? new(reading.LeftX, reading.LeftY, reading.RightX, reading.RightY, reading.LeftBumper, reading.Timestamp, reading.RawAxes) : null;
    public ManualControlSessionWorkflowSnapshot Session => ToSnapshot(_manual.Snapshot, _ownerKind, _ownerId);

    public Task SelectDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => _manual.SetSelectedDeviceAsync(deviceId, cancellationToken);
    public Task SaveProfileAsync(ManualControlProfileSnapshot profile, CancellationToken cancellationToken = default) => _manual.SaveProfileAsync(ToModel(profile), cancellationToken);

    public async Task<bool> TakeControlAsync(string unitId, ManualControlOwnerKind ownerKind, string ownerId, CancellationToken cancellationToken = default)
    {
        if (ownerKind == ManualControlOwnerKind.None || string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("A manual-control owner is required.");
        lock (_gate)
        {
            if (_manual.Snapshot.IsActive && (_ownerKind != ownerKind || !string.Equals(_ownerId, ownerId, StringComparison.Ordinal))) return false;
        }
        var accepted = await _manual.TakeControlAsync(unitId, cancellationToken);
        if (accepted) lock (_gate) { _ownerKind = ownerKind; _ownerId = ownerId; }
        Changed?.Invoke(this, EventArgs.Empty);
        return accepted;
    }

    public async Task ReleaseAsync(ManualControlOwnerKind ownerKind, string ownerId, string reason = "Operator released manual control.", CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_manual.Snapshot.IsActive && (_ownerKind != ownerKind || !string.Equals(_ownerId, ownerId, StringComparison.Ordinal)))
                throw new InvalidOperationException("Manual control is owned by another front end.");
        }
        await _manual.ReleaseAsync(reason, cancellationToken);
        lock (_gate) { _ownerKind = ManualControlOwnerKind.None; _ownerId = null; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyHostActivity(bool active) => _manual.OnApplicationFocusChanged(active);

    private static ManualControlProfileSnapshot ToSnapshot(ManualControlProfile value) => new(value.DeadZone, value.Expo, value.MaximumHorizontalSpeedMetresPerSecond, value.MaximumVerticalSpeedMetresPerSecond, value.MaximumYawRateDegreesPerSecond, value.HorizontalAccelerationMetresPerSecondSquared, value.VerticalAccelerationMetresPerSecondSquared, value.YawAccelerationDegreesPerSecondSquared, value.TakeoffAltitudeAglMetres, (value.JoystickMappings ?? new Dictionary<string, ManualJoystickMapping>()).ToDictionary(item => item.Key, item => ToSnapshot(item.Value), StringComparer.Ordinal));
    private static ManualControlProfile ToModel(ManualControlProfileSnapshot value) => new(value.DeadZone, value.Expo, value.MaximumHorizontalSpeedMetresPerSecond, value.MaximumVerticalSpeedMetresPerSecond, value.MaximumYawRateDegreesPerSecond, value.HorizontalAccelerationMetresPerSecondSquared, value.VerticalAccelerationMetresPerSecondSquared, value.YawAccelerationDegreesPerSecondSquared, value.TakeoffAltitudeAglMetres, (value.JoystickMappings ?? new Dictionary<string, ManualJoystickMappingSnapshot>()).ToDictionary(item => item.Key, item => ToModel(item.Value), StringComparer.Ordinal));
    private static ManualJoystickMappingSnapshot ToSnapshot(ManualJoystickMapping value) => new(value.ForwardAxis, value.ForwardInverted, value.RightAxis, value.RightInverted, value.VerticalAxis, value.VerticalInverted, value.YawAxis, value.YawInverted, value.DeadmanButton, value.ArmButton, value.TakeoffButton, value.ExecuteButton, value.CancelButton, value.ReleaseButton);
    private static ManualJoystickMapping ToModel(ManualJoystickMappingSnapshot value) => new ManualJoystickMapping(value.ForwardAxis, value.ForwardInverted, value.RightAxis, value.RightInverted, value.VerticalAxis, value.VerticalInverted, value.YawAxis, value.YawInverted, value.DeadmanButton, value.ArmButton, value.TakeoffButton, value.ExecuteButton, value.CancelButton, value.ReleaseButton).Normalize();
    private static ManualControlSessionWorkflowSnapshot ToSnapshot(ManualControlSessionSnapshot value, ManualControlOwnerKind owner, string? ownerId) => new(
        value.State.ToString(), value.Backend.ToString(), value.TargetVehicleId, value.TargetName, value.DeviceId, value.DeviceName,
        value.IsActive ? owner : ManualControlOwnerKind.None, value.IsActive ? ownerId : null, value.IsActive, value.DeadmanPressed,
        value.InputNeutral, value.Status, value.PendingButtonAction, value.PendingButtonExpiresAt, value.InputRateHertz,
        value.StartedAt, value.LastInputAt, value.AutopilotMode, value.ModeClass, value.AdmissionStatus,
        value.TransportInputRateHertz, value.LastInputSentAt, value.InputEchoAvailable, value.SafeReleaseMode,
        value.SafeReleaseConfirmed, value.InterruptionReason);
}
