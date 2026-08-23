using System.Text.Json.Nodes;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MediaSettingsServiceTests
{
    [Fact]
    public async Task SetAsync_PersistsNormalizedMediaSettingsWithoutDroppingLegacySections()
    {
        var root = Path.Combine(Path.GetTempPath(), "RobotCommand-media-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "appsettings.local.json"),
            "{\"ui\":{\"language\":\"fr\"},\"connections\":[]}");

        var service = new MediaSettingsService(new AppConfiguration(), root);
        await service.SetAsync(MediaSettingsSnapshot.FromConfiguration(new AppConfiguration()) with
        {
            RtspLatencyMilliseconds = -20,
            RollingBufferMinutes = 0,
            MaximumStorageGigabytes = 99999
        });

        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "appsettings.local.json")))!.AsObject();
        Assert.Equal("fr", document["ui"]!["language"]!.GetValue<string>());
        Assert.Equal(0, service.Current.RtspLatencyMilliseconds);
        Assert.Equal(1, service.Current.RollingBufferMinutes);
        Assert.Equal(2048, service.Current.MaximumStorageGigabytes);
        Assert.NotNull(document["video"]);
    }
}

