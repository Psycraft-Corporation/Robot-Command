using System.Collections.Specialized;
using System.ComponentModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;
using TeamApi = global::RobotCommand.Sdk.Team.V1;

namespace RobotCommand.Services.Team;

public interface IRobotCommandSnapshotProjector
{
    TeamApi.RobotCommandSnapshot Current { get; }
    bool ShareOperatorLocation { get; set; }
    event EventHandler<TeamApi.RobotCommandSnapshot>? SnapshotChanged;
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class RobotCommandSnapshotProjector : IRobotCommandSnapshotProjector, IHostedService, IDisposable
{
    private sealed record Capture(
        ConnectionRecord[] Connections,
        VehicleRecord[] Vehicles,
        VehicleTelemetryRecord[] Telemetry,
        VehicleDiagnosticsSnapshot[] Diagnostics,
        LinkRecord[] Links,
        OperationalCommandRecord[] Commands,
        OperationalMapPresentation Presentation,
        MapViewportSnapshot? Viewport,
        bool DestinationsVisible,
        string[] SelectedUnitIds);

    private readonly object _gate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly IUnitDefinitionService _units;
    private readonly ISelectionService _selection;
    private readonly IMapPresentationState _map;
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot> _diagnostics;
    private readonly IEntityStore<string, LinkRecord> _links;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private TeamApi.RobotCommandSnapshot _current = new() { CapturedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
    private bool _shareOperatorLocation;
    private int _refreshScheduled;
    private int _refreshRequested;
    private int _disposed;
    private CancellationTokenSource _lifetime = new();

    public RobotCommandSnapshotProjector(
        IUiDispatcher dispatcher,
        IUnitDefinitionService units,
        ISelectionService selection,
        IMapPresentationState map,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, VehicleDiagnosticsSnapshot> diagnostics,
        IEntityStore<string, LinkRecord> links,
        IEntityStore<string, OperationalCommandRecord> commands)
    {
        _dispatcher = dispatcher;
        _units = units;
        _selection = selection;
        _map = map;
        _connections = connections;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _diagnostics = diagnostics;
        _links = links;
        _commands = commands;

        Subscribe(_connections.Items);
        Subscribe(_vehicles.Items);
        Subscribe(_telemetry.Items);
        Subscribe(_diagnostics.Items);
        Subscribe(_links.Items);
        Subscribe(_commands.Items);
        _selection.Changed += OnSourceChanged;
        _units.Changed += OnSourceChanged;
        _map.Changed += OnMapChanged;
    }

    public TeamApi.RobotCommandSnapshot Current
    {
        get { lock (_gate) return _current.Clone(); }
    }

    public bool ShareOperatorLocation
    {
        get { lock (_gate) return _shareOperatorLocation; }
        set
        {
            lock (_gate)
            {
                if (_shareOperatorLocation == value) return;
                _shareOperatorLocation = value;
            }
            if (!value)
            {
                // Sharing can be revoked without waiting for a full map
                // projection. Publish the privacy change immediately.
                PublishOperatorLocationNotShared();
                return;
            }
            ScheduleRefresh();
        }
    }

    public event EventHandler<TeamApi.RobotCommandSnapshot>? SnapshotChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Hosted services start while Avalonia is still completing the window
        // activation path. A synchronous dispatcher round-trip here deadlocks
        // that path: the host is waiting for us while the dispatcher is waiting
        // for the host to return. Schedule the initial capture instead; later
        // store/map changes use the same coalesced, non-blocking path.
        ScheduleRefresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Capture? capture = null;
        await _dispatcher.InvokeAsync(() =>
        {
            var rawVehicles = _vehicles.Items.ToArray();
            capture = new Capture(
                _connections.Items.ToArray(),
                _units.ProjectVehicles(rawVehicles).ToArray(),
                _telemetry.Items.ToArray(),
                _diagnostics.Items.ToArray(),
                _links.Items.ToArray(),
                _commands.Items.ToArray(),
                _map.Presentation,
                _map.CurrentViewport,
                _map.GoToIndicatorsVisible,
                _selection.SelectedUnitIds.ToArray());
        }, cancellationToken);

        if (capture is null) return;
        var projected = await Task.Run(() => Project(capture), cancellationToken);
        lock (_gate)
        {
            // A refresh may have captured the old value while the sharing
            // toggle changed. Never publish a stale shared location after the
            // host has revoked sharing.
            if (!_shareOperatorLocation)
                projected.Map.OperatorLocation = new TeamApi.OperatorLocation { Shared = false, Available = false };
            projected.Revision = _current.Revision + 1;
            _current = projected;
        }
        SnapshotChanged?.Invoke(this, projected.Clone());
    }

    private TeamApi.RobotCommandSnapshot Project(Capture source)
    {
        // A Robot Command may observe another Robot Command, but it must never
        // relay that remote session again. Only locally-owned connections and
        // their projected units are eligible for LAN publication.
        var localConnectionIds = source.Connections
            .Where(connection => connection.Mode != ConnectionMode.TeamObserver)
            .Select(connection => connection.Id)
            .ToHashSet(StringComparer.Ordinal);
        source = source with
        {
            Connections = source.Connections.Where(connection => localConnectionIds.Contains(connection.Id)).ToArray(),
            Vehicles = source.Vehicles.Where(vehicle => vehicle.ConnectionIds.Any(localConnectionIds.Contains)).ToArray(),
            Telemetry = source.Telemetry.Where(item => localConnectionIds.Contains(item.ConnectionId)).ToArray(),
            Diagnostics = source.Diagnostics.Where(item => localConnectionIds.Contains(item.ConnectionId)).ToArray(),
            Links = source.Links.Where(item => localConnectionIds.Contains(item.ConnectionId)).ToArray(),
            Commands = source.Commands.Where(item => item.ConnectionId is null || localConnectionIds.Contains(item.ConnectionId)).ToArray()
        };
        var result = new TeamApi.RobotCommandSnapshot
        {
            CapturedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Map = ProjectMap(source)
        };
        result.Connections.AddRange(source.Connections.Select(ProjectConnection));
        foreach (var vehicle in source.Vehicles)
            result.Units.Add(ProjectUnit(vehicle, source));
        return result;
    }

    private TeamApi.UnitSnapshot ProjectUnit(VehicleRecord vehicle, Capture source)
    {
        var telemetrySourceId = _units.ResolveTelemetrySource(vehicle.Id);
        var diagnosticsSourceId = _units.ResolveDiagnosticsSource(vehicle.Id);
        var telemetry = source.Telemetry.Where(item => item.VehicleId == telemetrySourceId)
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        var diagnostics = source.Diagnostics.Where(item => item.VehicleId == diagnosticsSourceId)
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        var connectionIds = vehicle.ConnectionIds.Distinct(StringComparer.Ordinal).ToArray();
        var authorityConnection = telemetry?.ConnectionId ?? connectionIds.FirstOrDefault() ?? string.Empty;
        var unit = new TeamApi.UnitSnapshot
        {
            Id = vehicle.Id,
            Name = vehicle.Name,
            Backend = BackendName(authorityConnection, source.Connections, vehicle.IsGhost),
            VehicleClass = vehicle.VehicleClass,
            Domain = vehicle.Domain,
            State = ProjectAvailability(vehicle.State),
            IsGhost = vehicle.IsGhost,
            LogosInstanceId = vehicle.LogosInstanceId ?? string.Empty,
            TelemetryAuthorityConnectionId = telemetry?.ConnectionId ?? string.Empty,
            DiagnosticsAuthorityConnectionId = diagnostics?.ConnectionId ?? string.Empty,
            Telemetry = ProjectTelemetry(telemetry),
            Diagnostics = ProjectDiagnostics(diagnostics),
            Actions = ProjectActions(vehicle.Id, source.Commands)
        };
        unit.Capabilities.AddRange(vehicle.CapabilityKeys ?? []);
        unit.ConnectionIds.AddRange(connectionIds);
        unit.Links.AddRange(source.Links.Where(link => connectionIds.Contains(link.ConnectionId, StringComparer.Ordinal)).Select(ProjectLink));
        return unit;
    }

    private static TeamApi.ConnectionSnapshot ProjectConnection(ConnectionRecord connection)
        => new()
        {
            Id = connection.Id,
            Name = connection.Name,
            Target = SanitizeTarget(connection.Target),
            Mode = connection.Mode.ToString(),
            State = ProjectAvailability(connection.State),
            AutoReconnect = connection.AutoReconnect,
            LogosInstanceId = connection.LogosInstanceId ?? string.Empty,
            RuntimeRole = connection.RuntimeRole ?? string.Empty,
            ConnectedAt = Time(connection.ConnectedAt),
            LastSeen = Time(connection.LastSeen),
            LastError = connection.LastError ?? string.Empty,
            IsGhost = connection.IsGhost
        };

    private static TeamApi.TelemetrySnapshot ProjectTelemetry(VehicleTelemetryRecord? telemetry)
    {
        if (telemetry is null) return new TeamApi.TelemetrySnapshot { Reported = false };
        var result = new TeamApi.TelemetrySnapshot
        {
            Reported = true,
            Armed = telemetry.Armed,
            LandedState = telemetry.LandedState,
            Mode = telemetry.AirframeMode,
            Stale = telemetry.IsStale,
            Code = telemetry.Code,
            Message = telemetry.Message,
            ObservedAt = Time(telemetry.ObservedAt)
        };
        if (telemetry.LatitudeDegrees is { } latitude) result.LatitudeDegrees = latitude;
        if (telemetry.LongitudeDegrees is { } longitude) result.LongitudeDegrees = longitude;
        if (telemetry.AltitudeMslMetres is { } msl) result.AltitudeMslMetres = msl;
        if (telemetry.AltitudeAglMetres is { } agl) result.AltitudeAglMetres = agl;
        if (telemetry.LocalNorthMetres is { } north) result.LocalNorthMetres = north;
        if (telemetry.LocalEastMetres is { } east) result.LocalEastMetres = east;
        if (telemetry.LocalDownMetres is { } down) result.LocalDownMetres = down;
        if (telemetry.VelocityNorthMetresPerSecond is { } velocityNorth) result.VelocityNorthMetresPerSecond = velocityNorth;
        if (telemetry.VelocityEastMetresPerSecond is { } velocityEast) result.VelocityEastMetresPerSecond = velocityEast;
        if (telemetry.VelocityDownMetresPerSecond is { } velocityDown) result.VelocityDownMetresPerSecond = velocityDown;
        if (telemetry.HeadingDegrees is { } heading) result.HeadingDegrees = heading;
        return result;
    }

    private static TeamApi.DiagnosticsSnapshot ProjectDiagnostics(VehicleDiagnosticsSnapshot? diagnostics)
    {
        if (diagnostics is null) return new TeamApi.DiagnosticsSnapshot { Reported = false };
        var result = new TeamApi.DiagnosticsSnapshot
        {
            Reported = true,
            Backend = diagnostics.Backend,
            OverallStatus = ProjectDiagnosticStatus(diagnostics.OverallStatus),
            Summary = diagnostics.Summary,
            ArmReadiness = ProjectDiagnosticStatus(diagnostics.ArmReadiness),
            ArmReadinessDetail = diagnostics.ArmReadinessDetail,
            NavigationReadiness = ProjectDiagnosticStatus(diagnostics.NavigationReadiness),
            NavigationReadinessDetail = diagnostics.NavigationReadinessDetail,
            TelemetryStatus = ProjectDiagnosticStatus(diagnostics.TelemetryStatus),
            TelemetryDetail = diagnostics.TelemetryDetail,
            ObservedAt = Time(diagnostics.ObservedAt),
            SystemId = diagnostics.SystemId ?? 0,
            ComponentId = diagnostics.ComponentId ?? 0,
            Version = diagnostics.Version,
            Mode = diagnostics.Mode
        };
        result.Checks.AddRange(diagnostics.Checks.Select(check =>
        {
            var projected = new TeamApi.DiagnosticCheck
            {
                Code = check.Code,
                Category = check.Category,
                Name = check.Name,
                State = (TeamApi.DiagnosticCheckState)((int)check.State + 1),
                Detail = check.Detail
            };
            projected.AffectedOperations.AddRange(check.AffectedOperations?.Select(item => item.ToString()) ?? []);
            return projected;
        }));
        result.RecentMessages.AddRange(diagnostics.RecentMessages.Select(message => new TeamApi.DiagnosticMessage
        {
            Id = message.Id,
            Timestamp = Time(message.Timestamp),
            Severity = message.Severity.ToString(),
            Text = message.Text,
            Source = message.Source
        }));
        return result;
    }

    private static TeamApi.ActionSnapshot ProjectActions(string vehicleId, OperationalCommandRecord[] commands)
    {
        var matching = commands.Where(item => string.Equals(item.VehicleId ?? item.TargetId, vehicleId, StringComparison.Ordinal))
            .OrderByDescending(item => item.UpdatedAt).ToArray();
        var current = matching.FirstOrDefault(item => item.State is OperationalCommandState.Submitting or OperationalCommandState.Accepted or OperationalCommandState.InProgress);
        var queued = matching.FirstOrDefault(item => item.State == OperationalCommandState.Draft);
        return new TeamApi.ActionSnapshot
        {
            CurrentKind = current?.Kind ?? string.Empty,
            CurrentState = current?.State.ToString() ?? string.Empty,
            CurrentSummary = current?.Summary ?? string.Empty,
            QueuedKind = queued?.Kind ?? string.Empty,
            QueuedState = queued?.State.ToString() ?? string.Empty,
            QueuedSummary = queued?.Summary ?? string.Empty,
            UpdatedAt = Time(current?.UpdatedAt ?? queued?.UpdatedAt)
        };
    }

    private static TeamApi.LinkSnapshot ProjectLink(LinkRecord link)
    {
        var result = new TeamApi.LinkSnapshot
        {
            Id = link.LinkId,
            ConnectionId = link.ConnectionId,
            Name = link.Name,
            Kind = link.Kind,
            Direction = link.Direction,
            State = link.State,
            Health = link.Health,
            Connected = link.Connected,
            Stale = link.IsStale,
            Code = link.Code,
            Message = link.Message,
            ObservedAt = Time(link.ObservedAt)
        };
        if (link.RssiDbm is { } rssi) result.RssiDbm = rssi;
        if (link.SnrDb is { } snr) result.SnrDb = snr;
        if (link.Quality is { } quality) result.Quality = quality;
        if (link.PacketLoss is { } loss) result.PacketLoss = loss;
        if (link.LatencyMilliseconds is { } latency) result.LatencyMilliseconds = latency;
        return result;
    }

    private TeamApi.MapSnapshot ProjectMap(Capture source)
    {
        var presentation = source.Presentation;
        var scene = presentation.Scene;
        var localVehicleIds = source.Vehicles.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var map = new TeamApi.MapSnapshot
        {
            CoordinateFrame = scene.Frame.ToString(),
            FrameLabel = scene.FrameLabel,
            Viewport = ProjectViewport(source.Viewport),
            StyleId = presentation.Style?.StyleId ?? string.Empty,
            StyleName = presentation.Style?.DisplayName ?? presentation.StyleLabel,
            StyleAttribution = presentation.Attribution,
            GeometryVisible = scene.GeometryVisible,
            PolicyVisible = scene.PolicyVisible,
            TrailsVisible = scene.TrailsVisible,
            DestinationsVisible = source.DestinationsVisible,
            LabelsVisible = scene.VehicleLabelsVisible
        };
        map.SelectedUnitIds.AddRange(source.SelectedUnitIds.Where(localVehicleIds.Contains));
        var motionByVehicleId = presentation.Motion.Vehicles
            .Where(item => item.Frame == MapFrameKind.GlobalWgs84 && localVehicleIds.Contains(item.VehicleId))
            .ToDictionary(item => item.VehicleId, StringComparer.Ordinal);
        map.UnitVisuals.AddRange(scene.Vehicles.Where(vehicle => localVehicleIds.Contains(vehicle.VehicleId)).Select(vehicle =>
        {
            motionByVehicleId.TryGetValue(vehicle.VehicleId, out var motion);
            var visual = new TeamApi.MapUnitVisual
            {
                UnitId = vehicle.VehicleId,
                Name = vehicle.Name,
                X = motion?.X ?? vehicle.X,
                Y = motion?.Y ?? vehicle.Y,
                State = ProjectAvailability(vehicle.State),
                Selected = vehicle.Selected,
                Ghost = vehicle.IsGhost
            };
            if (motion?.HeadingDegrees is { } motionHeading)
                visual.HeadingDegrees = motionHeading;
            else if (vehicle.HeadingDegrees is { } heading)
                visual.HeadingDegrees = heading;
            return visual;
        }));
        map.Trails.AddRange(scene.Trails.Where(trail => localVehicleIds.Contains(trail.VehicleId)).Select(trail =>
        {
            var projected = new TeamApi.MapTrail
            {
                UnitId = trail.VehicleId,
                Name = trail.Name,
                ConnectionId = trail.ConnectionId,
                State = ProjectAvailability(trail.State),
                Selected = trail.Selected
            };
            projected.Points.AddRange(trail.Points.Select(ProjectPoint));
            return projected;
        }));
        map.Geometries.AddRange(scene.Geometries.Select(geometry =>
        {
            var projected = new TeamApi.MapGeometry
            {
                Id = geometry.GeometryId,
                Name = geometry.Name,
                Kind = geometry.Kind,
                Closed = geometry.Closed,
                PolicyConstraint = geometry.PolicyConstraint,
                PolicyKind = geometry.PolicyKind,
                Highlighted = geometry.Highlighted
            };
            projected.Points.AddRange(geometry.Points.Select(ProjectPoint));
            projected.Rings.AddRange(geometry.Rings.Select(ring =>
            {
                var projectedRing = new TeamApi.GeoRing();
                projectedRing.Points.AddRange(ring.Select(ProjectPoint));
                return projectedRing;
            }));
            return projected;
        }));
        map.Destinations.AddRange(scene.GoToTargets.Where(target => localVehicleIds.Contains(target.VehicleId)).Select(ProjectDestination));
        map.FormationPreviewDestinations.AddRange(scene.FormationPreviewTargets.Where(target => localVehicleIds.Contains(target.VehicleId)).Select(ProjectDestination));
        map.FormationPreviewPaths.AddRange(scene.FormationPreviewPaths.Select(path =>
        {
            var projected = new TeamApi.MapPath { Closed = path.Closed };
            projected.Points.AddRange(path.Points.Select(point => new TeamApi.GeoPoint { X = point.LongitudeDegrees, Y = point.LatitudeDegrees }));
            return projected;
        }));

        bool share;
        lock (_gate) share = _shareOperatorLocation;
        if (share)
        {
            var location = scene.OperatorLocation;
            map.OperatorLocation = new TeamApi.OperatorLocation
            {
                Shared = true,
                Available = location.IsAvailable,
                ObservedAt = Time(location.LastUpdated)
            };
            if (location.LatitudeDegrees is { } latitude) map.OperatorLocation.LatitudeDegrees = latitude;
            if (location.LongitudeDegrees is { } longitude) map.OperatorLocation.LongitudeDegrees = longitude;
            if (location.AccuracyMeters is { } accuracy) map.OperatorLocation.AccuracyMetres = accuracy;
        }
        else map.OperatorLocation = new TeamApi.OperatorLocation { Shared = false, Available = false };
        return map;
    }

    private static TeamApi.MapViewport ProjectViewport(MapViewportSnapshot? viewport)
        => viewport is null ? new TeamApi.MapViewport { Reported = false } : new TeamApi.MapViewport
        {
            Reported = true,
            LongitudeDegrees = viewport.LongitudeDegrees,
            LatitudeDegrees = viewport.LatitudeDegrees,
            ResolutionMetresPerPixel = viewport.Resolution,
            RotationDegrees = viewport.RotationDegrees
        };

    private static TeamApi.GeoPoint ProjectPoint(OperationalPoint point)
        => new() { X = point.X, Y = point.Y, AltitudeMetres = point.Z };

    private static TeamApi.MapDestination ProjectDestination(MapGoToTargetVisual target)
        => new()
        {
            UnitId = target.VehicleId,
            LatitudeDegrees = target.LatitudeDegrees,
            LongitudeDegrees = target.LongitudeDegrees,
            Selected = target.Selected,
            Preview = target.PreviewOnly
        };

    private static TeamApi.Availability ProjectAvailability(AvailabilityState state)
        => (TeamApi.Availability)((int)state + 1);

    private static TeamApi.DiagnosticStatus ProjectDiagnosticStatus(VehicleDiagnosticStatus state)
        => (TeamApi.DiagnosticStatus)((int)state + 1);

    private static Timestamp Time(DateTimeOffset? value)
        => Timestamp.FromDateTimeOffset(value ?? DateTimeOffset.UnixEpoch);

    private static string BackendName(string connectionId, ConnectionRecord[] connections, bool ghost)
    {
        if (ghost) return "Ghost";
        var connection = connections.FirstOrDefault(item => item.Id == connectionId);
        return connection?.Mode switch
        {
            ConnectionMode.Mavlink => "MAVLink",
            ConnectionMode.FieldLink => "Logos LinkD",
            ConnectionMode.Direct => "Logos",
            _ => "Unknown"
        };
    }

    private static string SanitizeTarget(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            (string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)))
            return target;
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.ToString();
    }

    private void Subscribe(INotifyCollectionChanged collection) => collection.CollectionChanged += OnCollectionChanged;
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();
    private void OnSourceChanged(object? sender, EventArgs e) => ScheduleRefresh();
    private void OnMapChanged(object? sender, EventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        // Keep a dirty bit in addition to the scheduled bit. A source can
        // change while a refresh is being captured; that change must result
        // in another snapshot instead of being silently coalesced away.
        Volatile.Write(ref _refreshRequested, 1);
        if (Interlocked.Exchange(ref _refreshScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
                {
                    await Task.Delay(50, _lifetime.Token);
                    await RefreshAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _refreshScheduled, 0);
                if (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
                    ScheduleRefresh();
            }
        });
    }

    private void PublishOperatorLocationNotShared()
    {
        TeamApi.RobotCommandSnapshot next;
        lock (_gate)
        {
            next = _current.Clone();
            next.Map.OperatorLocation = new TeamApi.OperatorLocation
            {
                Shared = false,
                Available = false
            };
            next.Revision = _current.Revision + 1;
            _current = next;
        }
        SnapshotChanged?.Invoke(this, next.Clone());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _selection.Changed -= OnSourceChanged;
        _units.Changed -= OnSourceChanged;
        _map.Changed -= OnMapChanged;
    }
}
