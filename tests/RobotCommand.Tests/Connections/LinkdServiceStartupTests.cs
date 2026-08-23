using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Services.Connections;
using Xunit;

namespace RobotCommand.Tests.Connections;

public sealed class LinkdServiceStartupTests
{
    [Fact]
    public async Task StartupServiceLogsAndDoesNotStopSharedService()
    {
        var controller = new FakeLinkdServiceController(
            new LinkdServiceStartupResult(
                LinkdServiceStartupState.Started,
                "The LogosLinkd service was started."));
        var service = new LinkdServiceStartupService(
            controller,
            NullLogger<LinkdServiceStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, controller.Calls);
    }

    [Fact]
    public async Task StartupServiceDoesNotThrowWhenLinkdIsNotInstalled()
    {
        var controller = new FakeLinkdServiceController(
            new LinkdServiceStartupResult(
                LinkdServiceStartupState.NotInstalled,
                "The LogosLinkd Windows service is not installed."));
        var service = new LinkdServiceStartupService(
            controller,
            NullLogger<LinkdServiceStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, controller.Calls);
    }

    private sealed class FakeLinkdServiceController(LinkdServiceStartupResult result)
        : ILinkdServiceController
    {
        public int Calls { get; private set; }

        public Task<LinkdServiceStartupResult> EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
