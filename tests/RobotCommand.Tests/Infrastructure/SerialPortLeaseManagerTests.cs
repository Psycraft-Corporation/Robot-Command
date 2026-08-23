using RobotCommand.Services.Serial;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SerialPortLeaseManagerTests
{
    [Fact]
    public async Task Lease_PreventsConcurrentMavlinkAndConfigurationOwners()
    {
        var manager = new SerialPortLeaseManager();
        await using var mavlink = await manager.AcquireAsync("com3", "MAVLink", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.AcquireAsync("COM3", "SiK configuration", TestContext.Current.CancellationToken));

        Assert.Contains("MAVLink", exception.Message);
    }

    [Fact]
    public async Task Lease_CanBeReacquiredAfterRelease()
    {
        var manager = new SerialPortLeaseManager();
        var first = await manager.AcquireAsync("COM3", "MAVLink", TestContext.Current.CancellationToken);
        await first.DisposeAsync();

        await using var second = await manager.AcquireAsync("COM3", "SiK configuration", TestContext.Current.CancellationToken);

        Assert.Equal("COM3", second.PortName);
    }
}
