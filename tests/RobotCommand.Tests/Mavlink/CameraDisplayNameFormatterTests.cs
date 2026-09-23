using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class CameraDisplayNameFormatterTests
{
    [Theory]
    [InlineData("SIYI\u0000\u0000", "ZR10\0", "SIYI ZR10")]
    [InlineData("SIYI\uFFFD", "ZR10", "MAVLink camera 1/100")]
    [InlineData("\0\0", "\t\0 ", "MAVLink camera 1/100")]
    public void RemovesPaddingAndRejectsMalformedOrEmptyReportedNames(string vendor, string model, string expected)
        => Assert.Equal(expected, CameraDisplayNameFormatter.Format(vendor, model, 1, 100));

    [Fact]
    public void KeepsValidUnicodeAndRemovesControlCharacters()
        => Assert.Equal("Ångström Camera", CameraDisplayNameFormatter.Format("Ångström\n", "Camera", 1, 100));
}
