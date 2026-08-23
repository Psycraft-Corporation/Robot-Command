using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public interface ILogosOperationalSession : IAsyncDisposable
{
    ConnectionDefinition Definition { get; }

    LogosOperationalClients Clients { get; }

    OperationalApiSnapshot Status { get; }

    Task<OperationalApiSnapshot> InspectAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}

public interface ILogosOperationalSessionFactory
{
    ILogosOperationalSession Create(
        ConnectionDefinition definition,
        ConnectionCredentials credentials);
}
