using Xunit;

namespace RobotCommand.Tests;

public sealed class CameraStreamPlaybackSurfaceTests
{
    [Fact]
    public void OperateView_BindsTheCameraPlayButtonAndShowsPlaybackFeedback()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));

        Assert.Contains("AutomationProperties.Name=\"Open stream\"", view);
        Assert.Contains("Command=\"{Binding Camera.OpenStreamCommand}\"", view);
        Assert.Contains("Text=\"{Binding Camera.PlaybackSummary}\"", view);
        Assert.Contains("Text=\"{Binding Camera.PlaybackDetail}\"", view);
    }

    [Fact]
    public void OpenStreamCommand_SetsPendingFeedbackBeforeWaitingForTheStreamGate()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "ViewModels", "CameraPanelViewModel.cs"));
        var openMethod = viewModel.IndexOf("private async Task OpenStreamAsync(", StringComparison.Ordinal);
        Assert.True(openMethod >= 0);

        var pendingFeedback = viewModel.IndexOf("Text(\"VideoOpeningStream\"", openMethod, StringComparison.Ordinal);
        var streamGate = viewModel.IndexOf("await _streamGate.WaitAsync(cancellationToken)", openMethod, StringComparison.Ordinal);

        Assert.True(pendingFeedback > openMethod);
        Assert.True(streamGate > pendingFeedback);
        Assert.Contains("PlaybackSummary = \"Camera stream failed\"", viewModel);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("RobotCommand.sln was not found.");
    }
}
