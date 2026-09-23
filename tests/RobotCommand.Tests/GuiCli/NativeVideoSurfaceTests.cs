using Avalonia;
using RobotCommand.Controls;
using Xunit;

namespace RobotCommand.Tests;

public sealed class NativeVideoSurfaceTests
{
    [Fact]
    public void FullWidthDestination_PreservesWholeFrameAndAspectRatioWhenTallerThanViewport()
    {
        var viewport = new Size(800, 300);
        var source = new PixelSize(960, 540);

        var destination = NativeVideoSurface.CalculateFullWidthDestination(viewport, source);

        Assert.Equal(viewport.Width, destination.Width, 6);
        Assert.Equal(450, destination.Height, 6);
        Assert.Equal(source.Width / (double)source.Height, destination.Width / destination.Height, 6);
        Assert.Equal(0, destination.X, 6);
        Assert.Equal(0, destination.Y, 6);
        Assert.True(destination.Height > viewport.Height);
    }

    [Fact]
    public void FullWidthDestination_FillsMatchingAspectViewportWithoutCropping()
    {
        var viewport = new Size(800, 450);
        var source = new PixelSize(960, 540);

        var destination = NativeVideoSurface.CalculateFullWidthDestination(viewport, source);

        Assert.Equal(new Rect(0, 0, viewport.Width, viewport.Height), destination);
    }

    [Fact]
    public void OperateVideoPane_ProvidesVerticalScrollingForTheFullFrame()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(
            root, "src", "app", "RobotCommand", "Views", "Workspaces", "OperateView.axaml"));

        Assert.Contains("<ScrollViewer Grid.Column=\"2\"", view);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", view);
        Assert.Contains("<Grid RowDefinitions=\"Auto,Auto,Auto,Auto\">", view);
        Assert.DoesNotContain("<Grid Grid.Row=\"1\" ClipToBounds=\"True\">", view);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("RobotCommand.sln was not found.");
    }
}
