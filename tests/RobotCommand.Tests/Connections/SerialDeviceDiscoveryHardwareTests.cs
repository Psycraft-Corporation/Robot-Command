using RobotCommand.Services.Serial;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SerialDeviceDiscoveryHardwareTests
{
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task AttachedFtdiSikRadio_IsDiscoveredOnCom3()
    {
        if (Environment.GetEnvironmentVariable("ROBOT_COMMAND_SIK_HARDWARE") != "1") return;

        var devices = await new WindowsSerialDeviceDiscovery().DiscoverAsync(TestContext.Current.CancellationToken);
        var radio = Assert.Single(devices, item => item.PortName.Equals("COM3", StringComparison.OrdinalIgnoreCase));

        Assert.True(radio.IsLikelySikRadio);
        Assert.Equal("0403", radio.VendorId, ignoreCase: true);
        Assert.Equal("6015", radio.ProductId, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task AttachedUnpairedRadio_CanProbeLocalFirmwareWithoutRemoteRadio()
    {
        if (Environment.GetEnvironmentVariable("ROBOT_COMMAND_SIK_PROBE") != "1") return;

        var discovery = new WindowsSerialDeviceDiscovery();
        var devices = await discovery.DiscoverAsync(TestContext.Current.CancellationToken);
        var radio = Assert.Single(devices, item => item.PortName.Equals("COM3", StringComparison.OrdinalIgnoreCase));
        var service = new SikRadioConfigurationService(
            new SerialByteTransportFactory(new SerialPortLeaseManager()));

        var result = await service.ProbeAsync(radio, 57600, TestContext.Current.CancellationToken);

        Assert.True(result.Local.Available, result.Local.Error);
        Assert.False(result.Remote.Available);
        Assert.NotEmpty(result.Local.Settings);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task DirectlyAttachedCom3AndCom4Radios_CanBePaired()
    {
        if (Environment.GetEnvironmentVariable("ROBOT_COMMAND_SIK_PAIR") != "1") return;

        var devices = await new WindowsSerialDeviceDiscovery().DiscoverAsync(TestContext.Current.CancellationToken);
        var source = Assert.Single(devices, item => item.PortName.Equals("COM3", StringComparison.OrdinalIgnoreCase));
        var target = Assert.Single(devices, item => item.PortName.Equals("COM4", StringComparison.OrdinalIgnoreCase));
        var configuration = new SikRadioConfigurationService(
            new SerialByteTransportFactory(new SerialPortLeaseManager()));
        var result = await new SikRadioPairingService(configuration).PairAsync(source, target, 57600, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.DisplayText);
    }
}
