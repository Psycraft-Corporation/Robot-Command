using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

/// <summary>
/// Transport-neutral connection contract. ILogosConnection is retained as a
/// compatibility surface while connection providers migrate to this name.
/// </summary>
public interface IManagedConnection : ILogosConnection
{
}

public interface IConnectionProvider
{
    bool Supports(ConnectionMode mode);

    IManagedConnection Create(ConnectionDefinition definition);
}
