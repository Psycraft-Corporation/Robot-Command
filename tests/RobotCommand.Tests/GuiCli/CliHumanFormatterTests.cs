using RobotCommand.Cli;
using Xunit;

namespace RobotCommand.Tests.GuiCli;

public sealed class CliHumanFormatterTests
{
    [Fact]
    public void FormatsListAsReadableHeadingAndRecords()
    {
        var output = CliHumanFormatter.Format("connection.list", new[]
        {
            new { id = "px4", displayName = "Dracula", state = "Online", isConnected = true },
            new { id = "ghost", displayName = "Ghost 1", state = "Offline", isConnected = false }
        });

        Assert.Contains("Connection list (2)", output, StringComparison.Ordinal);
        Assert.Contains("- Dracula", output, StringComparison.Ordinal);
        Assert.Contains("ID: px4", output, StringComparison.Ordinal);
        Assert.Contains("Is connected: Yes", output, StringComparison.Ordinal);
        Assert.Contains("- Ghost 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatsNestedValuesWithoutEmittingJsonPunctuation()
    {
        var output = CliHumanFormatter.Format("manual.status", new
        {
            session = new { state = "Active", inputAgeMilliseconds = 42 },
            reading = new { forward = 0.5, deadmanPressed = true }
        });

        Assert.Contains("Manual status", output, StringComparison.Ordinal);
        Assert.Contains("Session:", output, StringComparison.Ordinal);
        Assert.Contains("Input age milliseconds: 42", output, StringComparison.Ordinal);
        Assert.Contains("Deadman pressed: Yes", output, StringComparison.Ordinal);
        Assert.DoesNotContain("{", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", output, StringComparison.Ordinal);
    }
}
