using RobotCommand.Services.Mavlink;
using Xunit;

namespace RobotCommand.Tests.Mavlink;

public sealed class MavlinkCodecTests
{
    [Fact]
    public void ParameterMessages_EncodeAndDecode()
    {
        var codec = new MavlinkSharpCodec();

        var requests = new[]
        {
            codec.EncodeParameterRequestList(255, 190, 1, 1),
            codec.EncodeParameterRequestRead(255, 190, 1, 1, "MPC_XY_VEL_MAX", 4),
            codec.EncodeParameterSet(255, 190, 1, 1, "MPC_XY_VEL_MAX", 12.5f, 9)
        };
        var expected = new[]
        {
            MavlinkMessageIds.ParamRequestList,
            MavlinkMessageIds.ParamRequestRead,
            MavlinkMessageIds.ParamSet
        };

        for (var index = 0; index < requests.Length; index++)
        {
            Assert.True(codec.TryDecode(requests[index], DateTimeOffset.UtcNow, out var packet, out var error), error);
            Assert.NotNull(packet);
            Assert.Equal(expected[index], packet!.MessageId);
        }
    }
    [Fact]
    public void Heartbeat_RoundTripsAsMavlink2()
    {
        var codec = new MavlinkSharpCodec();

        var bytes = codec.EncodeHeartbeat(255, 190);

        Assert.True(codec.TryDecode(
            bytes,
            DateTimeOffset.UtcNow,
            out var packet,
            out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(2, packet.ProtocolVersion);
        Assert.Equal((byte)255, packet.SystemId);
        Assert.Equal((byte)190, packet.ComponentId);
        Assert.Equal(MavlinkMessageIds.Heartbeat, packet.MessageId);
        Assert.Equal(MavlinkValues.MavTypeGcs, packet.Byte("type"));
    }

    [Fact]
    public void CorruptFrame_IsRejectedByCrcValidation()
    {
        var codec = new MavlinkSharpCodec();
        var bytes = codec.EncodeHeartbeat(255, 190);
        bytes[^1] ^= 0xff;

        Assert.False(codec.TryDecode(
            bytes,
            DateTimeOffset.UtcNow,
            out var packet,
            out _));
        Assert.Null(packet);
    }

    [Fact]
    public void Ping_RoundTripsTargetAndTimestamp()
    {
        var codec = new MavlinkSharpCodec();

        var bytes = codec.EncodePing(255, 190, 1_234_567, 42, 1, 1);

        Assert.True(codec.TryDecode(
            bytes,
            DateTimeOffset.UtcNow,
            out var packet,
            out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.Ping, packet.MessageId);
        Assert.Equal((ulong)1_234_567, packet.UInt64("time_usec"));
        Assert.Equal((uint)42, packet.UInt32("seq"));
        Assert.Equal((byte)1, packet.Byte("target_system"));
        Assert.Equal((byte)1, packet.Byte("target_component"));
    }

    [Fact]
    public void ManualControl_RoundTripsNormalisedAxes()
    {
        var codec = new MavlinkSharpCodec();

        var bytes = codec.EncodeManualControl(255, 190, 1, 500, -250, 500, 750);

        Assert.True(codec.TryDecode(
            bytes,
            DateTimeOffset.UtcNow,
            out var packet,
            out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.ManualControl, packet.MessageId);
        Assert.Equal((byte)1, packet.Byte("target"));
        Assert.Equal((short)500, Convert.ToInt16(packet.Fields["x"]));
        Assert.Equal((short)-250, Convert.ToInt16(packet.Fields["y"]));
        Assert.Equal((short)500, Convert.ToInt16(packet.Fields["z"]));
        Assert.Equal((short)750, Convert.ToInt16(packet.Fields["r"]));
    }

    [Fact]
    public void OffboardLocalNedSetpoint_EncodesPositionVelocityAndIgnoresYaw()
    {
        var codec = new MavlinkSharpCodec();
        const ushort expectedMask = 1 << 6 | 1 << 7 | 1 << 8 | 1 << 10 | 1 << 11;
        var bytes = codec.EncodeSetPositionTargetLocalNed(255, 190, 1, 1, 1234, 1, expectedMask, 12, -3, -20, 4, 1, -2);

        Assert.True(codec.TryDecode(bytes, DateTimeOffset.UtcNow, out var packet, out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.SetPositionTargetLocalNed, packet!.MessageId);
        Assert.Equal(expectedMask, packet.UInt16("type_mask"));
        Assert.Equal(12f, packet.Single("x"));
        Assert.Equal(-3f, packet.Single("y"));
        Assert.Equal(-20f, packet.Single("z"));
        Assert.Equal(4f, packet.Single("vx"));
        Assert.Equal(-2f, packet.Single("vz"));
    }

    [Fact]
    public void MissionTransferMessages_RoundTripWithMissionType()
    {
        var codec = new MavlinkSharpCodec();
        var messages = new[]
        {
            codec.EncodeMissionCount(255, 190, 1, 1, 3),
            codec.EncodeMissionSetCurrent(255, 190, 1, 1, 0),
            codec.EncodeMissionItemInt(255, 190, 1, 1,
                new MavlinkMissionItem(1, 16, 6, 473977422, 85455941, 20)),
            codec.EncodeMissionRequestInt(255, 190, 1, 1, 1),
            codec.EncodeMissionAck(255, 190, 1, 1, 0)
        };
        var expected = new[]
        {
            MavlinkMessageIds.MissionCount,
            MavlinkMessageIds.MissionSetCurrent,
            MavlinkMessageIds.MissionItemInt,
            MavlinkMessageIds.MissionRequestInt,
            MavlinkMessageIds.MissionAck
        };

        for (var index = 0; index < messages.Length; index++)
        {
            Assert.True(codec.TryDecode(messages[index], DateTimeOffset.UtcNow, out var packet, out var error), error);
            Assert.NotNull(packet);
            Assert.Equal(expected[index], packet!.MessageId);
            Assert.Equal((byte)0, packet.Byte("mission_type"));
        }
    }

    [Fact]
    public void MissionTransferMessages_EncodeFenceMissionTypeOnWire()
    {
        var codec = new MavlinkSharpCodec();
        var messages = new[]
        {
            codec.EncodeMissionCount(255, 190, 1, 1, 1, 1),
            codec.EncodeMissionRequestList(255, 190, 1, 1, 1),
            codec.EncodeMissionRequestInt(255, 190, 1, 1, 0, 1),
            codec.EncodeMissionAck(255, 190, 1, 1, 0, 1)
        };

        foreach (var bytes in messages)
        {
            Assert.Equal((byte)0xFD, bytes[0]);
            Assert.True(codec.TryDecode(bytes, DateTimeOffset.UtcNow, out var packet, out var error), error);
            Assert.NotNull(packet);
            Assert.Equal((byte)1, packet!.Byte("mission_type"));
        }
    }

    [Fact]
    public void Decode_AcceptsPx4MissionAckWithMavlink2ExtensionFields()
    {
        var codec = new MavlinkSharpCodec();
        // Captured from PX4 SITL: MAV_MISSION_ACCEPTED plus MAVLink 2 mission
        // extensions that are newer than MavLinkSharp 1.8's Common dialect.
        var bytes = Convert.FromHexString("FD080000A201012F0000FFBE000075AA79C56856");

        Assert.True(codec.TryDecode(bytes, DateTimeOffset.UtcNow, out var packet, out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.MissionAck, packet!.MessageId);
        Assert.Equal((byte)0, packet.Byte("type"));
        Assert.Equal((byte)0, packet.Byte("mission_type"));
    }

    [Fact]
    public void Decode_AcceptsPx4MissionCountWithMavlink2ExtensionFields()
    {
        var codec = new MavlinkSharpCodec();
        // Captured from PX4 SITL during a mission-list request. Its newer
        // MAVLink 2 extension layout is not represented in MavLinkSharp 1.8.
        var bytes = Convert.FromHexString("FD0900005001012C0000000004030000040305E010");

        Assert.True(codec.TryDecode(bytes, DateTimeOffset.UtcNow, out var packet, out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.MissionCount, packet!.MessageId);
        Assert.Equal((ushort)0, packet.UInt16("count"));
        Assert.Equal((byte)0, packet.Byte("mission_type"));
    }

    [Fact]
    public void Decode_AcceptsLivePx4MissionCountWithOpaqueIdExtension()
    {
        var codec = new MavlinkSharpCodec();
        var bytes = Convert.FromHexString("FD090000DD01012C00000800FFBE00D663FD609BDC");

        Assert.True(codec.TryDecode(bytes, DateTimeOffset.UtcNow, out var packet, out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.MissionCount, packet!.MessageId);
        Assert.Equal((ushort)8, packet.UInt16("count"));
        Assert.Equal((byte)0, packet.Byte("mission_type"));
    }

    [Fact]
    public void SetMode_RoundTripsPositionModeWithoutCommandAckProtocol()
    {
        var codec = new MavlinkSharpCodec();

        var bytes = codec.EncodeSetMode(
            255,
            190,
            1,
            MavlinkValues.MavModeFlagCustomModeEnabled,
            MavlinkValues.Px4PositionCustomMode);

        Assert.True(codec.TryDecode(bytes, DateTimeOffset.UtcNow, out var packet, out var error), error);
        Assert.NotNull(packet);
        Assert.Equal(MavlinkMessageIds.SetMode, packet.MessageId);
        Assert.Equal((byte)1, packet.Byte("target_system"));
        Assert.Equal(MavlinkValues.MavModeFlagCustomModeEnabled, packet.Byte("base_mode"));
        Assert.Equal(MavlinkValues.Px4PositionCustomMode, packet.UInt32("custom_mode"));
    }
}
