using RobotCommand.Cli;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Reconciliation;
using Xunit;

namespace RobotCommand.Tests.GuiCli;

public sealed class CliUnifiedMediaWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"robot-command-cli-media-{Guid.NewGuid():N}");

    [Fact]
    public async Task CliRegistersRtspConnectionAssociatesCameraOnlyUnitAndReportsVideoRoute()
    {
        Assert.Equal(0, await ConnectionCommands.RunAsync([
            "connection", "add", "--mode", "media", "--name", "HM30", "--target", "rtsp://127.0.0.1:8554/live", "--data-dir", _directory]));
        var mediaConnection = Assert.Single(AppConfiguration.Load(_directory).Connections.Where(item => item.Mode == ConnectionMode.Media));
        Assert.StartsWith("media:", mediaConnection.Id);

        Assert.Equal(0, await ConnectionCommands.RunAsync([
            "unit", "create", "--name", "Dracula", "--connection", mediaConnection.Id, "--data-dir", _directory]));
        var savedBeforeCamera = new UnitDefinitionService(_directory).Units.Single();
        Assert.Empty(savedBeforeCamera.VehicleSources);

        Assert.Equal(0, await ConnectionCommands.RunAsync([
            "unit", "camera", "bind", savedBeforeCamera.Id,
            "--camera-id", "zr10", "--name", "ZR10", "--video-connection", mediaConnection.Id, "--data-dir", _directory]));
        var saved = new UnitDefinitionService(_directory).Units.Single();
        var sourceId = mediaConnection.Id["media:".Length..];
        Assert.Equal(sourceId, Assert.Single(saved.Cameras!).MediaSourceId);

        Assert.Equal(0, await ConnectionCommands.RunAsync([
            "unit", "route", "status", saved.Id, "--data-dir", _directory]));
        Assert.Contains(saved.Routes!, item => item.Role == UnitRouteRole.Video && item.MediaSourceId == sourceId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
