using RobotCommand.Cli;
using Xunit;

namespace RobotCommand.Tests;

public sealed class CliConnectionArgumentsTests
{
    [Fact]
    public void ParsesMavlinkConnectionOptionsWithoutTreatingSecretsAsValues()
    {
        var parsed = CliArguments.Parse([
            "--name", "PX4 radio", "--mode", "mavlink", "--target", "serial://COM3",
            "--transport", "serial", "--baud", "57600", "--alias", "1=Dracula", "--json"]);

        Assert.Null(parsed.Error);
        Assert.Equal("PX4 radio", parsed.Required("name"));
        Assert.Equal("serial://COM3", parsed.Required("target"));
        Assert.Equal("57600", parsed.Get("baud"));
        Assert.Equal(["1=Dracula"], parsed.Values("alias"));
        Assert.True(parsed.Has("json"));
    }

    [Fact]
    public void RejectsOptionWithoutValue()
    {
        var parsed = CliArguments.Parse(["--name"]);
        Assert.Equal("Option '--name' requires a value.", parsed.Error);
    }

    [Fact]
    public void ExplicitNegativeConnectionFlagsOverrideDefaults()
    {
        var parsed = CliArguments.Parse(["--no-auto-connect", "--no-auto-reconnect"]);
        Assert.False(parsed.Bool("auto-connect", true));
        Assert.False(parsed.Bool("auto-reconnect", true));
    }
}
