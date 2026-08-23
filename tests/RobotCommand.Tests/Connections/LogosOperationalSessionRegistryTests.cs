using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using Xunit;

namespace RobotCommand.Tests.Connections;

public sealed class LogosOperationalSessionRegistryTests
{
    [Fact]
    public async Task Open_ReusesSessionForSameDefinitionAndCredentials()
    {
        var factory = new RecordingFactory();
        await using var registry = new LogosOperationalSessionRegistry(
            factory,
            NullLogger<LogosOperationalSessionRegistry>.Instance);
        var definition = ConnectionDefinition.CreateDirect("SITL", "http://localhost:50051");
        var credentials = new ConnectionCredentials("api-key", "bearer");

        var first = await registry.OpenAsync(definition, credentials);
        var second = await registry.OpenAsync(definition, credentials);

        Assert.Same(first, second);
        Assert.Single(factory.Created);
        Assert.True(registry.TryGet(definition.Id, out var stored));
        Assert.Same(first, stored);
    }

    [Fact]
    public async Task Open_ReplacesSessionWhenCredentialsChange()
    {
        var factory = new RecordingFactory();
        await using var registry = new LogosOperationalSessionRegistry(
            factory,
            NullLogger<LogosOperationalSessionRegistry>.Instance);
        var definition = ConnectionDefinition.CreateDirect("SITL", "http://localhost:50051");

        var first = (FakeSession)await registry.OpenAsync(
            definition,
            new ConnectionCredentials("one", null));
        var second = await registry.OpenAsync(
            definition,
            new ConnectionCredentials("two", null));

        Assert.NotSame(first, second);
        Assert.True(first.Disposed);
        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public async Task Close_RemovesAndDisposesSession()
    {
        var factory = new RecordingFactory();
        await using var registry = new LogosOperationalSessionRegistry(
            factory,
            NullLogger<LogosOperationalSessionRegistry>.Instance);
        var definition = ConnectionDefinition.CreateDirect("SITL", "http://localhost:50051");
        var session = (FakeSession)await registry.OpenAsync(
            definition,
            ConnectionCredentials.Empty);

        await registry.CloseAsync(definition.Id);

        Assert.True(session.Disposed);
        Assert.False(registry.TryGet(definition.Id, out _));
    }

    private sealed class RecordingFactory : ILogosOperationalSessionFactory
    {
        public List<FakeSession> Created { get; } = [];

        public ILogosOperationalSession Create(
            ConnectionDefinition definition,
            ConnectionCredentials credentials)
        {
            var session = new FakeSession(definition);
            Created.Add(session);
            return session;
        }
    }

    private sealed class FakeSession(ConnectionDefinition definition) : ILogosOperationalSession
    {
        public ConnectionDefinition Definition { get; } = definition;

        public LogosOperationalClients Clients => throw new NotSupportedException();

        public OperationalApiSnapshot Status { get; } = OperationalApiSnapshot.Unknown(definition.Id);

        public bool Disposed { get; private set; }

        public Task<OperationalApiSnapshot> InspectAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Status);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
