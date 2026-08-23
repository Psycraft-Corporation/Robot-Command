using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Geometry;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryRegistrySupervisionServiceTests
{
    [Fact]
    public async Task Reconcile_LoadsSnapshotAppliesIncrementalUpdateAndClearsOnDisconnect()
    {
        var registry = new FakeSessionRegistry("connection-1");
        var gateway = new StreamingGateway
        {
            Listed = [RemoteRecord("route-1", "sha-1", "revision-1")]
        };
        var sink = new RecordingSink();
        using var service = new GeometryRegistrySupervisionService(
            registry,
            gateway,
            sink,
            NullLogger<GeometryRegistrySupervisionService>.Instance);

        await service.ReconcileOnceAsync();
        await WaitUntilAsync(() => sink.SnapshotCount == 1 && sink.LastStatus?.Status == GeometryRegistryWatchStatusKind.Live);

        gateway.Publish(new GeometryRegistryEvent(
            "connection-1",
            GeometryRegistryEventKind.Updated,
            Registry(1),
            RemoteRecord("route-1", "sha-2", "revision-2"),
            null,
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => sink.Events.Any(item => item.Record?.Revision == "revision-2"));

        registry.ConnectionIdsValue = [];
        await service.ReconcileOnceAsync();
        await WaitUntilAsync(() => sink.ClearCount == 1);

        Assert.Equal(GeometryRegistryWatchStatusKind.Stopped, sink.LastStatus!.Status);
        Assert.Equal("connection-1", sink.ClearedConnectionId);

        var eventCount = sink.Events.Count;
        gateway.Publish(new GeometryRegistryEvent(
            "connection-1",
            GeometryRegistryEventKind.Updated,
            Registry(1),
            RemoteRecord("late", "late-sha", "late-revision"),
            null,
            DateTimeOffset.UtcNow));
        await Task.Delay(50);
        Assert.Equal(eventCount, sink.Events.Count);
    }

    [Fact]
    public async Task UnimplementedWatch_IsReportedAsUnsupportedWithoutRestartLoop()
    {
        var registry = new FakeSessionRegistry("connection-1");
        var gateway = new StreamingGateway { ThrowUnimplementedOnWatch = true };
        var sink = new RecordingSink();
        using var service = new GeometryRegistrySupervisionService(
            registry,
            gateway,
            sink,
            NullLogger<GeometryRegistrySupervisionService>.Instance);

        await service.ReconcileOnceAsync();
        await WaitUntilAsync(() => sink.LastStatus?.Status == GeometryRegistryWatchStatusKind.Unsupported);

        Assert.Equal(1, gateway.WatchCount);
        Assert.Equal(0, sink.LastStatus!.RestartCount);

        registry.ConnectionIdsValue = [];
        await service.ReconcileOnceAsync();
    }

    [Fact]
    public async Task ReloadedEvent_RefreshesAuthoritativeSnapshot()
    {
        var registry = new FakeSessionRegistry("connection-1");
        var gateway = new StreamingGateway
        {
            Listed = [RemoteRecord("route-1", "sha-1", "revision-1")]
        };
        var sink = new RecordingSink();
        using var service = new GeometryRegistrySupervisionService(
            registry,
            gateway,
            sink,
            NullLogger<GeometryRegistrySupervisionService>.Instance);

        await service.ReconcileOnceAsync();
        await WaitUntilAsync(() => sink.SnapshotCount == 1);
        gateway.Listed = [RemoteRecord("route-2", "sha-2", "revision-2")];

        gateway.Publish(new GeometryRegistryEvent(
            "connection-1",
            GeometryRegistryEventKind.Reloaded,
            Registry(1),
            null,
            null,
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => sink.SnapshotCount == 2);

        Assert.Equal("route-2", Assert.Single(sink.LastSnapshotRecords!).GeometryId);

        registry.ConnectionIdsValue = [];
        await service.ReconcileOnceAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected geometry supervision state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static RemoteGeometryRecord RemoteRecord(string id, string sha, string revision)
        => new(
            "connection-1",
            id,
            GeometryDocumentKind.WaypointSequence,
            id,
            GeometryCoordinateFrame.GlobalWgs84,
            false,
            2,
            0,
            128,
            sha,
            revision,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

    private static GeometryRegistrySnapshot Registry(int count)
        => new(
            "connection-1",
            GeometryRegistryState.Ready,
            "Healthy",
            "Ready",
            "OK",
            "Registry ready",
            count,
            "signature",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);

    private sealed class FakeSessionRegistry(params string[] connectionIds) : ILogosOperationalSessionRegistry
    {
        public IReadOnlyList<string> ConnectionIdsValue { get; set; } = connectionIds;

        public IReadOnlyList<string> ConnectionIds => ConnectionIdsValue;

        public bool TryGet(string connectionId, out ILogosOperationalSession? session)
        {
            session = null;
            return false;
        }

        public ILogosOperationalSession GetRequired(string connectionId) => throw new NotSupportedException();

        public Task<ILogosOperationalSession> OpenAsync(
            ConnectionDefinition definition,
            ConnectionCredentials credentials,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CloseAsync(string connectionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamingGateway : IGeometryGateway
    {
        private readonly Channel<GeometryRegistryEvent> _events = Channel.CreateUnbounded<GeometryRegistryEvent>();

        public bool IsAvailable => true;
        public string AvailabilityMessage => "Geometry available";
        public IReadOnlyList<RemoteGeometryRecord> Listed { get; set; } = [];
        public bool ThrowUnimplementedOnWatch { get; set; }
        public int WatchCount { get; private set; }

        public void Publish(GeometryRegistryEvent value) => _events.Writer.TryWrite(value);

        public Task<IReadOnlyList<RemoteGeometryRecord>> ListAsync(
            string connectionId,
            GeometryQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Listed);

        public Task<RemoteGeometryObject?> GetAsync(
            string connectionId,
            string geometryId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RemoteGeometryObject?>(null);

        public Task<GeometryValidationResult> ValidateAsync(
            string connectionId,
            GeometryDocument document,
            bool checkUpdateCompatibility = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeometryValidationResult(GeometryValidationState.Valid, "Valid", []));

        public Task<GeometryCommandResult> CreateAsync(
            GeometryCreateRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> UpdateAsync(
            GeometryUpdateRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> DeleteAsync(
            GeometryDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryRegistrySnapshot> GetRegistryStatusAsync(
            string connectionId,
            bool includeDetails = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Registry(Listed.Count));

        public async IAsyncEnumerable<GeometryRegistryEvent> WatchAsync(
            string connectionId,
            GeometryQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            WatchCount++;
            if (ThrowUnimplementedOnWatch)
            {
                await Task.Yield();
                throw new RpcException(new Status(StatusCode.Unimplemented, "WatchGeometryRegistry is unavailable"));
            }

            await foreach (var value in _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return value;
            }
        }
    }

    private sealed class RecordingSink : IGeometryRegistryStateSink
    {
        private readonly object _gate = new();
        private readonly List<GeometryRegistryEvent> _events = [];
        private GeometryRegistryWatchState? _lastStatus;
        private IReadOnlyList<RemoteGeometryRecord>? _lastSnapshotRecords;
        private int _snapshotCount;
        private int _clearCount;
        private string? _clearedConnectionId;

        public int SnapshotCount { get { lock (_gate) return _snapshotCount; } }
        public int ClearCount { get { lock (_gate) return _clearCount; } }
        public string? ClearedConnectionId { get { lock (_gate) return _clearedConnectionId; } }
        public GeometryRegistryWatchState? LastStatus { get { lock (_gate) return _lastStatus; } }
        public IReadOnlyList<GeometryRegistryEvent> Events { get { lock (_gate) return _events.ToArray(); } }
        public IReadOnlyList<RemoteGeometryRecord>? LastSnapshotRecords { get { lock (_gate) return _lastSnapshotRecords; } }

        public void ReplaceRemoteSnapshot(
            string connectionId,
            IReadOnlyList<RemoteGeometryRecord> records,
            GeometryRegistrySnapshot registry)
        {
            lock (_gate)
            {
                _snapshotCount++;
                _lastSnapshotRecords = records.ToArray();
            }
        }

        public void ApplyRegistryEvent(GeometryRegistryEvent registryEvent)
        {
            lock (_gate)
            {
                _events.Add(registryEvent);
            }
        }

        public void SetRegistryWatchState(GeometryRegistryWatchState state, bool notify = true)
        {
            lock (_gate)
            {
                _lastStatus = state;
            }
        }

        public void ClearRemoteConnection(string connectionId, GeometryRegistryWatchState finalState)
        {
            lock (_gate)
            {
                _clearCount++;
                _clearedConnectionId = connectionId;
                _lastStatus = finalState;
            }
        }
    }
}
