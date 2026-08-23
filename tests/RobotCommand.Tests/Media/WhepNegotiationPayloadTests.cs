using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class WhepNegotiationPayloadTests
{
    [Fact]
    public void Parse_ReadsOptionalNativePlayerHints()
    {
        var result = WhepNegotiationPayload.Parse(
            "{\"authToken\":\"token\",\"videoPayloadType\":127,\"useLinkHeaders\":false,\"stunServer\":\"stun://stun.local:3478\"}");

        Assert.Equal("token", result.AuthToken);
        Assert.Equal(127, result.PayloadType);
        Assert.False(result.UseLinkHeaders);
        Assert.Equal("stun://stun.local:3478", result.StunServer);
    }


    [Fact]
    public void Parse_ReadsSnakeCaseHintsAndRejectsInvalidPayloadType()
    {
        var result = WhepNegotiationPayload.Parse(
            "{\"auth_token\":\"token\",\"video_payload_type\":42,\"use_link_headers\":false,\"turn_server\":\"turn://user:password@turn.local:3478\"}");

        Assert.Equal("token", result.AuthToken);
        Assert.Equal(0, result.PayloadType);
        Assert.False(result.UseLinkHeaders);
        Assert.Equal("turn://user:password@turn.local:3478", result.TurnServer);
    }

    [Fact]
    public void Parse_NonJsonPayload_UsesSafeDefaults()
    {
        var result = WhepNegotiationPayload.Parse("opaque SDP or server payload");
        Assert.Null(result.AuthToken);
        Assert.True(result.UseLinkHeaders);
    }
}
