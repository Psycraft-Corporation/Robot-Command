using System.Collections.Specialized;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.State;

namespace RobotCommand.Services.Reconciliation;

/// <summary>Resolves configured unit routes while keeping control-route changes operator-confirmed.</summary>
public sealed class UnitRouteFailoverService : IUnitRoutingWorkflow, IDisposable
{
    private readonly object _gate = new();
    private readonly IUnitAssociationWorkflow _units;
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot> _diagnostics;
    private readonly IEntityStore<string, CameraSourceRecord> _cameras;
    private readonly AppConfiguration _configuration;
    private readonly Dictionary<(string UnitId, UnitRouteRole Role), UnitRouteCandidate> _active = [];
    private IReadOnlyList<UnitRouteStatus> _routes = [];

    public UnitRouteFailoverService(
        IUnitAssociationWorkflow units,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, VehicleDiagnosticsSnapshot> diagnostics,
        IEntityStore<string, CameraSourceRecord> cameras,
        AppConfiguration configuration)
    {
        _units = units;
        _connections = connections;
        _telemetry = telemetry;
        _diagnostics = diagnostics;
        _cameras = cameras;
        _configuration = configuration;
        _units.Changed += OnChanged;
        Observe(_connections.Items);
        Observe(_telemetry.Items);
        Observe(_diagnostics.Items);
        Observe(_cameras.Items);
        Refresh();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<UnitRouteStatus> Routes
    {
        get { Refresh(); lock (_gate) return _routes.ToArray(); }
    }

    public IReadOnlyList<UnitRouteStatus> ForUnit(string unitId)
        => Routes.Where(item => item.UnitId == unitId).ToArray();

    public string? ActiveConnectionFor(string unitId, UnitRouteRole role)
    {
        var status = ForUnit(unitId).FirstOrDefault(item => item.Role == role);
        return status?.Health is UnitRouteHealth.Healthy or UnitRouteHealth.Degraded
            ? status.Active?.ConnectionId
            : null;
    }

    public Task<UnitRouteStatus> SelectAsync(
        string unitId,
        UnitRouteRole role,
        int candidateIndex,
        bool confirmControlSwitch,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = ForUnit(unitId).FirstOrDefault(item => item.Role == role)
            ?? throw new KeyNotFoundException($"No {role} route is configured for unit '{unitId}'.");
        if (candidateIndex < 0 || candidateIndex >= status.Candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(candidateIndex), "Select a configured route candidate.");
        var candidate = status.Candidates[candidateIndex];
        if (!IsHealthy(candidate, role)) throw new InvalidOperationException("The selected route is not healthy and cannot become active.");
        var previous = status.Active;
        if (role is UnitRouteRole.Command or UnitRouteRole.Gimbal && previous != candidate && !confirmControlSwitch)
            throw new InvalidOperationException("Switching a command or gimbal route requires explicit operator confirmation.");
        lock (_gate) _active[(unitId, role)] = candidate;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(ForUnit(unitId).First(item => item.Role == role));
    }

    public void Dispose()
    {
        _units.Changed -= OnChanged;
        Unobserve(_connections.Items);
        Unobserve(_telemetry.Items);
        Unobserve(_diagnostics.Items);
        Unobserve(_cameras.Items);
    }

    private void OnChanged(object? sender, EventArgs e) => RefreshAndNotify();
    private void Observe(System.Collections.ObjectModel.ReadOnlyObservableCollection<ConnectionRecord> items)
        => ((INotifyCollectionChanged)items).CollectionChanged += OnCollectionChanged;
    private void Observe(System.Collections.ObjectModel.ReadOnlyObservableCollection<VehicleTelemetryRecord> items)
        => ((INotifyCollectionChanged)items).CollectionChanged += OnCollectionChanged;
    private void Observe(System.Collections.ObjectModel.ReadOnlyObservableCollection<VehicleDiagnosticsSnapshot> items)
        => ((INotifyCollectionChanged)items).CollectionChanged += OnCollectionChanged;
    private void Observe(System.Collections.ObjectModel.ReadOnlyObservableCollection<CameraSourceRecord> items)
        => ((INotifyCollectionChanged)items).CollectionChanged += OnCollectionChanged;
    private void Unobserve(System.Collections.ObjectModel.ReadOnlyObservableCollection<ConnectionRecord> items)
        => ((INotifyCollectionChanged)items).CollectionChanged -= OnCollectionChanged;
    private void Unobserve(System.Collections.ObjectModel.ReadOnlyObservableCollection<VehicleTelemetryRecord> items)
        => ((INotifyCollectionChanged)items).CollectionChanged -= OnCollectionChanged;
    private void Unobserve(System.Collections.ObjectModel.ReadOnlyObservableCollection<VehicleDiagnosticsSnapshot> items)
        => ((INotifyCollectionChanged)items).CollectionChanged -= OnCollectionChanged;
    private void Unobserve(System.Collections.ObjectModel.ReadOnlyObservableCollection<CameraSourceRecord> items)
        => ((INotifyCollectionChanged)items).CollectionChanged -= OnCollectionChanged;
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAndNotify();

    private void RefreshAndNotify()
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var next = new List<UnitRouteStatus>();
        lock (_gate)
        {
            foreach (var unit in _units.Units)
            {
                foreach (var group in (unit.Routes ?? []).GroupBy(item => item.Role))
                {
                    var candidates = group.ToArray();
                    if (candidates.Length == 0) continue;
                    var key = (unit.Id, group.Key);
                    if (!_active.TryGetValue(key, out var active))
                    {
                        active = candidates[0];
                        _active[key] = active;
                    }

                    var activeHealthy = IsHealthy(active, group.Key);
                    var anyHealthy = candidates.Any(item => IsHealthy(item, group.Key));
                    var usingStandby = activeHealthy && candidates[0] != active &&
                        group.Key is UnitRouteRole.Telemetry or UnitRouteRole.Diagnostics or UnitRouteRole.Video;
                    var health = usingStandby ? UnitRouteHealth.Degraded : activeHealthy ? UnitRouteHealth.Healthy : UnitRouteHealth.Unavailable;
                    var detail = usingStandby
                        ? "A healthy standby route remains selected; recovery will not switch the route back automatically."
                        : activeHealthy ? "Active route is healthy." : "Active route is unavailable.";
                    if (!activeHealthy && group.Key is UnitRouteRole.Telemetry or UnitRouteRole.Diagnostics or UnitRouteRole.Video)
                    {
                        var fallback = candidates.FirstOrDefault(item => IsHealthy(item, group.Key));
                        if (fallback is not null)
                        {
                            active = fallback;
                            _active[key] = fallback;
                            health = UnitRouteHealth.Degraded;
                            detail = "Using a healthy standby route; recovery will not switch the route back automatically.";
                        }
                    }
                    else if (!activeHealthy && group.Key is UnitRouteRole.Command or UnitRouteRole.Gimbal && anyHealthy)
                    {
                        health = UnitRouteHealth.AwaitingConfirmation;
                        detail = "A standby route is available. Operator confirmation is required before switching control.";
                    }
                    next.Add(new UnitRouteStatus(unit.Id, group.Key, candidates, active, health, detail, now));
                }
            }
            _routes = next;
            var known = next.Select(item => (item.UnitId, item.Role)).ToHashSet();
            foreach (var stale in _active.Keys.Where(item => !known.Contains(item)).ToArray()) _active.Remove(stale);
        }
    }

