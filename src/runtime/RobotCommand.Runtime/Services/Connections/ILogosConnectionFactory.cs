using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public interface ILogosConnectionFactory
{
    ILogosConnection Create(ConnectionDefinition definition);
}
