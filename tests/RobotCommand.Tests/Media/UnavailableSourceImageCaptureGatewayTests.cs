using RobotCommand.Models;
using RobotCommand.Services.Evidence;
using Xunit;

namespace RobotCommand.Tests;

public sealed class UnavailableSourceImageCaptureGatewayTests
{
    [Fact]
    public async Task Capture_DoesNotSubstituteDisplayedFrame()
    {
        var gateway = new UnavailableSourceImageCaptureGateway();
        var result = await gateway.CaptureAsync(new SourceImageCaptureRequest(
            "connection",
            "front",
            new EvidenceCaptureContext(
                "connection", null, "vehicle", "Dracula", "front", null, null,
                null, null, null, null, null, null, null, null, null)));

        Assert.False(gateway.Status.Available);
        Assert.False(result.Succeeded);
        Assert.Null(result.Evidence);
        Assert.Contains("will not substitute", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