    private bool IsHealthy(UnitRouteCandidate candidate, UnitRouteRole role)
    {
        if (role == UnitRouteRole.Video)
        {
            var source = _cameras.Items.FirstOrDefault(item => item.CameraSourceId == candidate.MediaSourceId && item.ConnectionId.StartsWith("media:", StringComparison.Ordinal));
            return source is { State: AvailabilityState.Online, Fresh: true } && DateTimeOffset.UtcNow - source.ObservedAt <= TimeSpan.FromSeconds(30);
        }
        if (string.IsNullOrWhiteSpace(candidate.ConnectionId) || !_connections.TryGet(candidate.ConnectionId, out var connection) || connection?.State != AvailabilityState.Online)
            return false;
        if (role == UnitRouteRole.Gimbal)
        {
            var camera = _cameras.Items.FirstOrDefault(item => item.ConnectionId == candidate.ConnectionId && item.CameraSourceId == candidate.CameraSourceId);
            return camera is { SupportsGimbal: true, State: AvailabilityState.Online, Fresh: true };
        }
        var telemetry = _telemetry.Items.Where(item => item.VehicleId == candidate.VehicleId && item.ConnectionId == candidate.ConnectionId)
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        var telemetryFresh = telemetry is { IsStale: false } && DateTimeOffset.UtcNow - telemetry.ObservedAt <= TimeSpan.FromSeconds(Math.Max(5, _configuration.StaleAfterSeconds));
        if (!telemetryFresh) return false;
        if (role != UnitRouteRole.Diagnostics) return true;
        var diagnostics = _diagnostics.Items.Where(item => item.VehicleId == candidate.VehicleId && item.ConnectionId == candidate.ConnectionId)
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        return diagnostics is not null && DateTimeOffset.UtcNow - diagnostics.ObservedAt <= TimeSpan.FromSeconds(Math.Max(5, _configuration.StaleAfterSeconds));
    }
}
