using RobotCommand.Models;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class VideoProtocolPolicyTests
{
    private readonly VideoProtocolPolicy _policy = new();

    [Fact]
    public void BuildPlan_LocalConnection_PrefersServerAutomaticThenRtsp()
    {
        var plan = _policy.BuildPlan(
            ConnectionDefinition.CreateDirect("Local", "http://192.168.1.20:50051"),
            VideoProtocolPreference.Automatic,
            Diagnostics());

        Assert.Equal(
            [VideoProtocolPreference.Automatic, VideoProtocolPreference.Rtsp, VideoProtocolPreference.Hls, VideoProtocolPreference.WebRtc],
            plan);
    }


    [Fact]
    public void BuildPlan_FieldLink_PrefersHttpFriendlyFallbacksBeforeRtsp()
    {
        var connection = new ConnectionDefinition(
            "field",
            "Field Link",
            "https://field-link.example.com",
            ConnectionMode.FieldLink);

        var plan = _policy.BuildPlan(
            connection,
            VideoProtocolPreference.Automatic,
            Diagnostics());

        Assert.Equal(
            [VideoProtocolPreference.Automatic, VideoProtocolPreference.Hls, VideoProtocolPreference.WebRtc, VideoProtocolPreference.Rtsp],
            plan);
    }

    [Fact]
    public void BuildPlan_PublicConnection_PrefersWhepAndHls()
    {
        var plan = _policy.BuildPlan(
            ConnectionDefinition.CreateDirect("Remote", "https://vehicle.example.com"),
            VideoProtocolPreference.Automatic,
            Diagnostics());

        Assert.Equal(VideoProtocolPreference.WebRtc, plan[0]);
        Assert.Equal(VideoProtocolPreference.Hls, plan[1]);
    }

    [Fact]
    public void BuildPlan_FiltersUnavailableOptionalPlugins()
    {
        var diagnostics = Diagnostics(hls: false);
        var plan = _policy.BuildPlan(
            ConnectionDefinition.CreateDirect("Local", "http://localhost:50051"),
            VideoProtocolPreference.Automatic,
            diagnostics);

        Assert.DoesNotContain(VideoProtocolPreference.Hls, plan);
        Assert.Contains(VideoProtocolPreference.Automatic, plan);
    }

    [Fact]
    public void BuildPlan_ManualRequest_IsNotSilentlyChanged()
    {
        var plan = _policy.BuildPlan(null, VideoProtocolPreference.Hls, Diagnostics(hls: false));
        Assert.Equal([VideoProtocolPreference.Hls], plan);
    }

    private static GStreamerRuntimeDiagnostics Diagnostics(bool hls = true)
        => new(
            GStreamerRuntimeState.Available,
            "available",
            "available",
            ProtocolCapabilities:
            [
                new(VideoProtocolPreference.Rtsp, true, [], ""),
                new(VideoProtocolPreference.Hls, hls, hls ? [] : ["hlsdemux"], ""),
                new(VideoProtocolPreference.WebRtc, true, [], "")
            ]);
}
