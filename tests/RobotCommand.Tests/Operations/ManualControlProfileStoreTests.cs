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

    [Fact]
    public async Task LoadLibrary_MigratesLegacyProfileToDefaultEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-manual-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "data"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, "data", "manual-control.json"),
                "{\"DeadZone\":0.2,\"MaximumHorizontalSpeedMetresPerSecond\":12}");

            var library = await new ManualControlProfileStore(directory).LoadLibraryAsync(CancellationToken.None);

            var profile = Assert.Single(library.Profiles);
            Assert.Equal(ManualControlProfileLibrary.CurrentSchemaVersion, library.SchemaVersion);
            Assert.Equal("default", library.ActiveProfileId);
            Assert.Equal("Default", profile.Name);
            Assert.Equal(0.2, profile.Settings.DeadZone);
            Assert.Equal(12, profile.Settings.MaximumHorizontalSpeedMetresPerSecond);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SaveLibrary_PreservesProfilesAndActiveSelectionAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-manual-{Guid.NewGuid():N}");
        try
        {
            var store = new ManualControlProfileStore(directory);
            var library = new ManualControlProfileLibrary(
                ManualControlProfileLibrary.CurrentSchemaVersion,
                "field",
                [
                    new ManualControlProfileRecord("default", "Default", new ManualControlProfile()),
                    new ManualControlProfileRecord("field", "Field", new ManualControlProfile(DeadZone: 0.25))
                ]);

            await store.SaveLibraryAsync(library, CancellationToken.None);
            var loaded = await new ManualControlProfileStore(directory).LoadLibraryAsync(CancellationToken.None);

            Assert.Equal("field", loaded.ActiveProfileId);
            Assert.Equal(["default", "field"], loaded.Profiles.Select(item => item.Id));
            Assert.Equal(0.25, loaded.Profiles.Single(item => item.Id == "field").Settings.DeadZone);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadLibrary_RejectsMalformedRecordsWithoutThrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robot-command-manual-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "data"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, "data", "manual-control.json"),
                "{\"schemaVersion\":\"robotcommand.manual-control-profiles.v1\",\"activeProfileId\":\"missing\",\"profiles\":[{\"id\":\"\",\"name\":\"\"},{\"id\":\"valid\",\"name\":\" Valid \",\"settings\":null}]}");

            var library = await new ManualControlProfileStore(directory).LoadLibraryAsync(CancellationToken.None);

            var profile = Assert.Single(library.Profiles);
            Assert.Equal("valid", profile.Id);
            Assert.Equal("Valid", profile.Name);
            Assert.Equal("valid", library.ActiveProfileId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
