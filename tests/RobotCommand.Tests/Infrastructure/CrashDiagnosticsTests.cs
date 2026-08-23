using RobotCommand.Infrastructure;
using Xunit;

namespace RobotCommand.Tests.Infrastructure;

public sealed class CrashDiagnosticsTests
{
    [Fact]
    public void Write_AppendsToTheCurrentDailyLog()
    {
        var marker = $"diagnostic-test-{Guid.NewGuid():N}";

        CrashDiagnostics.Write("INFO", "Tests", marker);

        Assert.True(File.Exists(CrashDiagnostics.CurrentLogPath));
        Assert.Contains(marker, File.ReadAllText(CrashDiagnostics.CurrentLogPath));
    }
}
