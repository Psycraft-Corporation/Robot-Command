using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Connections;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class LogosConnectionManagerTests
{
    [Fact]
    public async Task ConnectAll_PopulatesRuntimeTeamVehicleAndLiveStores()
    {
        var configuration = new AppConfiguration
        {
            Connections =
            [
                new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one"),
                new ConnectionProfile("Unit Two", "http://unit-two:50051", Id: "two")
            ],
            RefreshSeconds = 2,
            StaleAfterSeconds = 10,
            OfflineAfterSeconds = 30
        };

        var stores = new TestStores();
        var factory = new FakeConnectionFactory(
            definition => CreateObservation(definition),
            definition => CreateLiveSnapshot(definition));

        await using var manager = CreateManager(configuration, factory, stores);
        await manager.ConnectAllAsync();

        Assert.Equal(2, stores.Connections.Items.Count);
        Assert.Equal(2, stores.Runtimes.Items.Count);
        var team = Assert.Single(stores.Teams.Items);
        Assert.Equal("team-alpha", team.Id);
        Assert.Equal(2, team.MemberCount);
        Assert.True(team.IsPartial);
        Assert.Equal(2, stores.Vehicles.Items.Count);
        Assert.All(stores.Vehicles.Items, item => Assert.Equal(AvailabilityState.Online, item.State));
        Assert.Equal(2, stores.Telemetry.Items.Count);
        Assert.Equal(2, stores.Links.Items.Count);
        Assert.Equal(2, stores.Events.Items.Count);
        Assert.Equal(14, stores.Streams.Items.Count);
        Assert.Equal(2, stores.Geometries.Items.Count);
        Assert.Equal(2, stores.Tracks.Items.Count);
        Assert.Equal(2, stores.CameraSources.Items.Count);
        Assert.Equal(2, stores.CameraStreams.Items.Count);
    }

    [Fact]
    public async Task Refresh_PreservesCameraSourcesPublishedByAnotherProvider()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one")]
        };
        var stores = new TestStores();
        stores.CameraSources.Upsert(new CameraSourceRecord(
            "sim-camera-1", "sim-camera-1", "sim-connection-1", null,
            "Simulator camera", "Simulated", AvailabilityState.Online, "Healthy", "Ready",
            true, true, true, 30, 0, 640, 360, "frame-1", "SIM", "", DateTimeOffset.UtcNow));

        await using var manager = CreateManager(
            configuration,
            new FakeConnectionFactory(CreateObservation, CreateLiveSnapshot),
            stores);
        await manager.ConnectAsync("one", ConnectionCredentials.Empty);

        Assert.Contains(stores.CameraSources.Items, item => item.ConnectionId == "sim-connection-1");
    }

    [Fact]
    public async Task Disconnect_LeavesDiscoveredVehicleAndTelemetryVisibleButOffline()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one")]
        };
        var stores = new TestStores();

        await using var manager = CreateManager(
            configuration,
            new FakeConnectionFactory(CreateObservation, CreateLiveSnapshot),
            stores);

        await manager.ConnectAsync("one", ConnectionCredentials.Empty);
        await manager.DisconnectAsync("one");

        Assert.Equal(AvailabilityState.Offline, Assert.Single(stores.Connections.Items).State);
        Assert.Equal(AvailabilityState.Offline, Assert.Single(stores.Vehicles.Items).State);
        var telemetry = Assert.Single(stores.Telemetry.Items);
        Assert.Equal(AvailabilityState.Offline, telemetry.State);
        Assert.True(telemetry.IsStale);
        Assert.All(stores.Streams.Items, item => Assert.Equal(LiveStreamState.Stopped, item.State));
    }

    [Fact]
    public async Task Supervise_ReconnectsActivatedConnectionAfterInitialFailure()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one")],
            RefreshSeconds = 1,
            StaleAfterSeconds = 10,
            OfflineAfterSeconds = 30
        };
        var stores = new TestStores();

        await using var manager = CreateManager(
            configuration,
            new FakeConnectionFactory(CreateObservation, CreateLiveSnapshot, failFirstConnect: true),
            stores);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ConnectAsync("one", ConnectionCredentials.Empty));

        Assert.Equal(AvailabilityState.Faulted, Assert.Single(stores.Connections.Items).State);

        await manager.SuperviseAsync();

        Assert.Equal(AvailabilityState.Online, Assert.Single(stores.Connections.Items).State);
        Assert.Single(stores.Runtimes.Items);
        Assert.Single(stores.Vehicles.Items);
        Assert.Single(stores.Telemetry.Items);
    }

    [Fact]
    public async Task StartAutoConnections_UsesAutoReconnectForSavedConnections()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one", AutoConnect: false, AutoReconnect: true)]
        };
        var stores = new TestStores();
        await using var manager = CreateManager(
            configuration,
            new FakeConnectionFactory(CreateObservation, CreateLiveSnapshot),
            stores);

        await manager.StartAutoConnectionsAsync();

        Assert.Equal(AvailabilityState.Online, Assert.Single(stores.Connections.Items).State);
    }

    [Fact]
    public async Task ExplicitDisconnect_SuppressesSupervisedReconnectUntilNextSession()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one", AutoConnect: false, AutoReconnect: true)],
            RefreshSeconds = 0
        };
        var stores = new TestStores();
        var factory = new FakeConnectionFactory(CreateObservation, CreateLiveSnapshot);
        await using var manager = CreateManager(configuration, factory, stores);

        await manager.ConnectAsync("one", ConnectionCredentials.Empty);
        await manager.DisconnectAsync("one");
        await manager.SuperviseAsync();

        Assert.Equal(AvailabilityState.Offline, Assert.Single(stores.Connections.Items).State);
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task ConnectionChange_PublishesNewLiveDataWithoutManualRefresh()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one")]
        };
        var stores = new TestStores();
        var factory = new FakeConnectionFactory(CreateObservation, _ => LogosConnectionLiveSnapshot.Empty);

        await using var manager = CreateManager(configuration, factory, stores);
        await manager.ConnectAsync("one", ConnectionCredentials.Empty);
        Assert.Empty(stores.Telemetry.Items);

        factory.Created.Single().PublishLive(CreateLiveSnapshot(factory.Created.Single().Definition));
        await WaitUntilAsync(() =>
            stores.Telemetry.Items.Count == 1 &&
            stores.Links.Items.Count == 1 &&
            stores.Events.Items.Count == 1 &&
            stores.Streams.Items.Count == 7);

        Assert.Single(stores.Links.Items);
        Assert.Single(stores.Events.Items);
        Assert.Equal(7, stores.Streams.Items.Count);
    }

    [Fact]
    public async Task CameraStreamCommands_AreDelegatedAndPublished()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one")]
        };
        var stores = new TestStores();
        var factory = new FakeConnectionFactory(CreateObservation, _ => LogosConnectionLiveSnapshot.Empty);

        await using var manager = CreateManager(configuration, factory, stores);
        await manager.ConnectAsync("one", ConnectionCredentials.Empty);

        var stream = await manager.OpenCameraStreamAsync(
            "one",
            new CameraStreamOpenRequest("camera-main", VideoProtocolPreference.Rtsp));
        await WaitUntilAsync(() => stores.CameraStreams.Items.Any(item => item.Id == stream.Id));

        Assert.Equal("Active", stores.CameraStreams.Items.Single(item => item.Id == stream.Id).State);

        await manager.CloseCameraStreamAsync("one", stream.StreamId);
        await WaitUntilAsync(() =>
            stores.CameraStreams.Items.Single(item => item.Id == stream.Id).State == "Closed");
    }

    [Fact]
    public async Task Supervise_UsesSlowFallbackRefreshWhenStreamsAreLive()
    {
        var configuration = new AppConfiguration
        {
            Connections = [new ConnectionProfile("Unit One", "http://unit-one:50051", Id: "one")],
            RefreshSeconds = 1,
            PollFallbackSeconds = 30
        };
        var stores = new TestStores();
        var factory = new FakeConnectionFactory(CreateObservation, CreateLiveSnapshot);

        await using var manager = CreateManager(configuration, factory, stores);
        await manager.ConnectAsync("one", ConnectionCredentials.Empty);
        var connection = factory.Created.Single();
        var refreshesBefore = connection.RefreshCount;

        await manager.SuperviseAsync();

        Assert.Equal(refreshesBefore, connection.RefreshCount);
    }

    private static LogosConnectionManager CreateManager(
        AppConfiguration configuration,
        ILogosConnectionFactory factory,
        TestStores stores)
        => new(
            configuration,
            factory,
            new ImmediateUiDispatcher(),
            stores.Connections,
            stores.Runtimes,
            stores.Teams,
            stores.Vehicles,
            stores.Telemetry,
            stores.Links,
            stores.Events,
            stores.Streams,
            stores.Geometries,
            stores.Tracks,
            stores.CameraSources,
            stores.CameraStreams,
            NullLogger<LogosConnectionManager>.Instance);

    private static LogosConnectionObservation CreateObservation(ConnectionDefinition definition)
    {
        var suffix = definition.Id == "one" ? "one" : "two";
        return new LogosConnectionObservation(
            new RuntimeObservation(
                $"logos-{suffix}",
                $"Runtime {suffix}",
                "Unit",
                "Field",
                "drone",
                suffix,
                "0.1.0",
                "Healthy",
                "Ready",
                "OK",
                string.Empty,
                ["system.health", "vehicle.state"]),
            new TeamObservation("team-alpha", "Team Alpha", null, $"vehicle-{suffix}"),
            new VehicleObservation(
                $"vehicle-{suffix}",
                $"Vehicle {suffix}",
                $"logos-{suffix}",
                "team-alpha",
                "Multicopter",
                "Air",
                "test-profile",
                "Ready",
                "Ready",
                "Disarmed",
                "Healthy",
                ["arm", "disarm"]),
            AvailabilityState.Online,
            DateTimeOffset.UtcNow);
    }

    private static LogosConnectionLiveSnapshot CreateLiveSnapshot(ConnectionDefinition definition)
    {
        var suffix = definition.Id == "one" ? "one" : "two";
        var now = DateTimeOffset.UtcNow;
        return new LogosConnectionLiveSnapshot(
            [new VehicleTelemetryRecord(
                $"{definition.Id}:vehicle-{suffix}",
                $"vehicle-{suffix}",
                definition.Id,
                $"logos-{suffix}",
                AvailabilityState.Online,
                false,
                "OnGround",
                "Multicopter",
                "ready",
                "Healthy",
                "Ready",
                43.65,
                -79.38,
                100,
                4,
                1,
                2,
                -4,
                0,
                0,
                0,
                90,
                false,
                "OK",
                string.Empty,
                now)],
            [new LinkRecord(
                $"{definition.Id}:radio",
                "radio",
                definition.Id,
                $"logos-{suffix}",
                "Field radio",
                "Radio",
                "Bidirectional",
                "Active",
                "Healthy",
                "Ready",
                true,
                false,
                "peer",
                "Logos",
                $"Peer {suffix}",
                -70,
                12,
                0.9,
                0.01,
                35,
                "OK",
                string.Empty,
                now)],
            [new ConsoleEventRecord(
                $"{definition.Id}:event-{suffix}",
                now,
                "Info",
                "vehicle_service",
                "Vehicle discovered",
                definition.Id,
                $"logos-{suffix}",
                "Vehicle",
                "StateChanged",
                "OK",
                $"vehicle-{suffix}")],
            Enum.GetValues<LiveStreamKind>()
                .Select(kind => new LiveStreamRecord(
                    $"{definition.Id}:{kind}",
                    definition.Id,
                    kind,
                    LiveStreamState.Live,
                    now,
                    now))
                .ToArray(),
            [new GeometryOverlayRecord(
                $"{definition.Id}:zone-{suffix}",
                $"zone-{suffix}",
                definition.Id,
                $"logos-{suffix}",
                $"Zone {suffix}",
                "Zone",
                MapFrameKind.GlobalWgs84,
                true,
                [],
                [[
                    new OperationalPoint(-79.39, 43.64),
                    new OperationalPoint(-79.37, 43.64),
                    new OperationalPoint(-79.37, 43.66)
                ]],
                "geofence",
                "inclusion",
                now)],
            [new PerceptionTrackRecord(
                $"{definition.Id}:track-{suffix}",
                $"track-{suffix}",
                definition.Id,
                $"logos-{suffix}",
                "vehicle",
                0.91,
                0.5,
                0.5,
                0.2,
                0.15,
                10,
                8,
                0,
                "camera-main",
                "camera_frame",
                now)],
            [new CameraSourceRecord(
                $"{definition.Id}:camera-main",
                "camera-main",
                definition.Id,
                $"logos-{suffix}",
                "Main camera",
                "Usb",
                AvailabilityState.Online,
                "Healthy",
                "Ready",
                true,
                true,
                true,
                30,
                40,
                1920,
                1080,
                "camera_frame",
                "OK",
                string.Empty,
                now)],
            [new CameraStreamRecord(
                $"{definition.Id}:stream-{suffix}",
                $"stream-{suffix}",
                "camera-main",
                definition.Id,
                $"logos-{suffix}",
                "Rtsp",
                "Active",
                $"rtsp://{definition.Id}/camera",
                string.Empty,
                "h264",
                1920,
                1080,
                30,
                4000,
                now,
                null,
                "OK",
                string.Empty,
                now)]);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(predicate(), "Expected live store update was not published.");
    }

    private sealed class TestStores
    {
        public EntityStore<string, ConnectionRecord> Connections { get; } = Store<ConnectionRecord>(item => item.Id);
        public EntityStore<string, RuntimeRecord> Runtimes { get; } = Store<RuntimeRecord>(item => item.Id);
        public EntityStore<string, TeamRecord> Teams { get; } = Store<TeamRecord>(item => item.Id);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = Store<VehicleRecord>(item => item.Id);
        public EntityStore<string, VehicleTelemetryRecord> Telemetry { get; } = Store<VehicleTelemetryRecord>(item => item.Id);
        public EntityStore<string, LinkRecord> Links { get; } = Store<LinkRecord>(item => item.Id);
        public EntityStore<string, ConsoleEventRecord> Events { get; } = Store<ConsoleEventRecord>(item => item.Id);
        public EntityStore<string, LiveStreamRecord> Streams { get; } = Store<LiveStreamRecord>(item => item.Id);
        public EntityStore<string, GeometryOverlayRecord> Geometries { get; } = Store<GeometryOverlayRecord>(item => item.Id);
        public EntityStore<string, PerceptionTrackRecord> Tracks { get; } = Store<PerceptionTrackRecord>(item => item.Id);
        public EntityStore<string, CameraSourceRecord> CameraSources { get; } = Store<CameraSourceRecord>(item => item.Id);
        public EntityStore<string, CameraStreamRecord> CameraStreams { get; } = Store<CameraStreamRecord>(item => item.Id);

        private static EntityStore<string, T> Store<T>(Func<T, string> keySelector)
            => new(keySelector, StringComparer.Ordinal);
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConnectionFactory : ILogosConnectionFactory
    {
        private readonly Func<ConnectionDefinition, LogosConnectionObservation> _observationFactory;
        private readonly Func<ConnectionDefinition, LogosConnectionLiveSnapshot> _liveFactory;
        private readonly bool _failFirstConnect;

        public FakeConnectionFactory(
            Func<ConnectionDefinition, LogosConnectionObservation> observationFactory,
            Func<ConnectionDefinition, LogosConnectionLiveSnapshot> liveFactory,
            bool failFirstConnect = false)
        {
            _observationFactory = observationFactory;
            _liveFactory = liveFactory;
            _failFirstConnect = failFirstConnect;
        }

        public List<FakeConnection> Created { get; } = [];

        public ILogosConnection Create(ConnectionDefinition definition)
        {
            var connection = new FakeConnection(
                definition,
                _observationFactory(definition),
                _liveFactory(definition),
                _failFirstConnect);
            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection : ILogosConnection
    {
        private readonly LogosConnectionObservation _observation;
        private readonly LogosConnectionLiveSnapshot _initialLiveSnapshot;
        private readonly bool _failFirstConnect;
        private int _connectAttempts;
        private string? _lastError;

        public FakeConnection(
            ConnectionDefinition definition,
            LogosConnectionObservation observation,
            LogosConnectionLiveSnapshot initialLiveSnapshot,
            bool failFirstConnect = false)
        {
            Definition = definition;
            _observation = observation;
            _initialLiveSnapshot = initialLiveSnapshot;
            _failFirstConnect = failFirstConnect;
        }

        public event EventHandler? Changed;

        public ConnectionDefinition Definition { get; }
        public AvailabilityState State { get; private set; } = AvailabilityState.Offline;
        public bool IsTransportOpen { get; private set; }
        public bool HasConnectBeenRequested { get; private set; }
        public bool HasActiveStreams => LiveSnapshot.HasLiveStreams;
        public DateTimeOffset? LastAttempt { get; private set; }
        public DateTimeOffset? ConnectedAt { get; private set; }
        public DateTimeOffset? LastConnectedAt { get; private set; }
        public DateTimeOffset? LastSeen { get; private set; }
        public DateTimeOffset? LastSnapshotRefresh { get; private set; }
        public string? LastError => _lastError;
        public LogosConnectionObservation? LastObservation { get; private set; }
        public LogosConnectionLiveSnapshot LiveSnapshot { get; private set; } = LogosConnectionLiveSnapshot.Empty;
        public int RefreshCount { get; private set; }

        public Task<LogosConnectionObservation> ConnectAsync(
            ConnectionCredentials credentials,
            bool reconnecting,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HasConnectBeenRequested = true;
            LastAttempt = DateTimeOffset.UtcNow.AddMinutes(-1);
            _connectAttempts++;

            if (_failFirstConnect && _connectAttempts == 1)
            {
                IsTransportOpen = false;
                State = AvailabilityState.Faulted;
                _lastError = "Simulated first connection failure.";
                Changed?.Invoke(this, EventArgs.Empty);
                throw new InvalidOperationException(_lastError);
            }

            IsTransportOpen = true;
            ConnectedAt = DateTimeOffset.UtcNow;
            LastConnectedAt = ConnectedAt;
            LastSeen = _observation.ObservedAt;
            LastSnapshotRefresh = DateTimeOffset.UtcNow;
            LastObservation = _observation;
            LiveSnapshot = _initialLiveSnapshot;
            State = _observation.Availability;
            _lastError = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(_observation);
        }

        public Task<LogosConnectionObservation> RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            LastSnapshotRefresh = DateTimeOffset.UtcNow;
            return Task.FromResult(_observation);
        }

        public Task<CameraStreamRecord> OpenCameraStreamAsync(
            CameraStreamOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var stream = new CameraStreamRecord(
                $"{Definition.Id}:opened-stream",
                "opened-stream",
                request.CameraSourceId,
                Definition.Id,
                LastObservation?.Runtime.LogosInstanceId,
                request.Protocol.ToString(),
                "Active",
                $"rtsp://{Definition.Id}/opened-stream",
                string.Empty,
                "h264",
                request.Width,
                request.Height,
                request.FrameRateHz,
                request.BitrateKbps,
                now,
                null,
                "OK",
                string.Empty,
                now);
            LiveSnapshot = LiveSnapshot with
            {
                CameraStreams = LiveSnapshot.CameraStreams
                    .Where(item => item.Id != stream.Id)
                    .Append(stream)
                    .ToArray()
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(stream);
        }

        public Task CloseCameraStreamAsync(
            string streamId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LiveSnapshot = LiveSnapshot with
            {
                CameraStreams = LiveSnapshot.CameraStreams
                    .Select(item => item.StreamId == streamId
                        ? item with { State = "Closed", ObservedAt = DateTimeOffset.UtcNow }
                        : item)
                    .ToArray()
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
            OperatorPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new OperatorPolicyEvaluation(
                true,
                true,
                "Allow",
                "Allowed by fake policy.",
                Array.Empty<OperatorPolicyFinding>()));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsTransportOpen = false;
            ConnectedAt = null;
            HasConnectBeenRequested = false;
            State = AvailabilityState.Offline;
            LiveSnapshot = LiveSnapshot with
            {
                Streams = LiveSnapshot.Streams
                    .Select(item => item with { State = LiveStreamState.Stopped })
                    .ToArray()
            };
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void PublishLive(LogosConnectionLiveSnapshot snapshot)
        {
            LiveSnapshot = snapshot;
            LastSeen = DateTimeOffset.UtcNow;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void EvaluateFreshness(DateTimeOffset now, TimeSpan staleAfter, TimeSpan offlineAfter)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
