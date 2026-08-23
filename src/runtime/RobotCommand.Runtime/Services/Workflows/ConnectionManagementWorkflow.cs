using System.Collections.Specialized;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Location;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

/// <summary>
/// The single saved-connection lifecycle used by Avalonia and the CLI. Protocol
/// implementations remain behind ILogosConnectionManager; callers only receive
/// safe immutable projections.
/// </summary>
public sealed class ConnectionManagementWorkflow : IConnectionManagementWorkflow
{
    private readonly ILogosConnectionManager _manager;
    private readonly IConnectionPersistence _persistence;
    private readonly IEntityStore<string, ConnectionRecord> _connections;

    public ConnectionManagementWorkflow(
        ILogosConnectionManager manager,
        IConnectionPersistence persistence,
        IEntityStore<string, ConnectionRecord> connections)
    {
        _manager = manager;
        _persistence = persistence;
        _connections = connections;
        ((INotifyCollectionChanged)_connections.Items).CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ManagedConnectionSnapshot> Connections => _manager.Definitions
        .Where(item => !item.IsGhost && item.Mode != ConnectionMode.TeamObserver)
        .Select(item => ToSnapshot(item, _connections.TryGet(item.Id, out var record) ? record : null))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool TryGet(string connectionId, out ManagedConnectionSnapshot? connection)
    {
        connection = Connections.FirstOrDefault(item => string.Equals(item.Id, connectionId, StringComparison.Ordinal));
        return connection is not null;
    }

    public async Task<ManagedConnectionSnapshot> CreateAsync(ConnectionMutationRequest request, CancellationToken cancellationToken = default)
    {
        var definition = ToDefinition(request, request.Id);
        if (_manager.TryGetDefinition(definition.Id, out _))
            throw new InvalidOperationException($"A connection with ID '{definition.Id}' already exists.");

        await _manager.RegisterAsync(definition, cancellationToken);
        await PersistAsync(cancellationToken);
        return RequireSnapshot(definition.Id);
    }

    public async Task<ManagedConnectionSnapshot> UpdateAsync(string connectionId, ConnectionMutationRequest request, CancellationToken cancellationToken = default)
    {
        if (!_manager.TryGetDefinition(connectionId, out var existing) || existing is null)
            throw new KeyNotFoundException($"Unknown connection '{connectionId}'.");
        EnsureSavedConnection(existing);
        var definition = ToDefinition(request, connectionId);
        await _manager.UpdateAsync(definition, cancellationToken);
        await PersistAsync(cancellationToken);
        return RequireSnapshot(connectionId);
    }

    public async Task RemoveAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var definition = RequireDefinition(connectionId);
        EnsureSavedConnection(definition);
        await _manager.RemoveAsync(connectionId, cancellationToken);
        await PersistAsync(cancellationToken);
    }

    public Task ConnectAsync(string connectionId, ConnectionCredentialInput? credentials = null, CancellationToken cancellationToken = default)
    {
        var definition = RequireDefinition(connectionId);
        EnsureSavedConnection(definition);
        var input = credentials ?? new ConnectionCredentialInput();
        return _manager.ConnectAsync(connectionId, new ConnectionCredentials(input.ApiKey, input.BearerToken), cancellationToken);
    }

