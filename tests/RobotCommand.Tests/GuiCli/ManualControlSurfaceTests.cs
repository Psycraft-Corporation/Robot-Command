using Xunit;

namespace RobotCommand.Tests;

public sealed class ManualControlSurfaceTests
{
    [Fact]
    public void UnitsRailShowsManualModeAndControllerConfirmationCountdown()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Shell", "UnitsPanelView.axaml"));

        Assert.Contains("Key=ManualMode", view, StringComparison.Ordinal);
        Assert.Contains("SelectedManualControlMode", view, StringComparison.Ordinal);
        Assert.Contains("Key=ControllerCommandPending", view, StringComparison.Ordinal);
        Assert.Contains("SelectedManualPendingExpiry", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualControlWorkspaceShowsCompactProfileAndSessionSetup()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Workspaces", "ManualControlView.axaml"));

        Assert.Contains("Key=ManualProfile", view, StringComparison.Ordinal);
        Assert.Contains("Key=ManualController", view, StringComparison.Ordinal);
        Assert.Contains("Key=ManualUnit", view, StringComparison.Ordinal);
        Assert.Contains("Key=ManualDiagnostics", view, StringComparison.Ordinal);
        Assert.Contains("ControlButtonText", view, StringComparison.Ordinal);
        Assert.Contains("ToggleControlCommand", view, StringComparison.Ordinal);
        Assert.DoesNotContain("TakeControlCommand", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseControlCommand", view, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualControlUsesTheSameToggleInTheGlobalSessionPill()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "MainWindow.axaml"));

        Assert.Contains("ManualControl.ControlButtonText", view, StringComparison.Ordinal);
        Assert.Contains("ManualControl.ToggleControlCommand", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualControl.ReleaseControlCommand", view, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
