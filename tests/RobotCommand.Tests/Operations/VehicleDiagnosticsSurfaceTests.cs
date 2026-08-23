using Xunit;

namespace RobotCommand.Tests;

public sealed class VehicleDiagnosticsSurfaceTests
{
    [Fact]
    public void OperateUnitSurface_UsesNormalizedDiagnosticsWithoutLegacyReadinessPanel()
    {
        var view = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "app",
            "RobotCommand",
            "Views",
            "Workspaces",
            "OperateView.axaml"));

        Assert.Contains("OVERALL ASSESSMENT", view);
        Assert.Contains("PREFLIGHT SUMMARY", view);
        Assert.Contains("Text=\"Arm\"", view);
        Assert.Contains("Text=\"Navigation\"", view);
        Assert.Contains("ArmReadinessDetail", view);
        Assert.Contains("NavigationReadinessDetail", view);
        Assert.Contains("TelemetryStatusDetail", view);
        Assert.DoesNotContain("OverallSummary", view);
        Assert.Contains("CURRENT BLOCKERS AND WARNINGS", view);
        Assert.Contains("RecentAutopilotWarnings", view);
        Assert.DoesNotContain("SELECTED VEHICLE", view);
        Assert.DoesNotContain("Text=\"Vehicle readiness\"", view);

        var connections = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "app", "RobotCommand", "Views", "Workspaces", "ConnectionsView.axaml"));
        Assert.Contains("IsMavlinkParameterPanel", connections);
        Assert.Contains("ParameterPanelTitle", connections);
        Assert.Contains("ParameterBackendUnit", connections);
        Assert.Contains("SelectedValue=\"{Binding SelectedConnectionId, Mode=TwoWay}\"", connections);
        Assert.Contains("SelectedValueBinding=\"{Binding Id}\"", connections);
        Assert.DoesNotContain("SelectedItem=\"{Binding SelectedConnection}\"", connections);
    }

    [Fact]
    public void UnitsRail_ShowsCompactSelectedUnitStatusAndMetrics()
    {
        var view = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "app",
            "RobotCommand",
            "Views",
            "Shell",
            "UnitsPanelView.axaml"));

        Assert.Contains("SelectedStatusIndicators", view);
        Assert.Contains("SelectedConnectionChips", view);
        Assert.Contains("SelectedVelocity", view);
        Assert.Contains("SelectedVerticalSpeed", view);
        Assert.Contains("SelectedOperatorDistance", view);
        Assert.Contains("SelectedBattery", view);
        Assert.Contains("ToolTip.Tip", view);
        Assert.DoesNotContain("associated connection(s)", view);
        Assert.DoesNotContain("SelectedIdentity", view);
    }

    [Fact]
    public void OperatorsRail_StartsCollapsed()
    {
        var shell = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "app",
            "RobotCommand",
            "ViewModels",
            "ShellViewModel.cs"));

        Assert.Contains("private bool _leftRailOpen;", shell);
        Assert.Contains("private bool _leftRailManuallyCollapsed = true;", shell);
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
               ?? throw new InvalidOperationException("Could not locate the RobotCommand repository root.");
    }
}
