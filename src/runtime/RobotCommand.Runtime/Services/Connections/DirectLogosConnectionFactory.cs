using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Connections;

public sealed class DirectLogosConnectionFactory : IConnectionProvider
{
    private readonly AppConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    public DirectLogosConnectionFactory(
        AppConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    public bool Supports(ConnectionMode mode)
        => mode == ConnectionMode.Direct;

    public IManagedConnection Create(ConnectionDefinition definition)
        => definition.Mode switch
        {
            ConnectionMode.Direct => new DirectLogosConnection(
                definition,
                _configuration,
                _loggerFactory.CreateLogger<DirectLogosConnection>()),
            _ => new UnsupportedLogosConnection(
                definition,
                $"Unsupported connection mode: {definition.Mode}")
        };
}
