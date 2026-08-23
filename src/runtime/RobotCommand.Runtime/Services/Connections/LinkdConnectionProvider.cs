using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

/// <summary>Creates connections to the local or remote Logos LinkD daemon.</summary>
public sealed class LinkdConnectionProvider : IConnectionProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public LinkdConnectionProvider(ILoggerFactory loggerFactory)
        => _loggerFactory = loggerFactory;

    public bool Supports(ConnectionMode mode) => mode == ConnectionMode.FieldLink;

    public IManagedConnection Create(ConnectionDefinition definition)
        => new LinkdConnection(definition, _loggerFactory.CreateLogger<LinkdConnection>());
}