    public Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var definition = RequireDefinition(connectionId);
        EnsureSavedConnection(definition);
        return _manager.DisconnectAsync(connectionId, cancellationToken);
    }

    public Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var definition = RequireDefinition(connectionId);
        EnsureSavedConnection(definition);
        return _manager.RefreshAsync(connectionId, cancellationToken);
    }

    private ConnectionDefinition RequireDefinition(string connectionId)
        => _manager.TryGetDefinition(connectionId, out var definition) && definition is not null
            ? definition
            : throw new KeyNotFoundException($"Unknown connection '{connectionId}'.");

    private ManagedConnectionSnapshot RequireSnapshot(string connectionId)
        => TryGet(connectionId, out var snapshot) && snapshot is not null
            ? snapshot
            : throw new InvalidOperationException($"Connection '{connectionId}' was not projected after the operation.");

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var profiles = _manager.Definitions
            .Where(item => !item.IsGhost && item.Mode is not ConnectionMode.Ghost and not ConnectionMode.TeamObserver)
            .Select(item => new ConnectionProfile(item.Name, item.Target, item.Description, item.Id, item.Mode, item.AutoConnect, item.AutoReconnect, item.Mavlink, item.Linkd))
            .ToArray();
        await _persistence.SaveAsync(profiles, cancellationToken);
    }

    private static void EnsureSavedConnection(ConnectionDefinition definition)
    {
        if (definition.IsGhost || definition.Mode is ConnectionMode.Ghost or ConnectionMode.TeamObserver)
            throw new InvalidOperationException("Session-only ghost and observer connections are read-only.");
    }

    internal static ConnectionDefinition ToDefinition(ConnectionMutationRequest request, string? stableId)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Connection name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Target)) throw new ArgumentException("Connection target is required.", nameof(request));
        var mode = request.Mode switch
        {
            ManagedConnectionMode.Direct => ConnectionMode.Direct,
            ManagedConnectionMode.FieldLink => ConnectionMode.FieldLink,
            ManagedConnectionMode.Mavlink => ConnectionMode.Mavlink,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode))
        };
        var target = request.Target.Trim();
        ValidateTarget(mode, target, request.Mavlink);
        var mavlink = mode == ConnectionMode.Mavlink ? ToMavlinkOptions(request.Mavlink) : null;
        var linkd = mode == ConnectionMode.FieldLink ? ToLinkdOptions(request.Linkd) : null;
        var id = string.IsNullOrWhiteSpace(stableId)
            ? ConnectionId.Create(request.Name, target)
            : stableId.Trim();
        return new ConnectionDefinition(id, request.Name.Trim(), target, mode, request.AutoConnect,
            request.AutoReconnect, request.Description?.Trim(), Mavlink: mavlink, Linkd: linkd);
    }

    private static void ValidateTarget(ConnectionMode mode, string target, ManagedMavlinkOptions? mavlink)
    {
        if (mode == ConnectionMode.Mavlink)
        {
            var options = mavlink ?? new ManagedMavlinkOptions();
            if (options.Transport == ManagedMavlinkTransport.Serial)
            {
                _ = Mavlink.MavlinkConnectionProvider.ParseSerialPort(target);
                return;
            }
            _ = Mavlink.MavlinkConnectionProvider.ParseUdpListener(target);
            return;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The {mode} target must be an absolute HTTP or HTTPS URI.", nameof(target));
    }

    private static MavlinkConnectionOptions ToMavlinkOptions(ManagedMavlinkOptions? input)
    {
        var options = input ?? new ManagedMavlinkOptions();
        if (options.SourceSystemId == 0 || options.SourceComponentId == 0)
            throw new ArgumentException("MAVLink source system and component IDs must be between 1 and 255.");
        if (options.BaudRate is <= 0) throw new ArgumentException("MAVLink baud rate must be positive.");
        return new MavlinkConnectionOptions(
            options.Transport == ManagedMavlinkTransport.Serial ? MavlinkTransportKind.Serial : MavlinkTransportKind.UdpListener,
            options.Autopilot == ManagedMavlinkAutopilot.ArduPilot ? MavlinkAutopilotProfile.ArduPilot : MavlinkAutopilotProfile.Px4,
            options.SourceSystemId,
            options.SourceComponentId,
            (options.SystemAliases ?? new Dictionary<byte, string>()).Where(item => item.Key > 0 && !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => new MavlinkSystemAlias(item.Key, item.Value.Trim())).ToArray(),
            options.BaudRate,
            options.SerialDeviceId?.Trim(),
            options.LastKnownPort?.Trim());
    }

    private static LinkdConnectionOptions ToLinkdOptions(ManagedLinkdOptions? input)
    {
        var options = input ?? new ManagedLinkdOptions();
        if (options.BaudRate <= 0) throw new ArgumentException("LinkD baud rate must be positive.");
        return new LinkdConnectionOptions(options.TransportPlugin.Trim(), options.BaudRate,
            options.SerialDeviceId?.Trim(), options.LastKnownPort?.Trim(), options.RadioProfileKey?.Trim(), options.WireProfilePath?.Trim());
    }

    internal static ManagedConnectionSnapshot ToSnapshot(ConnectionDefinition definition, ConnectionRecord? record)
        => new(definition.Id, definition.Name, definition.Target, ToCoreMode(definition.Mode), ToCoreState(record?.State ?? AvailabilityState.Offline),
            definition.AutoConnect, definition.AutoReconnect, definition.Description, ToCoreMavlink(definition.Mavlink), ToCoreLinkd(definition.Linkd),
            record?.LogosInstanceId, record?.RuntimeRole, record?.ConnectedAt, record?.LastConnectedAt, record?.LastSeen, record?.LastAttempt, record?.LastError);

    private static ManagedConnectionMode ToCoreMode(ConnectionMode mode) => mode switch
    {
        ConnectionMode.FieldLink => ManagedConnectionMode.FieldLink,
        ConnectionMode.Mavlink => ManagedConnectionMode.Mavlink,
        _ => ManagedConnectionMode.Direct
    };
    internal static ManagedConnectionState ToCoreState(AvailabilityState state) => (ManagedConnectionState)(int)state;
    private static ManagedMavlinkOptions? ToCoreMavlink(MavlinkConnectionOptions? options) => options is null ? null : new(
        options.Transport == MavlinkTransportKind.Serial ? ManagedMavlinkTransport.Serial : ManagedMavlinkTransport.UdpListener,
        options.Autopilot == MavlinkAutopilotProfile.ArduPilot ? ManagedMavlinkAutopilot.ArduPilot : ManagedMavlinkAutopilot.Px4,
        options.SourceSystemId, options.SourceComponentId,
        options.Aliases.ToDictionary(item => item.SystemId, item => item.Name), options.BaudRate, options.SerialDeviceId, options.LastKnownPort);
    private static ManagedLinkdOptions? ToCoreLinkd(LinkdConnectionOptions? options) => options is null ? null : new(
        options.TransportPlugin, options.BaudRate, options.SerialDeviceId, options.LastKnownPort, options.RadioProfileKey, options.WireProfilePath);
}

