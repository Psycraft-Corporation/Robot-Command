using Microsoft.Extensions.Logging;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Simulation;
using RobotCommand.State;

namespace RobotCommand.Services.Connections;

public sealed class LogosConnectionManager : ILogosConnectionManager, IDisposable
{
    private readonly object _gate = new();
    // Connection events arrive on transport threads.  The desktop dispatcher
    // serializes their store projection, but the headless dispatcher executes
    // inline, so publication must also be serialized here.
    private readonly object _publishGate = new();
    private readonly Dictionary<string, ILogosConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionCredentials> _credentials = new(StringComparer.Ordinal);
    private readonly ILogosConnectionFactory _connectionFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IEntityStore<string, ConnectionRecord> _connectionStore;
    private readonly IEntityStore<string, RuntimeRecord> _runtimeStore;
    private readonly IEntityStore<string, TeamRecord> _teamStore;
    private readonly IEntityStore<string, VehicleRecord> _vehicleStore;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetryStore;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot> _diagnosticsStore;
    private readonly IEntityStore<string, LinkRecord> _linkStore;
    private readonly IEntityStore<string, ConsoleEventRecord> _eventStore;
    private readonly IEntityStore<string, LiveStreamRecord> _streamStore;
    private readonly IEntityStore<string, GeometryOverlayRecord> _geometryStore;
    private readonly IEntityStore<string, PerceptionTrackRecord> _trackStore;
    private readonly IEntityStore<string, CameraSourceRecord> _cameraSourceStore;
    private readonly IEntityStore<string, CameraStreamRecord> _cameraStreamStore;
    private readonly AppConfiguration _configuration;
    private readonly ILogger<LogosConnectionManager> _logger;
    private readonly IGhostUnitService? _ghosts;
    private readonly IStorePublicationGate _storePublicationGate;
    // Stores are shared by all connection providers (Logos, Ghost, and future
    // vehicle backends). Keep track of the records this manager owned on its
    // previous publication so a refresh cannot erase records published by a
    // different provider.
    private readonly HashSet<string> _publishedConnectionIds = new(StringComparer.Ordinal);
    private int _publishRequested;
    private int _publishWorkerRunning;

