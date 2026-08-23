using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using Xunit;

namespace RobotCommand.Tests.Connections;

public sealed class OperationalLogosConnectionManagerTests
{
    [Fact]
    public async Task Connect_OpensAndInspectsOperationalSession()
    {
        var definition = ConnectionDefinition.CreateDirect("SITL", "http://localhost:50051");
        var inner = new RecordingConnectionManager(definition);
        var registry = new RecordingRegistry();
        await using var manager = new OperationalLogosConnectionManager(
            inner,
            registry,
            NullLogger<OperationalLogosConnectionManager>.Instance);

        await manager.ConnectAsync(
            definition.Id,
            new ConnectionCredentials("test-key", null));

        Assert.Equal(definition.Id, inner.ConnectedId);
        Assert.Equal(definition.Id, registry.OpenedId);
        Assert.Equal(1, registry.Session.InspectionCount);
    }

    [Fact]
    public async Task Disconnect_ClosesOperationalSessionBeforeDelegating()
    {
        var definition = ConnectionDefinition.CreateDirect("SITL", "http://localhost:50051");
        var inner = new RecordingConnectionManager(definition);
        var registry = new RecordingRegistry();
        await using var manager = new OperationalLogosConnectionManager(
            inner,
            registry,
            NullLogger<OperationalLogosConnectionManager>.Instance);

        await manager.DisconnectAsync(definition.Id);

        Assert.Equal(definition.Id, registry.ClosedId);
        Assert.Equal(definition.Id, inner.DisconnectedId);
    }

    [Fact]
    public async Task MavlinkConnection_NeverOpensLogosOperationalSession()
    {
        var definition = new ConnectionDefinition(
            "px4",
            "PX4",
            "udp-listen://0.0.0.0:14550",
            ConnectionMode.Mavlink,
            Mavlink: new MavlinkConnectionOptions());
        var inner = new RecordingConnectionManager(definition);
        var registry = new RecordingRegistry();
        await using var manager = new OperationalLogosConnectionManager(
            inner,
            registry,
            NullLogger<OperationalLogosConnectionManager>.Instance);

        await manager.ConnectAsync(definition.Id, ConnectionCredentials.Empty);
        await manager.RefreshAsync(definition.Id);
        await manager.DisconnectAsync(definition.Id);

        Assert.Null(registry.OpenedId);
        Assert.Null(registry.ClosedId);
    }

    [Fact]
    public async Task LinkdConnection_NeverOpensLogosOperationalSession()
    {
        var definition = new ConnectionDefinition(
            "linkd",
            "LinkD",
            "http://127.0.0.1:9467",
            ConnectionMode.FieldLink);
        var inner = new RecordingConnectionManager(definition);
        var registry = new RecordingRegistry();
        await using var manager = new OperationalLogosConnectionManager(
            inner,
            registry,
            NullLogger<OperationalLogosConnectionManager>.Instance);

        await manager.ConnectAsync(definition.Id, ConnectionCredentials.Empty);
        await manager.DisconnectAsync(definition.Id);

        Assert.Null(registry.OpenedId);
        Assert.Null(registry.ClosedId);
    }

    private sealed class RecordingRegistry : ILogosOperationalSessionRegistry
    {
        public FakeSession Session { get; } = new();
        public string? OpenedId { get; private set; }
        public string? ClosedId { get; private set; }
        public IReadOnlyList<string> ConnectionIds => OpenedId is null ? [] : [OpenedId];

        public bool TryGet(string connectionId, out ILogosOperationalSession? session)
        {
            session = OpenedId == connectionId ? Session : null;
            return session is not null;
        }

        public ILogosOperationalSession GetRequired(string connectionId)
            => TryGet(connectionId, out var session) && session is not null
                ? session
                : throw new InvalidOperationException();

        public Task<ILogosOperationalSession> OpenAsync(
            ConnectionDefinition definition,
            ConnectionCredentials credentials,
            CancellationToken cancellationToken = default)
        {
            OpenedId = definition.Id;
            Session.SetDefinition(definition);
            return Task.FromResult<ILogosOperationalSession>(Session);
        }

        public Task CloseAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            ClosedId = connectionId;
            OpenedId = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession : ILogosOperationalSession
    {
        private ConnectionDefinition _definition =
            ConnectionDefinition.CreateDirect("Unknown", "http://localhost:1");

        public ConnectionDefinition Definition => _definition;
        public LogosOperationalClients Clients => throw new NotSupportedException();
        public OperationalApiSnapshot Status => OperationalApiSnapshot.Unknown(_definition.Id);
        public int InspectionCount { get; private set; }

        public void SetDefinition(ConnectionDefinition definition) => _definition = definition;

        public Task<OperationalApiSnapshot> InspectAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            InspectionCount++;
            return Task.FromResult(Status);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingConnectionManager(ConnectionDefinition definition) : ILogosConnectionManager
    {
        public IReadOnlyList<ConnectionDefinition> Definitions { get; } = [definition];
        public string? ConnectedId { get; private set; }
        public string? DisconnectedId { get; private set; }

        public bool TryGetDefinition(string connectionId, out ConnectionDefinition? result)
        {
            result = Definitions.FirstOrDefault(item => item.Id == connectionId);
            return result is not null;
        }

        public Task RegisterAsync(ConnectionDefinition value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(ConnectionDefinition value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConnectAsync(
            string connectionId,
            ConnectionCredentials credentials,
            CancellationToken cancellationToken = default)
        {
            ConnectedId = connectionId;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            DisconnectedId = connectionId;
            return Task.CompletedTask;
        }

        public Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CameraStreamRecord> OpenCameraStreamAsync(string connectionId, CameraStreamOpenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseCameraStreamAsync(string connectionId, string streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(string connectionId, OperatorPolicyRequest request, CancellationToken cancellationToken = default) => Task.FromResult(OperatorPolicyEvaluation.Unavailable("Not used"));
        public Task ConnectAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAutoConnectionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SuperviseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
