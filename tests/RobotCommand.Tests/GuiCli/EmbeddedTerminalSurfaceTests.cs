using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class EmbeddedTerminalSurfaceTests
{
    [Fact]
    public void Shell_ExposesAPersistentResizableEmbeddedCliTerminal()
    {
        var shell = typeof(ShellViewModel);

        Assert.NotNull(shell.GetProperty(nameof(ShellViewModel.Terminal)));
        Assert.NotNull(shell.GetProperty(nameof(ShellViewModel.TerminalOpen)));
        Assert.NotNull(shell.GetProperty(nameof(ShellViewModel.TerminalHeight)));
        Assert.NotNull(shell.GetProperty(nameof(ShellViewModel.TerminalResizeHeight)));

        var root = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "MainWindow.axaml"));
        var terminal = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Shell", "EmbeddedTerminalView.axaml"));

        Assert.Contains("EmbeddedTerminalView", window);
        Assert.Contains("TerminalHandle", window);
        Assert.Contains("OnTerminalHandlePressed", window);
        Assert.Contains("OnTerminalHandleMoved", window);
        Assert.Contains("OnTerminalHandleReleased", window);
        Assert.Contains("TerminalHeight", window);
        Assert.DoesNotContain("terminalDrawerToggle", window);
        Assert.Contains("Cursor=\"SizeNorthSouth\"", window);
        Assert.DoesNotContain("TerminalDrawerGlyph", window);
        Assert.DoesNotContain("ToggleTerminalCommand", window);
        Assert.Contains("Grid.Row=\"1\"", window);
        Assert.Contains("Grid.Row=\"2\"", window);
        Assert.DoesNotContain("Content=\"{loc:Loc Key=Terminal}\"", window);
        Assert.DoesNotContain("LOGOS ONLY", window);
        Assert.Contains("TerminalTitle", terminal);
        Assert.DoesNotContain("TerminalDescription", terminal);
        Assert.Contains("TextWrapping=\"Wrap\"", terminal);
        Assert.Contains("OnInputKeyDown", terminal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the RobotCommand repository root.");
    }
}