    public LogosConnectionManager(
        AppConfiguration configuration,
        ILogosConnectionFactory connectionFactory,
        IUiDispatcher uiDispatcher,
        IEntityStore<string, ConnectionRecord> connectionStore,
        IEntityStore<string, RuntimeRecord> runtimeStore,
        IEntityStore<string, TeamRecord> teamStore,
        IEntityStore<string, VehicleRecord> vehicleStore,
        IEntityStore<string, VehicleTelemetryRecord> telemetryStore,
        IEntityStore<string, LinkRecord> linkStore,
        IEntityStore<string, ConsoleEventRecord> eventStore,
        IEntityStore<string, LiveStreamRecord> streamStore,
        IEntityStore<string, GeometryOverlayRecord> geometryStore,
        IEntityStore<string, PerceptionTrackRecord> trackStore,
        IEntityStore<string, CameraSourceRecord> cameraSourceStore,
        IEntityStore<string, CameraStreamRecord> cameraStreamStore,
        ILogger<LogosConnectionManager> logger,
        IGhostUnitService? ghosts = null,
        IStorePublicationGate? storePublicationGate = null)
        : this(configuration, connectionFactory, uiDispatcher, connectionStore, runtimeStore, teamStore,
            vehicleStore, telemetryStore,
            new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal),
            linkStore, eventStore, streamStore, geometryStore, trackStore, cameraSourceStore, cameraStreamStore, logger, ghosts, storePublicationGate)
    {
    }

    public LogosConnectionManager(
        AppConfiguration configuration,
        ILogosConnectionFactory connectionFactory,
        IUiDispatcher uiDispatcher,
        IEntityStore<string, ConnectionRecord> connectionStore,
        IEntityStore<string, RuntimeRecord> runtimeStore,
        IEntityStore<string, TeamRecord> teamStore,
        IEntityStore<string, VehicleRecord> vehicleStore,
        IEntityStore<string, VehicleTelemetryRecord> telemetryStore,
        IEntityStore<string, VehicleDiagnosticsSnapshot> diagnosticsStore,
        IEntityStore<string, LinkRecord> linkStore,
        IEntityStore<string, ConsoleEventRecord> eventStore,
        IEntityStore<string, LiveStreamRecord> streamStore,
        IEntityStore<string, GeometryOverlayRecord> geometryStore,
        IEntityStore<string, PerceptionTrackRecord> trackStore,
        IEntityStore<string, CameraSourceRecord> cameraSourceStore,
        IEntityStore<string, CameraStreamRecord> cameraStreamStore,
        ILogger<LogosConnectionManager> logger,
        IGhostUnitService? ghosts = null,
        IStorePublicationGate? storePublicationGate = null)
    {
        _configuration = configuration;
        _connectionFactory = connectionFactory;
        _uiDispatcher = uiDispatcher;
        _connectionStore = connectionStore;
        _runtimeStore = runtimeStore;
        _teamStore = teamStore;
        _vehicleStore = vehicleStore;
        _telemetryStore = telemetryStore;
        _diagnosticsStore = diagnosticsStore;
        _linkStore = linkStore;
        _eventStore = eventStore;
        _streamStore = streamStore;
        _geometryStore = geometryStore;
        _trackStore = trackStore;
        _cameraSourceStore = cameraSourceStore;
        _cameraStreamStore = cameraStreamStore;
        _logger = logger;
        _ghosts = ghosts;
        _storePublicationGate = storePublicationGate ?? new StorePublicationGate();

        foreach (var profile in configuration.Connections)
        {
            try
            {
                RegisterCore(profile.ToDefinition());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Ignoring invalid configured Logos connection {ConnectionName}",
                    profile.Name);
            }
        }

        PublishAllCore();
    }

    public IReadOnlyList<ConnectionDefinition> Definitions
    {
        get
        {
            lock (_gate)
            {
                return _connections.Values
                    .Select(item => item.Definition)
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public bool TryGetDefinition(string connectionId, out ConnectionDefinition? definition)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
            {
                definition = connection.Definition;
                return true;
            }
        }

        definition = null;
        return false;
    }

    public async Task RegisterAsync(ConnectionDefinition definition, CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);
        lock (_gate)
        {
            RegisterCore(definition);
        }

        await PublishAllAsync(cancellationToken);
        _logger.LogInformation("Registered Logos connection {ConnectionId}", definition.Id);
    }

    public async Task RemoveAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ILogosConnection? connection;
        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out connection))
            {
                return;
            }

            _credentials.Remove(connectionId);
            connection.Changed -= OnConnectionChanged;
        }

        try
        {
            await connection.DisconnectAsync(cancellationToken);
        }
        finally
        {
            await connection.DisposeAsync();
            await PublishAllAsync(cancellationToken);
        }

        _logger.LogInformation("Removed Logos connection {ConnectionId}", connectionId);
    }

    public async Task UpdateAsync(ConnectionDefinition definition, CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);
        ILogosConnection? existing;
        lock (_gate)
        {
            if (!_connections.TryGetValue(definition.Id, out existing))
            {
                throw new KeyNotFoundException($"Unknown Logos connection '{definition.Id}'.");
            }

            existing.Changed -= OnConnectionChanged;
            _connections.Remove(definition.Id);
        }

        await existing.DisconnectAsync(cancellationToken);
        await existing.DisposeAsync();

        lock (_gate)
        {
            RegisterCore(definition);
        }

        await PublishAllAsync(cancellationToken);
        _logger.LogInformation("Updated Logos connection {ConnectionId}", definition.Id);
    }

    public async Task ConnectAsync(
        string connectionId,
        ConnectionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var connection = GetRequiredConnection(connectionId);
        var normalizedCredentials = credentials.Normalize();
        lock (_gate)
        {
            _credentials[connectionId] = normalizedCredentials;
        }

        await PublishAllAsync(cancellationToken);
        try
        {
            await connection.ConnectAsync(normalizedCredentials, reconnecting: false, cancellationToken);
        }
        finally
        {
            await PublishAllAsync(cancellationToken);
        }
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connection = GetRequiredConnection(connectionId);
        await connection.DisconnectAsync(cancellationToken);
        await PublishAllAsync(cancellationToken);
    }

    public async Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connection = GetRequiredConnection(connectionId);
        try
        {
            await connection.RefreshAsync(cancellationToken);
        }
        finally
        {
            await PublishAllAsync(cancellationToken);
        }
    }

    public async Task<CameraStreamRecord> OpenCameraStreamAsync(
        string connectionId,
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = GetRequiredConnection(connectionId);
        try
        {
            return await connection.OpenCameraStreamAsync(request, cancellationToken);
        }
        finally
        {
            await PublishAllAsync(cancellationToken);
        }
    }

    public async Task CloseCameraStreamAsync(
        string connectionId,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        var connection = GetRequiredConnection(connectionId);
        try
        {
            await connection.CloseCameraStreamAsync(streamId, cancellationToken);
        }
        finally
        {
            await PublishAllAsync(cancellationToken);
        }
    }

    public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        string connectionId,
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = GetRequiredConnection(connectionId);
        return connection.EvaluateOperatorPolicyAsync(request, cancellationToken);
    }

    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        var connections = SnapshotConnections();
        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credentials = GetCredentials(connection.Definition.Id);
            try
            {
                await connection.ConnectAsync(credentials, reconnecting: false, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Connect-all failed for {ConnectionId}",
                    connection.Definition.Id);
            }
            finally
            {
                await PublishAllAsync(cancellationToken);
            }
        }
    }

    public async Task StartAutoConnectionsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var connection in SnapshotConnections().Where(item => item.Definition.AutoConnect))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await connection.ConnectAsync(
                    GetCredentials(connection.Definition.Id),
                    reconnecting: false,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Automatic connection failed for {ConnectionId}",
                    connection.Definition.Id);
            }
            finally
            {
                await PublishAllAsync(cancellationToken);
            }
        }
    }

    public async Task SuperviseAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var refreshAfter = TimeSpan.FromSeconds(_configuration.RefreshSeconds);
        var staleAfter = TimeSpan.FromSeconds(_configuration.StaleAfterSeconds);
        var offlineAfter = TimeSpan.FromSeconds(_configuration.OfflineAfterSeconds);

        foreach (var connection in SnapshotConnections())
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection.EvaluateFreshness(now, staleAfter, offlineAfter);

            if (connection.IsTransportOpen)
            {
                var snapshotInterval = connection.HasActiveStreams
                    ? TimeSpan.FromSeconds(_configuration.PollFallbackSeconds)
                    : refreshAfter;
                var lastSnapshot = connection.LastSnapshotRefresh ?? DateTimeOffset.MinValue;
                if (now - lastSnapshot < snapshotInterval)
                {
                    continue;
                }

                try
                {
                    await connection.RefreshAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Supervised fallback refresh failed for {ConnectionId}",
                        connection.Definition.Id);
                }

                continue;
            }

            var lastAttempt = connection.LastAttempt ?? DateTimeOffset.MinValue;
            if (now - lastAttempt < refreshAfter)
            {
                continue;
            }

            if (!connection.Definition.AutoReconnect || !connection.HasConnectBeenRequested)
            {
                continue;
            }

            if (connection.NextReconnectAt is { } nextReconnectAt && now < nextReconnectAt)
            {
                continue;
            }

            try
            {
                await connection.ConnectAsync(
                    GetCredentials(connection.Definition.Id),
                    reconnecting: true,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Supervised reconnect failed for {ConnectionId}",
                    connection.Definition.Id);
            }
        }

        await PublishAllAsync(cancellationToken);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        var connections = SnapshotConnections();
        lock (_gate)
        {
            _connections.Clear();
            _credentials.Clear();
        }

        foreach (var connection in connections)
        {
            connection.Changed -= OnConnectionChanged;
            await connection.DisposeAsync();
        }
    }

    private void RegisterCore(ConnectionDefinition definition)
    {
        ValidateDefinition(definition);
        if (_connections.ContainsKey(definition.Id))
        {
            throw new InvalidOperationException($"A connection with ID '{definition.Id}' already exists.");
        }

        var connection = _connectionFactory.Create(definition);
        connection.Changed += OnConnectionChanged;
        _connections.Add(definition.Id, connection);
        _credentials[definition.Id] = ConnectionCredentials.Empty;
    }

    private ILogosConnection GetRequiredConnection(string connectionId)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
            {
                return connection;
            }
        }

        throw new KeyNotFoundException($"Unknown Logos connection '{connectionId}'.");
    }

    private ConnectionCredentials GetCredentials(string connectionId)
    {
        lock (_gate)
        {
            return _credentials.TryGetValue(connectionId, out var credentials)
                ? credentials
                : ConnectionCredentials.Empty;
        }
    }

    private ILogosConnection[] SnapshotConnections()
    {
        lock (_gate)
        {
            return _connections.Values.ToArray();
        }
    }

    private Task PublishAllAsync(CancellationToken cancellationToken)
        => PublishAllAsyncCore(cancellationToken);

    private async Task PublishAllAsyncCore(CancellationToken cancellationToken)
    {
        using var storeLease = await _storePublicationGate.EnterAsync(cancellationToken);
        await _uiDispatcher.InvokeAsync(PublishAllCore, cancellationToken);
    }

    private void PublishAllCore()
    {
        lock (_publishGate)
        {
            PublishAllCoreLocked();
        }
    }

    private void PublishAllCoreLocked()
    {
        // Ghost records are owned by GhostUnitService, not by the Logos
        // connection manager. Preserve only records that still have an active
        // Ghost owner; otherwise a queued Logos refresh can resurrect a Ghost
        // after it has been deleted from the map and simulation.
        var ghostConnections = _connectionStore.Items.Where(IsActiveGhostConnection).ToArray();
        var ghostRuntimes = _runtimeStore.Items.Where(item => item.IsGhost && item.VehicleId is not null && IsActiveGhost(item.VehicleId)).ToArray();
        var ghostVehicles = _vehicleStore.Items.Where(item => item.IsGhost && IsActiveGhost(item.Id)).ToArray();
        var ghostTelemetry = _telemetryStore.Items.Where(item => item.IsGhost && IsActiveGhost(item.VehicleId)).ToArray();
        var ghostDiagnostics = _diagnosticsStore.Items.Where(item => string.Equals(item.Backend, "Ghost", StringComparison.OrdinalIgnoreCase) && IsActiveGhost(item.VehicleId)).ToArray();
        var connections = SnapshotConnections();
        var connectionIds = connections
            .Select(item => item.Definition.Id)
            .ToHashSet(StringComparer.Ordinal);
        var externalLinks = _linkStore.Items
            .Where(item => !_publishedConnectionIds.Contains(item.ConnectionId) && !connectionIds.Contains(item.ConnectionId))
            .ToArray();
        var externalCameraSources = _cameraSourceStore.Items
            .Where(item => !_publishedConnectionIds.Contains(item.ConnectionId) && !connectionIds.Contains(item.ConnectionId))
            .ToArray();
        var externalCameraStreams = _cameraStreamStore.Items
            .Where(item => !_publishedConnectionIds.Contains(item.ConnectionId) && !connectionIds.Contains(item.ConnectionId))
            .ToArray();
        _connectionStore.ReplaceAll(ghostConnections.Concat(connections
            .Select(ToConnectionRecord)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)));

        var observations = connections
            .SelectMany(connection => connection.Observations
                .Select(observation => new ConnectionObservation(connection, observation)))
            .ToArray();

        _runtimeStore.ReplaceAll(ghostRuntimes.Concat(BuildRuntimeRecords(observations)));
        _teamStore.ReplaceAll(BuildTeamRecords(observations));
        _vehicleStore.ReplaceAll(ghostVehicles.Concat(BuildVehicleRecords(observations)));

        _telemetryStore.ReplaceAll(ghostTelemetry.Concat(connections
            .SelectMany(item => item.LiveSnapshot.Telemetry)
            .Select(item => ApplyConnectionState(item, connections))
            .OrderBy(item => item.VehicleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ConnectionId, StringComparer.Ordinal)));
        _diagnosticsStore.ReplaceAll(ghostDiagnostics.Concat(connections
            .SelectMany(item => item.LiveSnapshot.VehicleDiagnostics)
            .OrderBy(item => item.VehicleId, StringComparer.Ordinal)));
        _linkStore.ReplaceAll(externalLinks.Concat(connections
            .SelectMany(item => item.LiveSnapshot.Links)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)));
        _eventStore.ReplaceAll(connections
            .SelectMany(item => item.LiveSnapshot.Events)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Timestamp).First())
            .OrderByDescending(item => item.Timestamp)
            .Take(_configuration.MaxEvents));
        _streamStore.ReplaceAll(connections
            .SelectMany(item => item.LiveSnapshot.Streams)
            .OrderBy(item => item.ConnectionId, StringComparer.Ordinal)
            .ThenBy(item => item.Kind));
        _geometryStore.ReplaceAll(connections
            .SelectMany(item => item.LiveSnapshot.Geometries)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        _trackStore.ReplaceAll(connections
            .SelectMany(item => item.LiveSnapshot.Tracks)
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.TrackId, StringComparer.Ordinal));
        _cameraSourceStore.ReplaceAll(externalCameraSources.Concat(connections
            .SelectMany(item => item.LiveSnapshot.CameraSources)
            .Select(item => ApplyConnectionState(item, connections))
            .OrderByDescending(item => item.Active)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)));
        _cameraStreamStore.ReplaceAll(externalCameraStreams.Concat(connections
            .SelectMany(item => item.LiveSnapshot.CameraStreams)
            .OrderByDescending(item => item.ObservedAt)));

        _publishedConnectionIds.Clear();
        _publishedConnectionIds.UnionWith(connectionIds);
    }

    private bool IsActiveGhost(string vehicleId)
        => _ghosts?.IsGhostVehicle(vehicleId) == true;

    private bool IsActiveGhostConnection(ConnectionRecord connection)
    {
        if (!connection.IsGhost) return false;
        return IsActiveGhostConnectionId(connection.Id);
    }

    private bool IsActiveGhostConnectionId(string connectionId)
    {
        const string prefix = "ghost-connection-";
        return connectionId.StartsWith(prefix, StringComparison.Ordinal) &&
               IsActiveGhost("ghost-" + connectionId[prefix.Length..]);
    }


    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _publishRequested, 1);
        if (Interlocked.CompareExchange(ref _publishWorkerRunning, 1, 0) == 0)
        {
            _ = DrainConnectionChangesAsync();
        }
    }

    private async Task DrainConnectionChangesAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _publishRequested, 0) == 1)
            {
                await Task.Delay(75);
                await PublishAllAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish live Logos connection updates");
        }
        finally
        {
            Interlocked.Exchange(ref _publishWorkerRunning, 0);
            if (Volatile.Read(ref _publishRequested) == 1 &&
                Interlocked.CompareExchange(ref _publishWorkerRunning, 1, 0) == 0)
            {
                _ = DrainConnectionChangesAsync();
            }
        }
    }

    private static CameraSourceRecord ApplyConnectionState(
        CameraSourceRecord camera,
        IReadOnlyList<ILogosConnection> connections)
    {
        var connection = connections.FirstOrDefault(item => item.Definition.Id == camera.ConnectionId);
        if (connection is null || connection.State is AvailabilityState.Offline or AvailabilityState.Faulted)
        {
            return camera with { State = AvailabilityState.Offline, Fresh = false };
        }

        if (connection.State == AvailabilityState.Stale)
        {
            return camera with { State = AvailabilityState.Stale, Fresh = false };
        }

        if (connection.State == AvailabilityState.Degraded && camera.State == AvailabilityState.Online)
        {
            return camera with { State = AvailabilityState.Degraded };
        }

        return camera;
    }

    private static VehicleTelemetryRecord ApplyConnectionState(
        VehicleTelemetryRecord telemetry,
        IReadOnlyList<ILogosConnection> connections)
    {
        var connection = connections.FirstOrDefault(item => item.Definition.Id == telemetry.ConnectionId);
        if (connection is null || connection.State is AvailabilityState.Offline or AvailabilityState.Faulted)
        {
            return telemetry with { State = AvailabilityState.Offline, IsStale = true };
        }

        if (connection.State == AvailabilityState.Stale)
        {
            return telemetry with { State = AvailabilityState.Stale, IsStale = true };
        }

        if (connection.State == AvailabilityState.Degraded && telemetry.State == AvailabilityState.Online)
        {
            return telemetry with { State = AvailabilityState.Degraded };
        }

        return telemetry;
    }

    private static ConnectionRecord ToConnectionRecord(ILogosConnection connection)
    {
        var observation = connection.LastObservation;
        return new ConnectionRecord(
            connection.Definition.Id,
            connection.Definition.Name,
            connection.Definition.Target,
            connection.Definition.Mode,
            connection.State,
            connection.Definition.AutoReconnect,
            observation?.Runtime.LogosInstanceId,
            observation?.Runtime.Role,
            connection.ConnectedAt,
            connection.LastConnectedAt,
            connection.LastSeen,
            connection.LastAttempt,
            connection.LastError);
    }

    private static IReadOnlyList<RuntimeRecord> BuildRuntimeRecords(IReadOnlyList<ConnectionObservation> observations)
        => observations
            .GroupBy(item => item.Observation.Runtime.LogosInstanceId, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = SelectBest(group);
                var runtime = selected.Observation.Runtime;
                return new RuntimeRecord(
                    runtime.LogosInstanceId,
                    runtime.DisplayName,
                    group.Select(item => item.Connection.Definition.Id)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray(),
                    BestState(group.Select(item => item.Connection.State)),
                    runtime.Role,
                    runtime.RuntimeMode,
                    runtime.PlatformKind,
                    runtime.PlatformProfile,
                    runtime.LogosVersion,
                    runtime.Health,
                    runtime.Readiness,
                    group.SelectMany(item => item.Observation.Runtime.CapabilityKeys)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray(),
                    group.Max(item => item.Connection.LastSeen),
                    group.Select(item => item.Observation.Vehicle?.VehicleId)
                        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
                    group.Select(item => item.Observation.Vehicle?.DisplayName)
                        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)));
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<TeamRecord> BuildTeamRecords(IReadOnlyList<ConnectionObservation> observations)
        => observations
            .Where(item => item.Observation.Team is not null)
            .GroupBy(item => item.Observation.Team!.TeamId, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = SelectBest(group);
                var team = selected.Observation.Team!;
                var memberCount = group
                    .Select(item => item.Observation.Runtime.LogosInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                return new TeamRecord(
                    team.TeamId,
                    team.DisplayName,
                    group.Select(item => item.Connection.Definition.Id)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray(),
                    BestState(group.Select(item => item.Connection.State)),
                    memberCount,
                    IsPartial: true,
                    group.Select(item => item.Observation.Team?.ManagerLogosInstanceId)
                        .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
                    group.Max(item => item.Connection.LastSeen));
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<VehicleRecord> BuildVehicleRecords(IReadOnlyList<ConnectionObservation> observations)
        => observations
            .Where(item => item.Observation.Vehicle is not null)
            .GroupBy(item => item.Observation.Vehicle!.VehicleId, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = SelectBest(group);
                var vehicle = selected.Observation.Vehicle!;
                var state = BestState(group.Select(item => MapVehicleState(item.Connection.State, item.Observation.Vehicle!.Health)));

                return new VehicleRecord(
                    vehicle.VehicleId,
                    vehicle.DisplayName,
                    group.Select(item => item.Connection.Definition.Id)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray(),
                    vehicle.LogosInstanceId,
                    vehicle.TeamId,
                    vehicle.VehicleClass,
                    vehicle.Domain,
                    vehicle.ProfileKey,
                    state,
                    vehicle.Readiness,
                    vehicle.Lifecycle,
                    vehicle.ArmState,
                    vehicle.Health,
                    group.SelectMany(item => item.Observation.Vehicle?.CapabilityKeys ?? [])
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(item => item, StringComparer.Ordinal)
                        .ToArray(),
                    group.Max(item => item.Connection.LastSeen));
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static ConnectionObservation SelectBest(IEnumerable<ConnectionObservation> candidates)
        => candidates
            .OrderByDescending(item => StateRank(item.Connection.State))
            .ThenByDescending(item => item.Observation.ObservedAt)
            .First();

    private static AvailabilityState BestState(IEnumerable<AvailabilityState> states)
        => states.OrderByDescending(StateRank).FirstOrDefault();

    private static AvailabilityState MapVehicleState(AvailabilityState connectionState, string health)
    {
        if (connectionState is AvailabilityState.Online &&
            (health.Contains("Degraded", StringComparison.OrdinalIgnoreCase) ||
             health.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) ||
             health.Contains("Failed", StringComparison.OrdinalIgnoreCase)))
        {
            return AvailabilityState.Degraded;
        }

        return connectionState;
    }

    private static int StateRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 90,
            AvailabilityState.Degraded => 80,
            AvailabilityState.Stale => 70,
            AvailabilityState.Connecting => 60,
            AvailabilityState.Reconnecting => 50,
            AvailabilityState.Unknown => 40,
            AvailabilityState.Offline => 20,
            AvailabilityState.Faulted => 10,
            _ => 0
        };

    private static void ValidateDefinition(ConnectionDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new ArgumentException("Connection ID is required.", nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("Connection name is required.", nameof(definition));
        }

        if (!Uri.TryCreate(definition.Target, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                $"Connection target must be an absolute URL. Got '{definition.Target}'.",
                nameof(definition));
        }
    }

    private sealed record ConnectionObservation(
        ILogosConnection Connection,
        LogosConnectionObservation Observation);
}
