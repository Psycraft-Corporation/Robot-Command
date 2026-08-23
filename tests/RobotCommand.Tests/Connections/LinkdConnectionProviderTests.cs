using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using Xunit;

namespace RobotCommand.Tests.Connections;

public sealed class LinkdConnectionProviderTests
{
    [Fact]
    public void SupportsOnlyFieldLinkMode()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var provider = new LinkdConnectionProvider(loggerFactory);

        Assert.True(provider.Supports(ConnectionMode.FieldLink));
        Assert.False(provider.Supports(ConnectionMode.Direct));
        Assert.False(provider.Supports(ConnectionMode.Mavlink));
        Assert.False(provider.Supports(ConnectionMode.Ghost));
    }

    [Fact]
    public void CreatesLinkdConnectionWithoutCreatingLogosSession()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var provider = new LinkdConnectionProvider(loggerFactory);
        var definition = new ConnectionDefinition(
            "linkd",
            "LinkD",
            "http://127.0.0.1:9467",
            ConnectionMode.FieldLink);

        var connection = provider.Create(definition);

        Assert.IsType<LinkdConnection>(connection);
        Assert.Equal(ConnectionMode.FieldLink, connection.Definition.Mode);
    }
}
