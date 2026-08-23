using Xunit;

namespace RobotCommand.Tests;

public sealed class OperatorLocationSurfaceTests
{
    [Fact]
    public void MapControl_ContainsDedicatedOperatorLocationSurface()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "Controls",
            "NativeOperationalMapControl.cs"));
        var operate = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "Views",
            "Workspaces",
            "OperateView.axaml"));

        Assert.Contains("OperatorNavigationLabel", operate, StringComparison.Ordinal);
        Assert.Contains("JumpToOperatorCommand", operate, StringComparison.Ordinal);
        Assert.Contains("Operator location", source, StringComparison.Ordinal);
        Assert.Contains("ReplaceOperatorLocationFeatures", source, StringComparison.Ordinal);
        Assert.Contains("operatorLocation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_RegistersOperatorLocationLifecycle()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "Bootstrap",
            "AppHost.cs"));

        Assert.Contains("IOperatorLocationService", source, StringComparison.Ordinal);
        Assert.Contains("WindowsOperatorLocationService", source, StringComparison.Ordinal);
        Assert.Contains("AddHostedService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorLocationChanges_AreMarshalledToTheUiDispatcher()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "ViewModels",
            "OperationalMapViewModel.cs"));

        Assert.Contains("private void OnOperatorLocationChanged", source, StringComparison.Ordinal);
        Assert.Contains("_dispatcher.CheckAccess()", source, StringComparison.Ordinal);
        Assert.Contains("_dispatcher.InvokeAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_StartsHostedServicesAfterTheMainWindowIsOpened()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "App.axaml.cs"));

        Assert.Contains("mainWindow.Opened += OnMainWindowOpened", source, StringComparison.Ordinal);
        Assert.Contains("private async void OnMainWindowOpened", source, StringComparison.Ordinal);
        Assert.Contains("await host.StartAsync();", source, StringComparison.Ordinal);
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