public sealed class UnitObservationWorkflow : IUnitObservationWorkflow
{
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot> _diagnostics;
    private readonly IEntityStore<string, LinkRecord> _links;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly IUnitDefinitionService _reconciliation;
    private readonly IEntityStore<string, ConnectionRecord>? _connectionRecords;
    private readonly IOperatorLocationService? _operatorLocation;
    private readonly IGhostProfileWorkflow? _ghostProfiles;
    private readonly CoalescedChangeNotifier _changeNotifier;

    public UnitObservationWorkflow(
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, VehicleDiagnosticsSnapshot> diagnostics,
        IEntityStore<string, LinkRecord> links,
        IEntityStore<string, OperationalCommandRecord> commands,
        IUnitDefinitionService reconciliation,
        IEntityStore<string, ConnectionRecord>? connectionRecords = null,
        IOperatorLocationService? operatorLocation = null,
        IGhostProfileWorkflow? ghostProfiles = null,
        IUiDispatcher? dispatcher = null)
    {
        _vehicles = vehicles; _telemetry = telemetry; _diagnostics = diagnostics; _links = links; _commands = commands; _reconciliation = reconciliation;
        _connectionRecords = connectionRecords; _operatorLocation = operatorLocation;
        _ghostProfiles = ghostProfiles;
        _changeNotifier = new(TimeSpan.FromMilliseconds(5), dispatcher);
        Subscribe(vehicles.Items); Subscribe(telemetry.Items); Subscribe(diagnostics.Items); Subscribe(links.Items); Subscribe(commands.Items);
        if (_connectionRecords is not null) Subscribe(_connectionRecords.Items);
        _changeNotifier.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _reconciliation.Changed += (_, _) => _changeNotifier.Request();
        if (_operatorLocation is not null) _operatorLocation.Changed += (_, _) => _changeNotifier.Request();
    }

    public event EventHandler? Changed;

