using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class MavlinkStreamFramerTests
{
    [Fact]
    public void Push_ReassemblesSplitMavlinkTwoFrame()
    {
        var framer = new MavlinkStreamFramer();
        var frame = V2([1, 2, 3]);

        Assert.Empty(framer.Push(frame.AsSpan(0, 5)));
        var result = Assert.Single(framer.Push(frame.AsSpan(5)));

        Assert.Equal(frame, result);
    }

    [Fact]
    public void Push_ExtractsMixedV1V2AndSkipsNoise()
    {
        var framer = new MavlinkStreamFramer();
        var v1 = V1([7, 8]);
        var v2 = V2([9]);
        var bytes = new byte[] { 0, 1, 2 }.Concat(v1).Concat(v2).ToArray();

        var result = framer.Push(bytes);

        Assert.Equal(2, result.Count);
        Assert.Equal(v1, result[0]);
        Assert.Equal(v2, result[1]);
        Assert.Equal(3, framer.DiscardedBytes);
    }

    [Fact]
    public void Push_ExtractsEveryFrameFromOneUdpDatagram()
    {
        var framer = new MavlinkStreamFramer();
        var first = V2([1, 2]);
        var second = V2([3, 4, 5]);

        var result = framer.Push(first.Concat(second).ToArray());

        Assert.Equal(2, result.Count);
        Assert.Equal(first, result[0]);
        Assert.Equal(second, result[1]);
    }

    [Fact]
    public void Push_AccountsForMavlinkTwoSignature()
    {
        var framer = new MavlinkStreamFramer();
        var signed = V2([4, 5], signed: true);

        var result = Assert.Single(framer.Push(signed));

        Assert.Equal(signed.Length, result.Length);
        Assert.Equal(27, result.Length);
    }

    private static byte[] V1(byte[] payload)
        => [0xFE, (byte)payload.Length, 1, 1, 1, 0, .. payload, 0, 0];

    private static byte[] V2(byte[] payload, bool signed = false)
        => [0xFD, (byte)payload.Length, (byte)(signed ? 1 : 0), 0, 1, 1, 1, 0, 0, 0, .. payload, 0, 0, .. (signed ? new byte[13] : [])];
}
