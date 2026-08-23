using System.Collections.Specialized;
using Microsoft.Extensions.Hosting;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Operations;
using RobotCommand.State;

namespace RobotCommand.Services.Maps;

/// <summary>Maintains a complete non-rendering map scene for the LAN observer server.</summary>
public sealed class HeadlessMapPresentationService : IHostedService, IDisposable
{
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, GeometryOverlayRecord> _geometries;
    private readonly IOperationalMapSceneBuilder _builder;
    private readonly IOperationalMapEngine _engine;
    private readonly IMapPresentationState _presentation;
    private readonly IMapViewportState _viewport;
    private readonly IVehicleTrackHistory _tracks;
    private readonly ISelectionWorkflow _selection;
    private readonly IMapViewWorkflow _map;
    private readonly CoalescedChangeNotifier _rebuildNotifier;
    private int _rebuildQueued;
    private int _rebuildRequested;
    private string _vehicleStructureKey = string.Empty;

    public HeadlessMapPresentationService(
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, GeometryOverlayRecord> geometries,
        IOperationalMapSceneBuilder builder,
        IOperationalMapEngine engine,
        IMapPresentationState presentation,
        IMapViewportState viewport,
        IVehicleTrackHistory tracks,
        ISelectionWorkflow selection,
        IMapViewWorkflow map,
        IUiDispatcher? dispatcher = null)
    {
        _vehicles = vehicles;
        _telemetry = telemetry;
        _geometries = geometries;
        _builder = builder;
        _engine = engine;
        _presentation = presentation;
        _viewport = viewport;
        _tracks = tracks;
        _selection = selection;
        _map = map;
        _rebuildNotifier = new(TimeSpan.FromMilliseconds(5), dispatcher);
        _rebuildNotifier.Changed += (_, _) => QueueRebuild();
        Subscribe(_vehicles.Items, OnChanged);
        Subscribe(_telemetry.Items, OnTelemetryChanged);
        Subscribe(_geometries.Items, OnChanged);
        _selection.Changed += OnWorkflowChanged;
        _map.Changed += OnWorkflowChanged;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Rebuild();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Subscribe(INotifyCollectionChanged collection, NotifyCollectionChangedEventHandler handler)
        => collection.CollectionChanged += handler;

    private void OnTelemetryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var scene = _presentation.Presentation.Scene;
        if (scene.Frame == MapFrameKind.Unknown)
        {
            _rebuildNotifier.Request();
            return;
        }

        var motion = _builder.BuildMotion(_vehicles.Items, _telemetry.Items, scene.Frame, DateTimeOffset.UtcNow);
        var sceneIds = scene.Vehicles.Select(vehicle => vehicle.VehicleId).ToHashSet(StringComparer.Ordinal);
        var motionIds = motion.Vehicles.Select(vehicle => vehicle.VehicleId).ToHashSet(StringComparer.Ordinal);
        if (motion.Frame != scene.Frame || !sceneIds.SetEquals(motionIds))
        {
            _rebuildNotifier.Request();
            return;
        }

        _presentation.UpdateMotion(motion);
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _vehicles.Items))
        {
            var structureKey = CreateVehicleStructureKey(_vehicles.Items);
            if (string.Equals(_vehicleStructureKey, structureKey, StringComparison.Ordinal))
            {
                return;
            }

            _vehicleStructureKey = structureKey;
        }

        _rebuildNotifier.Request();
    }

    private static string CreateVehicleStructureKey(IEnumerable<VehicleRecord> vehicles)
        => string.Join("|", vehicles
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => string.Join("\u001f", [
                item.Id,
                item.Name,
                string.Join(",", item.ConnectionIds.OrderBy(id => id, StringComparer.Ordinal)),
                item.LogosInstanceId ?? string.Empty,
                item.TeamId ?? string.Empty,
                item.VehicleClass,
                item.Domain,
                item.ProfileKey,
                item.IsGhost.ToString()
            ])));

    private void OnWorkflowChanged(object? sender, EventArgs e) => _rebuildNotifier.Request();

    private void QueueRebuild()
    {
        Volatile.Write(ref _rebuildRequested, 1);
        if (Interlocked.Exchange(ref _rebuildQueued, 1) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _rebuildRequested, 0) == 1)
                    Rebuild();
            }
            finally
            {
                Interlocked.Exchange(ref _rebuildQueued, 0);
                if (Volatile.Read(ref _rebuildRequested) == 1)
                    QueueRebuild();
            }
        });
    }

    private void Rebuild()
    {
        _vehicleStructureKey = CreateVehicleStructureKey(_vehicles.Items);
        _tracks.Record(_telemetry.Items, DateTimeOffset.UtcNow);
        var map = _map.Current;
        var selectedIds = _selection.Current.UnitIds.ToHashSet(StringComparer.Ordinal);
        var scene = _builder.Build(
            _vehicles.Items,
            _telemetry.Items,
            _geometries.Items,
            selectedVehicleId: _selection.Current.PrimaryUnitId,
            MapViewportMode.FitAll,
            geometryVisible: map.Overlays.GeometriesVisible,
            policyVisible: map.Overlays.PolicyVisible,
            highlightedGeometryIds: new HashSet<string>(StringComparer.Ordinal),
            selectedVehicleIds: selectedIds);
        scene = scene with
        {
            Trails = scene.Frame == MapFrameKind.GlobalWgs84
                ? _tracks.BuildTrailsForSelectedVehicles(_vehicles.Items, selectedIds)
                : [],
            TrailsVisible = map.Overlays.TrailsVisible,
            VehicleLabelsVisible = map.Overlays.LabelsVisible
        };
        var prepared = _engine.Prepare(scene);
        var motion = _builder.BuildMotion(
            _vehicles.Items,
            _telemetry.Items,
            prepared.Scene.Frame,
            DateTimeOffset.UtcNow);
        prepared = prepared with { Motion = motion };
        var viewport = _viewport.Current ?? prepared.Scene.RequestedViewport ?? _engine.StartupViewport;
        if (viewport is not null) _viewport.Update(viewport);
        _presentation.Update(prepared, viewport, map.Overlays.DestinationsVisible);
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)_vehicles.Items).CollectionChanged -= OnChanged;
        ((INotifyCollectionChanged)_telemetry.Items).CollectionChanged -= OnTelemetryChanged;
        ((INotifyCollectionChanged)_geometries.Items).CollectionChanged -= OnChanged;
        _selection.Changed -= OnWorkflowChanged;
        _map.Changed -= OnWorkflowChanged;
        _rebuildNotifier.Dispose();
    }
}
