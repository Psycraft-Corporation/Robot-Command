using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public interface ILogosOperationalSessionRegistry : IAsyncDisposable
{
    IReadOnlyList<string> ConnectionIds { get; }

    bool TryGet(string connectionId, out ILogosOperationalSession? session);

    ILogosOperationalSession GetRequired(string connectionId);

    Task<ILogosOperationalSession> OpenAsync(
        ConnectionDefinition definition,
        ConnectionCredentials credentials,
        CancellationToken cancellationToken = default);

    Task CloseAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
}
