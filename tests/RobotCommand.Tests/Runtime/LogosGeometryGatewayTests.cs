using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Missions;
using Xunit;

namespace RobotCommand.Tests;

public sealed class LogosGeometryGatewayTests
{
    [Fact]
    public void Availability_UsesInspectedGeometryDomain()
    {
        var registry = new FakeRegistry(new FakeSession(GeometryStatus(available: true)));
        var gateway = CreateGateway(registry);

        Assert.True(gateway.IsAvailable);
        Assert.Contains("1 connected Logos runtime", gateway.AvailabilityMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_NoActiveSession_ReturnsUnavailableResult()
    {
        var gateway = CreateGateway(new FakeRegistry());
        var document = GeometryDocument.Create(
            "poi-alpha",
            "POI Alpha",
            GeometryDocumentKind.PointOfInterest) with
        {
            Points = [GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65)]
        };

        var result = await gateway.ValidateAsync("missing", document);

        Assert.Equal(GeometryValidationState.Unavailable, result.State);
        Assert.Contains("no active Logos operational session", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegistryStatus_UnavailableDomainReturnsUnknownSnapshot()
    {
        var registry = new FakeRegistry(new FakeSession(GeometryStatus(available: false)));
        var gateway = CreateGateway(registry);

        var result = await gateway.GetRegistryStatusAsync("connection-1");

        Assert.Equal(GeometryRegistryState.Unknown, result.State);
        Assert.Contains("not available", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnavailableGateway_RejectsMutationsWithoutPretendingSuccess()
    {
        var gateway = new UnavailableGeometryGateway("Geometry SDK unavailable");
        var document = GeometryDocument.Create(
            "poi-alpha",
            "POI Alpha",
            GeometryDocumentKind.PointOfInterest);

        var result = await gateway.CreateAsync(new GeometryCreateRequest("connection-1", document));

        Assert.False(result.Accepted);
        Assert.Equal(GeometryCommandState.Rejected, result.State);
        Assert.Equal("Geometry SDK unavailable", result.Message);
    }

    private static LogosGeometryGateway CreateGateway(ILogosOperationalSessionRegistry registry)
    {
        var codec = new GeometryDocumentCodec();
        return new LogosGeometryGateway(
            registry,
            new LogosCommandMetadataFactory(),
            new GeometryProtoMapper(codec),
            NullLogger<LogosGeometryGateway>.Instance);
    }

    private static OperationalApiSnapshot GeometryStatus(bool available)
    {
        var domains = Enum.GetValues<OperationalApiDomain>()
            .Select(domain => new OperationalApiDomainStatus(
                domain,
                domain == OperationalApiDomain.Geometry
                    ? available
                        ? OperationalApiAvailability.Available
                        : OperationalApiAvailability.Unimplemented
                    : OperationalApiAvailability.Unavailable,
                domain == OperationalApiDomain.Geometry ? "Geometry" : domain.ToString(),
                available ? "Available" : "Not available",
                []))
            .ToArray();
        return new OperationalApiSnapshot(
            "connection-1",
            DateTimeOffset.UtcNow,
            domains,
            [],
            []);
    }

    private sealed class FakeRegistry(params ILogosOperationalSession[] sessions)
        : ILogosOperationalSessionRegistry
    {
        private readonly Dictionary<string, ILogosOperationalSession> _sessions = sessions
            .ToDictionary(item => item.Definition.Id, StringComparer.Ordinal);

        public IReadOnlyList<string> ConnectionIds => _sessions.Keys.ToArray();

        public bool TryGet(string connectionId, out ILogosOperationalSession? session)
        {
            var found = _sessions.TryGetValue(connectionId, out var value);
            session = value;
            return found;
        }

        public ILogosOperationalSession GetRequired(string connectionId)
            => _sessions[connectionId];

        public Task<ILogosOperationalSession> OpenAsync(
            ConnectionDefinition definition,
            ConnectionCredentials credentials,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CloseAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(OperationalApiSnapshot status) : ILogosOperationalSession
    {
        public ConnectionDefinition Definition { get; } =
            ConnectionDefinition.CreateDirect("Geometry test", "http://localhost:50051") with
            {
                Id = status.ConnectionId
            };

        public LogosOperationalClients Clients => throw new NotSupportedException();

        public OperationalApiSnapshot Status { get; } = status;

        public Task<OperationalApiSnapshot> InspectAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Status);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
