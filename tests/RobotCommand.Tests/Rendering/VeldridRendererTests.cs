using RobotCommand.Core;
using RobotCommand.Rendering.Veldrid;
using Xunit;

namespace RobotCommand.Tests;

public sealed class VeldridRendererTests
{
    [Fact]
    public async Task HardwareProbeIsNonFatalWhenDeviceIsUnavailable()
    {
        await using var renderer = new VeldridRenderer();

        await renderer.InitializeAsync(ThreeDRenderBackendPolicy.Auto);

        Assert.True(renderer.Status.IsInitialized || renderer.Status.FallbackReason is not null);
    }

    [Fact]
    public async Task SoftwarePolicyDoesNotCreateADevice()
    {
        await using var renderer = new VeldridRenderer();

        await renderer.InitializeAsync(ThreeDRenderBackendPolicy.Software);

        Assert.False(renderer.Status.IsHardwareAccelerated);
        Assert.False(renderer.Status.IsInitialized);
        Assert.Contains("Software", renderer.Status.FallbackReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
