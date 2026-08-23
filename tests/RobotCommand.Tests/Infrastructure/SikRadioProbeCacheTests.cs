using RobotCommand.Services.Serial;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SikRadioProbeCacheTests
{
    [Fact]
    public void StableDeviceIdentityNeverFallsBackToAnotherRadiosComPort()
    {
        var cache = new SikRadioProbeCache();
        var first = CreateProbe("ftdi-ground", "COM3", 73);
        cache.Store(first);

        var found = cache.TryGet("ftdi-air", "COM3", out _);

        Assert.False(found);
    }

    [Fact]
    public void ReturnsOnlyProbeForTheSelectedStableRadio()
    {
        var cache = new SikRadioProbeCache();
        var ground = CreateProbe("ftdi-ground", "COM3", 73);
        var air = CreateProbe("ftdi-air", "COM4", 25);
        cache.Store(ground);
        cache.Store(air);

        Assert.True(cache.TryGet("ftdi-air", "COM4", out var result));
        Assert.Equal("COM4", result.Device.PortName);
        Assert.Equal(25, result.Local.Setting(3));
    }

    private static SikRadioProbeResult CreateProbe(string id, string port, int networkId)
    {
        var device = new SerialDeviceDescriptor(
            id,
            port,
            "Test SiK",
            Manufacturer: "Test",
            VendorId: "0403",
            ProductId: "6015",
            SerialNumber: id,
            IsLikelySikRadio: true);
        var local = new SikRadioSnapshot(true, false, "test", "test", "test", new Dictionary<int, int> { [3] = networkId }, "");
        var remote = new SikRadioSnapshot(false, true, null, null, null, new Dictionary<int, int>(), "", "No remote");
        return new SikRadioProbeResult(device, local, remote, DateTimeOffset.UtcNow);
    }
}
