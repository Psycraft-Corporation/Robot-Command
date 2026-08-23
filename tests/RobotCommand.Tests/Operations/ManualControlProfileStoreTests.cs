using RobotCommand.Models;
using RobotCommand.Services.ManualControl;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ManualControlProfileStoreTests
{
    [Fact]
    public async Task SaveAndLoad_NormalizesAndPersistsProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-manual-{Guid.NewGuid():N}");
        try
        {
            var store = new ManualControlProfileStore(directory);
            await store.SaveAsync(new(-1, 2, 100, -1, 1000, -1, 1000, -1), CancellationToken.None);
            var profile = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(0, profile.DeadZone);
            Assert.Equal(1, profile.Expo);
            Assert.Equal(30, profile.MaximumHorizontalSpeedMetresPerSecond);
            Assert.Equal(0.1, profile.MaximumVerticalSpeedMetresPerSecond);
            Assert.True(File.Exists(Path.Combine(directory, "data", "manual-control.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesJoystickMappingByDeviceIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-manual-{Guid.NewGuid():N}");
        try
        {
            var store = new ManualControlProfileStore(directory);
            var deviceId = "joystick-044F-B10A-example";
            await store.SaveAsync(
                new ManualControlProfile().WithJoystickMapping(deviceId, new ManualJoystickMapping(
                    ForwardAxis: 7, ForwardInverted: false,
                    RightAxis: 6, RightInverted: true,
                    VerticalAxis: 5, VerticalInverted: false,
                    YawAxis: 4, YawInverted: true,
                    DeadmanButton: 9, ArmButton: 10, TakeoffButton: 11,
                    ExecuteButton: 12, CancelButton: 13, ReleaseButton: 14)),
                CancellationToken.None);

            var profile = await store.LoadAsync(CancellationToken.None);
            var mapping = profile.MappingFor(deviceId);

            Assert.Equal(7, mapping.ForwardAxis);
            Assert.False(mapping.ForwardInverted);
            Assert.Equal(6, mapping.RightAxis);
            Assert.True(mapping.RightInverted);
            Assert.Equal(9, mapping.DeadmanButton);
            Assert.Equal(14, mapping.ReleaseButton);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