    // Entity stores are fed by transport/background services while a headless
    // CLI session can read them on its console thread. ObservableCollection is
    // deliberately used for Avalonia binding, but it is not safe to enumerate
    // while a transport update is applying. Project an immutable set of store
    // snapshots before reconciling so observation never crashes the CLI (or a
    // Team snapshot) during MAVLink discovery.
    public IReadOnlyList<UnitObservationSnapshot> Units
    {
        get
        {
            var vehicles = Snapshot(_vehicles.Items);
            var telemetry = Snapshot(_telemetry.Items);
            var diagnostics = Snapshot(_diagnostics.Items);
            var links = Snapshot(_links.Items);
            var commands = Snapshot(_commands.Items);
            var connectionRecords = _connectionRecords is null ? [] : Snapshot(_connectionRecords.Items);

            return _reconciliation.ProjectVehicles(vehicles)
                .Select(vehicle => Project(vehicle, telemetry, diagnostics, links, commands, connectionRecords))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
    public bool TryGet(string unitId, out UnitObservationSnapshot? unit)
    {
        unit = Units.FirstOrDefault(item => string.Equals(item.Id, unitId, StringComparison.Ordinal));
        return unit is not null;
    }

    private void Subscribe(System.Collections.ObjectModel.ReadOnlyObservableCollection<object> _) { }
    private void Subscribe<T>(System.Collections.ObjectModel.ReadOnlyObservableCollection<T> items)
        => ((INotifyCollectionChanged)items).CollectionChanged += (_, _) => _changeNotifier.Request();

    private UnitObservationSnapshot Project(
        VehicleRecord vehicle,
        IReadOnlyList<VehicleTelemetryRecord> telemetryRecords,
        IReadOnlyList<VehicleDiagnosticsSnapshot> diagnosticRecords,
        IReadOnlyList<LinkRecord> linkRecords,
        IReadOnlyList<OperationalCommandRecord> commandRecords,
        IReadOnlyList<ConnectionRecord> connectionRecords)
    {
        var definition = _reconciliation.FindBySource(vehicle.Id);
        var telemetrySource = definition is null ? vehicle.Id : _reconciliation.ResolveTelemetrySource(vehicle.Id);
        var diagnosticsSource = definition is null ? vehicle.Id : _reconciliation.ResolveDiagnosticsSource(vehicle.Id);
        var commandSource = definition is null ? vehicle.Id : _reconciliation.ResolveCommandSource(vehicle.Id);
        // A normal vehicle uses its backend vehicle ID as the routing source,
        // while a reconciled vehicle has explicit connection authorities. Keep
        // those two identities separate when projecting the public snapshot.
        var commandAuthorityConnectionId = definition?.CommandAuthorityConnectionId ?? vehicle.ConnectionIds.FirstOrDefault();
        var telemetryAuthorityConnectionId = definition?.TelemetryAuthorityConnectionId ?? vehicle.ConnectionIds.FirstOrDefault();
        var diagnosticsAuthorityConnectionId = definition?.DiagnosticsAuthorityConnectionId ?? vehicle.ConnectionIds.FirstOrDefault();
        var telemetry = telemetryRecords.Where(item => item.VehicleId == telemetrySource)
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        var diagnostic = diagnosticRecords.Where(item => item.VehicleId == diagnosticsSource)
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        var links = linkRecords.Where(item => vehicle.ConnectionIds.Contains(item.ConnectionId, StringComparer.Ordinal))
            .Select(item => new UnitLinkObservation(item.ConnectionId, item.Name, item.State, item.Connected, item.IsStale, item.RssiDbm, item.SnrDb, item.Quality, item.PacketLoss, item.LatencyMilliseconds, item.ObservedAt)).ToArray();
        var associatedConnections = vehicle.ConnectionIds
            .Select(connectionId =>
            {
                var connection = connectionRecords.FirstOrDefault(item => item.Id == connectionId);
                return new UnitConnectionObservation(
                    connectionId,
                    connection?.Name ?? connectionId,
                    connection?.Mode.ToString() ?? "Unknown",
                    connection?.Target ?? string.Empty,
                    connection is null ? ManagedConnectionState.Unknown : ConnectionManagementWorkflow.ToCoreState(connection.State),
                    string.Equals(commandAuthorityConnectionId, connectionId, StringComparison.Ordinal),
                    string.Equals(telemetryAuthorityConnectionId, connectionId, StringComparison.Ordinal),
                    string.Equals(diagnosticsAuthorityConnectionId, connectionId, StringComparison.Ordinal),
                    connection?.LastSeen);
            })
            .ToArray();
        var action = commandRecords.Where(item => item.VehicleId == commandSource || item.TargetId == commandSource)
            .OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        var current = action is { State: OperationalCommandState.Accepted or OperationalCommandState.InProgress or OperationalCommandState.Submitting }
            ? action.Kind : null;
        var queued = action is { State: OperationalCommandState.Draft } ? action.Kind : null;
        var commandConnection = connectionRecords.FirstOrDefault(item => item.Id == commandAuthorityConnectionId);
        var canAcceptOperatorCommands = commandConnection is null || commandConnection.Mode != ConnectionMode.TeamObserver;
        var flightMode = vehicle.ProfileKey.StartsWith("mavlink:", StringComparison.OrdinalIgnoreCase)
            ? telemetry?.AirframeMode
            : telemetry?.AdapterState;
        var result = new UnitObservationSnapshot(vehicle.Id, vehicle.Name, vehicle.ConnectionIds, vehicle.LogosInstanceId, vehicle.TeamId,
            vehicle.VehicleClass, vehicle.Domain, vehicle.ProfileKey, ConnectionManagementWorkflow.ToCoreState(vehicle.State), vehicle.Readiness,
            vehicle.Lifecycle, vehicle.ArmState, vehicle.Health, vehicle.CapabilityKeys ?? [], vehicle.LastSeen, vehicle.IsGhost,
            definition is null ? vehicle.Id : commandSource, definition is null ? vehicle.Id : telemetrySource, definition is null ? vehicle.Id : diagnosticsSource,
            telemetry is null ? null : new UnitTelemetryObservation(ConnectionManagementWorkflow.ToCoreState(telemetry.State), telemetry.Armed,
                telemetry.LandedState, flightMode ?? telemetry.AdapterState, telemetry.LatitudeDegrees, telemetry.LongitudeDegrees, telemetry.AltitudeMslMetres,
                telemetry.AltitudeAglMetres, telemetry.VelocityNorthMetresPerSecond, telemetry.VelocityEastMetresPerSecond,
                telemetry.VelocityDownMetresPerSecond, telemetry.HeadingDegrees, telemetry.IsStale, telemetry.ObservedAt,
                diagnostic?.BatteryRemainingPercent, diagnostic?.BatteryVoltageVolts, diagnostic?.ObservedAt),
            diagnostic is null ? null : new UnitDiagnosticsObservation(diagnostic.OverallStatus.ToString(), diagnostic.Summary,
                diagnostic.ArmReadiness.ToString(), diagnostic.ArmReadinessDetail, diagnostic.NavigationReadiness.ToString(), diagnostic.NavigationReadinessDetail,
                diagnostic.TelemetryStatus.ToString(), diagnostic.TelemetryDetail, diagnostic.Blockers.Select(item => item.Detail).ToArray(),
                diagnostic.Warnings.Select(item => item.Detail).ToArray(), diagnostic.ObservedAt), links, new UnitActionObservation(current, queued),
            associatedConnections, DistanceFromOperator(telemetry), canAcceptOperatorCommands);
        if (vehicle.IsGhost)
        {
            var profile = _ghostProfiles?.Find(vehicle.ProfileKey) ?? GhostProfileDefaults.Dracula;
            result = result with
            {
                GhostProfileId = profile.Id,
                GhostProfileName = profile.Name,
                GhostProfileModel = null,
                GhostSimulationStats = profile.Simulation
            };
        }
        return result;
    }

    private double? DistanceFromOperator(VehicleTelemetryRecord? telemetry)
    {
        if (_operatorLocation?.Snapshot is not { IsAvailable: true, LatitudeDegrees: { } operatorLatitude, LongitudeDegrees: { } operatorLongitude } ||
            telemetry?.LatitudeDegrees is not { } unitLatitude || telemetry.LongitudeDegrees is not { } unitLongitude ||
            telemetry.IsStale ||
            !double.IsFinite(operatorLatitude) || !double.IsFinite(operatorLongitude) ||
            !double.IsFinite(unitLatitude) || !double.IsFinite(unitLongitude) ||
            operatorLatitude is < -90 or > 90 || unitLatitude is < -90 or > 90 ||
            operatorLongitude is < -180 or > 180 || unitLongitude is < -180 or > 180)
            return null;

        return HaversineMetres(operatorLatitude, operatorLongitude, unitLatitude, unitLongitude);
    }

    private static double HaversineMetres(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusMetres = 6_371_008.8;
        var dLatitude = (latitude2 - latitude1) * Math.PI / 180d;
        var dLongitude = (longitude2 - longitude1) * Math.PI / 180d;
        var a = Math.Sin(dLatitude / 2d) * Math.Sin(dLatitude / 2d) +
                Math.Cos(latitude1 * Math.PI / 180d) * Math.Cos(latitude2 * Math.PI / 180d) *
                Math.Sin(dLongitude / 2d) * Math.Sin(dLongitude / 2d);
        return earthRadiusMetres * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0d, 1d - a)));
    }

    private static T[] Snapshot<T>(System.Collections.ObjectModel.ReadOnlyObservableCollection<T> items)
    {
        // Collection change notifications can occur between MoveNext calls.
        // Retrying produces a coherent newest snapshot without making the UI
        // store itself a cross-thread synchronization primitive.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return items.ToArray(); }
            catch (InvalidOperationException) when (attempt < 2) { Thread.Yield(); }
        }

        // A last defensive copy keeps a rare concurrent update from escaping
        // a read-only workflow boundary as an application failure.
        return items.Take(items.Count).ToArray();
    }
}

public sealed class ConnectionRuntimeLifecycle : IConnectionRuntimeLifecycle
{
    private readonly ILogosConnectionManager _manager;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    public ConnectionRuntimeLifecycle(ILogosConnectionManager manager, AppConfiguration configuration)
    {
        _manager = manager;
        _interval = TimeSpan.FromSeconds(Math.Max(1, configuration.RefreshSeconds));
    }

    public async Task StartAsync(bool autoConnect, string? connectionId = null, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        if (autoConnect) await _manager.StartAutoConnectionsAsync(cancellationToken);
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = SuperviseAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping is null) return;
        stopping.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
        finally { stopping.Dispose(); _loop = null; }
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await _manager.SuperviseAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
