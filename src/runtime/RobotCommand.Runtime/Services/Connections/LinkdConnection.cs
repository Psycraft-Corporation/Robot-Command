using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

/// <summary>
/// Retains the LinkD connection type for saved-connection compatibility while
/// the external LinkD SDK is unavailable to the public application build.
/// </summary>
public sealed class LinkdConnection : UnsupportedLogosConnection
{
    public LinkdConnection(ConnectionDefinition definition, ILogger<LinkdConnection> logger)
        : base(definition, "LinkD support is unavailable in this build.")
    {
    }
}
