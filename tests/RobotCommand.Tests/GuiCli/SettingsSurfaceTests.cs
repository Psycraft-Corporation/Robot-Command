using Xunit;

namespace RobotCommand.Tests;

public sealed class SettingsSurfaceTests
{
    [Fact]
    public void SettingsWorkspace_IsRegisteredAndExposesBrightModeSetting()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "ViewModels", "ShellViewModel.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "App.axaml"));
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Workspaces", "SettingsView.axaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("\"settings\"", shell, StringComparison.Ordinal);
        Assert.Contains("new WorkspaceNavigationItem(\"settings\", L(\"Settings\"), settings)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsDescription", shell, StringComparison.Ordinal);
        Assert.Contains("SettingsViewModel", app, StringComparison.Ordinal);
        Assert.Contains("BrightModeEnabled", view, StringComparison.Ordinal);
        Assert.DoesNotContain("BrightModeDescription", view, StringComparison.Ordinal);
        Assert.Contains("Language", view, StringComparison.Ordinal);
        Assert.Contains("SelectedIndex=\"{Binding SelectedLanguageIndex, Mode=OneWay}\"", view, StringComparison.Ordinal);
        Assert.Contains("DropDownClosed=\"OnLanguageDropDownClosed\"", view, StringComparison.Ordinal);
        Assert.Contains("HorizontalDistanceTitle", view, StringComparison.Ordinal);
        Assert.Contains("VerticalDistanceTitle", view, StringComparison.Ordinal);
        Assert.Contains("TemperatureTitle", view, StringComparison.Ordinal);
        Assert.Contains("OnUnitDropDownClosed", view, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedValueBinding", view, StringComparison.Ordinal);
        Assert.Contains("RequestedThemeVariant = enabled ? ThemeVariant.Light : ThemeVariant.Dark", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void BrightModeStyles_TargetApplicationControlsWithoutReplacingMapOrVideoImagery()
    {
        var root = FindRepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Styles", "AppStyles.axaml"));

        Assert.Contains("Window.brightMode", styles, StringComparison.Ordinal);
        Assert.Contains("#FFD400", styles, StringComparison.Ordinal);
        Assert.Contains("#005A9C", styles, StringComparison.Ordinal);
        Assert.Contains("#087443", styles, StringComparison.Ordinal);
        Assert.Contains("#8A5300", styles, StringComparison.Ordinal);
        Assert.Contains("#B42318", styles, StringComparison.Ordinal);
        Assert.Contains("#FFFFFF", styles, StringComparison.Ordinal);
        Assert.Contains("#F4F7F9", styles, StringComparison.Ordinal);
        Assert.Contains("Button", styles, StringComparison.Ordinal);
        Assert.Contains("TextBox", styles, StringComparison.Ordinal);
        Assert.Contains("ListBox", styles, StringComparison.Ordinal);
        Assert.Contains("quickRunWarnings", styles, StringComparison.Ordinal);
        Assert.Contains("quickRunBlockers", styles, StringComparison.Ordinal);
        Assert.Contains("errorPanel", styles, StringComparison.Ordinal);
        Assert.Contains("Button:disabled", styles, StringComparison.Ordinal);
        Assert.Contains("TextBox:focus", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Window.brightMode Mapsui", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Window.brightMode Border.mapSurface", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("Window.brightMode Border.videoSurface", styles, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
