using MavLinkSharp;
using MavLinkSharp.Enums;
using MavLinkSharp.Protocols;

namespace RobotCommand.Services.Mavlink;

public sealed record MavlinkPacket(
    byte ProtocolVersion,
    byte Sequence,
    byte SystemId,
    byte ComponentId,
    uint MessageId,
    IReadOnlyDictionary<string, object> Fields,
    DateTimeOffset ReceivedAt)
{
    public byte Byte(string name, byte fallback = 0)
        => ConvertValue(name, Convert.ToByte, fallback);

    public ushort UInt16(string name, ushort fallback = 0)
        => ConvertValue(name, Convert.ToUInt16, fallback);

    public uint UInt32(string name, uint fallback = 0)
        => ConvertValue(name, Convert.ToUInt32, fallback);

    public ulong UInt64(string name, ulong fallback = 0)
        => ConvertValue(name, Convert.ToUInt64, fallback);

    public int Int32(string name, int fallback = 0)
        => ConvertValue(name, Convert.ToInt32, fallback);

    public short Int16(string name, short fallback = 0)
        => ConvertValue(name, Convert.ToInt16, fallback);

    public sbyte Int8(string name, sbyte fallback = 0)
        => ConvertValue(name, Convert.ToSByte, fallback);

    public float Single(string name, float fallback = 0)
        => ConvertValue(name, Convert.ToSingle, fallback);

    public string Text(string name)
    {
        if (!Fields.TryGetValue(name, out var value) || value is null) return string.Empty;
        return value switch
        {
            string text => text.TrimEnd('\0').Trim(),
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim(),
            IEnumerable<byte> bytes => System.Text.Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim(),
            char[] chars => new string(chars).TrimEnd('\0').Trim(),
            IEnumerable<char> chars => new string(chars.ToArray()).TrimEnd('\0').Trim(),
            _ => value.ToString()?.TrimEnd('\0').Trim() ?? string.Empty
        };
    }

    private T ConvertValue<T>(string name, Func<object, T> convert, T fallback)
    {
        if (!Fields.TryGetValue(name, out var value) || value is null)
        {
            return fallback;
        }

        try
        {
            return convert(value);
        }
        catch (Exception) when (value is IConvertible)
        {
            return fallback;
        }
    }
}

public interface IMavlinkCodec
{
    bool TryDecode(
        ReadOnlySpan<byte> bytes,
        DateTimeOffset receivedAt,
        out MavlinkPacket? packet,
        out string? error);

    byte[] EncodeHeartbeat(byte sourceSystemId, byte sourceComponentId);

    byte[] EncodePing(
        byte sourceSystemId,
        byte sourceComponentId,
        ulong timeUsec,
        uint sequence,
        byte targetSystemId,
        byte targetComponentId);

    byte[] EncodeCommandLong(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        ushort command,
        ReadOnlySpan<float> parameters,
        byte confirmation = 0);

    byte[] EncodeCommandInt(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        byte frame,
        ushort command,
        ReadOnlySpan<float> parameters,
        int x,
        int y,
        float z,
        byte confirmation = 0);

    /// <summary>
    /// Encodes MAVLink's standard mode-selection message. Autopilot adapters
    /// use this for built-in manual and assisted modes where appropriate; it
    /// is not a COMMAND_LONG request and consequently has no COMMAND_ACK.
    /// </summary>
    byte[] EncodeSetMode(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte baseMode,
        uint customMode);

    /// <summary>
    /// Encodes the standard MAVLink manual-control message used by supported
    /// autopilot adapters. This is deliberately not an Offboard setpoint; the
    /// autopilot retains responsibility for stabilization and position hold.
    /// </summary>
    byte[] EncodeManualControl(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        short x,
        short y,
        short z,
        short r,
        ushort buttons = 0);

    /// <summary>
    /// Encodes a local-NED position/velocity setpoint for PX4 Offboard
    /// control. Callers explicitly select which dimensions are ignored using
    /// the MAVLink POSITION_TARGET_TYPEMASK bitmap.
    /// </summary>
    byte[] EncodeSetPositionTargetLocalNed(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        uint timeBootMilliseconds,
        byte coordinateFrame,
        ushort typeMask,
        float north,
        float east,
        float down,
        float velocityNorth,
        float velocityEast,
        float velocityDown);

    /// <summary>
    /// Encodes a global WGS84 position/velocity target for ArduPilot Guided
    /// control. Callers explicitly select ignored dimensions with the
    /// POSITION_TARGET_TYPEMASK bitmap.
    /// </summary>
    byte[] EncodeSetPositionTargetGlobalInt(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        uint timeBootMilliseconds,
        byte coordinateFrame,
        ushort typeMask,
        int latitudeE7,
        int longitudeE7,
        float altitude,
        float velocityNorth,
        float velocityEast,
        float velocityDown);

    byte[] EncodeParameterRequestList(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId);

    byte[] EncodeParameterRequestRead(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        string parameterName,
        short parameterIndex);

    byte[] EncodeParameterSet(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        string parameterName,
        float value,
        byte parameterType);

    byte[] EncodeMissionCount(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort count, byte missionType = 0);
    byte[] EncodeMissionSetCurrent(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort sequence);
    byte[] EncodeMissionClearAll(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte missionType = 0);
    byte[] EncodeMissionItem(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, MavlinkMissionItem item)
        => EncodeMissionItemInt(sourceSystemId, sourceComponentId, targetSystemId, targetComponentId, item);
    byte[] EncodeMissionItemInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, MavlinkMissionItem item);
    byte[] EncodeMissionRequestList(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte missionType = 0);
    byte[] EncodeMissionRequestInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort sequence, byte missionType = 0);
    byte[] EncodeMissionAck(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte result, byte missionType = 0);
}

public sealed class MavlinkSharpCodec : IMavlinkCodec
{
    private readonly object _gate = new();
    private readonly MavLinkContext _context = new();
    private readonly DialectType _dialect;
    private int _sequence;

    public MavlinkSharpCodec(DialectType dialect = DialectType.Common)
    {
        _dialect = dialect;
        _context.Initialize(dialect);
    }

    public bool TryDecode(
        ReadOnlySpan<byte> bytes,
        DateTimeOffset receivedAt,
        out MavlinkPacket? packet,
        out string? error)
    {
        lock (_gate)
        {
            var frame = new Frame { Context = _context };
            if (!frame.TryParse(bytes))
            {
                // MavLinkSharp 1.8 ships older Common-dialect metadata and rejects
                // MAVLink 2 mission packets when PX4 appends newer extension fields
                // (notably mission_type and opaque_id). The fixed, standard base
                // fields remain at the start of the payload, so project the mission
                // messages we use without discarding a valid transfer acknowledgement.
                // PX4's current MAVLink 2 mission messages include extension
                // fields that MavLinkSharp 1.8 does not describe. Depending on
                // the frame shape, that library reports either an invalid
                // payload length or a frame-too-short error before exposing
                // the standard mission fields. The bounded fallback below
                // validates the wire length and projects only the fields we
                // need for mission transfer.
                if ((frame.ErrorReason.ToString() is "PayloadLengthInvalid" or "FrameTooShort") &&
                    TryDecodeMissionExtensionFrame(bytes, receivedAt, out packet))
                {
                    error = null;
                    return true;
                }
                // ArduPilot may emit valid dialect-extension messages that are
                // newer than the bundled MavLinkSharp metadata. Preserve their
                // wire identity and sequence number even when no field schema
                // is available. They are intentionally ignored by ProcessPacket
                // until a schema is added, but must not become artificial packet
                // loss in the link-health calculation.
                if (_dialect == DialectType.Ardupilotmega &&
                    frame.ErrorReason.ToString() == "MessageNotFound" &&
                    TryDecodeHeaderOnlyFrame(bytes, receivedAt, out packet))
                {
                    error = null;
                    return true;
                }
                packet = null;
                error = frame.ErrorReason.ToString();
                return false;
            }

            var fields = new Dictionary<string, object>(frame.Fields, StringComparer.Ordinal);
            AddMissionTypeExtension(bytes, frame.MessageId, fields);
            packet = new MavlinkPacket(
                frame.StartMarker == Protocol.V2.StartMarker ? (byte)2 : (byte)1,
                frame.PacketSequence,
                frame.SystemId,
                frame.ComponentId,
                frame.MessageId,
                fields,
                receivedAt);
            error = null;
            return true;
        }
    }

    private static bool TryDecodeHeaderOnlyFrame(ReadOnlySpan<byte> bytes, DateTimeOffset receivedAt, out MavlinkPacket? packet)
    {
        packet = null;
        if (bytes.Length < 12 || bytes[0] != Protocol.V2.StartMarker) return false;
        var payloadLength = bytes[1];
        var signed = (bytes[2] & 0x01) != 0;
        var expectedLength = 12 + payloadLength + (signed ? 13 : 0);
        if (bytes.Length != expectedLength || expectedLength > 300) return false;

        packet = new MavlinkPacket(
            2,
            bytes[4],
            bytes[5],
            bytes[6],
            (uint)(bytes[7] | (bytes[8] << 8) | (bytes[9] << 16)),
            new Dictionary<string, object>(StringComparer.Ordinal),
            receivedAt);
        return true;
    }

    private static bool TryDecodeMissionExtensionFrame(ReadOnlySpan<byte> bytes, DateTimeOffset receivedAt, out MavlinkPacket? packet)
    {
        packet = null;
        // MAVLink 2: 10-byte header, payload, two-byte checksum and optional
        // 13-byte signature. Frames reaching this fallback already failed only
        // the library's outdated payload-length check.
        if (bytes.Length < 12 || bytes[0] != Protocol.V2.StartMarker) return false;
        var payloadLength = bytes[1];
        var signed = (bytes[2] & 0x01) != 0;
        var expectedLength = 12 + payloadLength + (signed ? 13 : 0);
        if (bytes.Length != expectedLength) return false;

        var messageId = (uint)(bytes[7] | (bytes[8] << 8) | (bytes[9] << 16));
        var payload = bytes.Slice(10, payloadLength);
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        switch (messageId)
        {
            case MavlinkMessageIds.MissionCount when payload.Length >= 4:
                fields["count"] = BitConverter.ToUInt16(payload[..2]);
                fields["target_system"] = payload[2];
                fields["target_component"] = payload[3];
                if (payload.Length >= 5) fields["mission_type"] = payload[4];
                break;
            case MavlinkMessageIds.MissionAck when payload.Length >= 3:
                fields["target_system"] = payload[0];
                fields["target_component"] = payload[1];
                fields["type"] = payload[2];
                if (payload.Length >= 4) fields["mission_type"] = payload[3];
                break;
            case MavlinkMessageIds.MissionCurrent when payload.Length >= 2:
                fields["seq"] = BitConverter.ToUInt16(payload[..2]);
                if (payload.Length >= 3) fields["total"] = payload[2];
                break;
            case MavlinkMessageIds.MissionItemReached when payload.Length >= 2:
                fields["seq"] = BitConverter.ToUInt16(payload[..2]);
                break;
            default:
                return false;
        }

        packet = new MavlinkPacket(
            2,
            bytes[4],
            bytes[5],
            bytes[6],
            messageId,
            fields,
            receivedAt);
        return true;
    }

    public byte[] EncodeHeartbeat(byte sourceSystemId, byte sourceComponentId)
    {
        lock (_gate)
        {
            var definition = _context.Metadata.MessagesDictionary[MavlinkMessageIds.Heartbeat];
            var frame = new Frame
            {
                Context = _context,
                StartMarker = Protocol.V2.StartMarker,
                SystemId = sourceSystemId,
                ComponentId = sourceComponentId,
                MessageId = definition.Id,
                Message = definition,
                PacketSequence = NextSequence()
            };
            frame.SetFields(new Dictionary<string, object>
            {
                ["custom_mode"] = 0u,
                ["type"] = MavlinkValues.MavTypeGcs,
                ["autopilot"] = MavlinkValues.MavAutopilotInvalid,
                ["base_mode"] = (byte)0,
                ["system_status"] = MavlinkValues.MavStateActive,
                ["mavlink_version"] = (byte)3
            });
            return frame.ToBytes();
        }
    }

    public byte[] EncodePing(
        byte sourceSystemId,
        byte sourceComponentId,
        ulong timeUsec,
        uint sequence,
        byte targetSystemId,
        byte targetComponentId)
    {
        lock (_gate)
        {
            var definition = _context.Metadata.MessagesDictionary[MavlinkMessageIds.Ping];
            var frame = new Frame
            {
                Context = _context,
                StartMarker = Protocol.V2.StartMarker,
                SystemId = sourceSystemId,
                ComponentId = sourceComponentId,
                MessageId = definition.Id,
                Message = definition,
                PacketSequence = NextSequence()
            };
            frame.SetFields(new Dictionary<string, object>
            {
                ["time_usec"] = timeUsec,
                ["seq"] = sequence,
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId
            });
            return frame.ToBytes();
        }
    }

    public byte[] EncodeCommandLong(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        ushort command,
        ReadOnlySpan<float> parameters,
        byte confirmation = 0)
    {
        lock (_gate)
        {
            return CommandProtocol.CreateCommandLong(
                _context,
                sourceSystemId,
                sourceComponentId,
                targetSystemId,
                targetComponentId,
                command,
                parameters,
                confirmation,
                NextSequence()).ToBytes();
        }
    }

    public byte[] EncodeCommandInt(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        byte frame,
        ushort command,
        ReadOnlySpan<float> parameters,
        int x,
        int y,
        float z,
        byte confirmation = 0)
    {
        lock (_gate)
        {
            var definition = _context.Metadata.MessagesDictionary[MavlinkMessageIds.CommandInt];
            var values = new Dictionary<string, object>
            {
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId,
                ["frame"] = frame,
                ["command"] = command,
                ["current"] = (byte)0,
                ["autocontinue"] = (byte)0,
                ["param1"] = parameters.Length > 0 ? parameters[0] : float.NaN,
                ["param2"] = parameters.Length > 1 ? parameters[1] : float.NaN,
                ["param3"] = parameters.Length > 2 ? parameters[2] : float.NaN,
                ["param4"] = parameters.Length > 3 ? parameters[3] : float.NaN,
                ["x"] = x,
                ["y"] = y,
                ["z"] = z
            };
            var frameValue = new Frame
            {
                Context = _context,
                StartMarker = Protocol.V2.StartMarker,
                SystemId = sourceSystemId,
                ComponentId = sourceComponentId,
                MessageId = definition.Id,
                Message = definition,
                PacketSequence = NextSequence()
            };
            frameValue.SetFields(values);
            return frameValue.ToBytes();
        }
    }

    public byte[] EncodeSetMode(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte baseMode,
        uint customMode)
    {
        lock (_gate)
        {
            var definition = _context.Metadata.MessagesDictionary[MavlinkMessageIds.SetMode];
            var frame = new Frame
            {
                Context = _context,
                StartMarker = Protocol.V2.StartMarker,
                SystemId = sourceSystemId,
                ComponentId = sourceComponentId,
                MessageId = definition.Id,
                Message = definition,
                PacketSequence = NextSequence()
            };
            frame.SetFields(new Dictionary<string, object>
            {
                ["target_system"] = targetSystemId,
                ["base_mode"] = baseMode,
                ["custom_mode"] = customMode
            });
            return frame.ToBytes();
        }
    }

    public byte[] EncodeManualControl(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        short x,
        short y,
        short z,
        short r,
        ushort buttons = 0)
    {
        lock (_gate)
        {
            var definition = _context.Metadata.MessagesDictionary[MavlinkMessageIds.ManualControl];
            var frame = new Frame
            {
                Context = _context,
                StartMarker = Protocol.V2.StartMarker,
                SystemId = sourceSystemId,
                ComponentId = sourceComponentId,
                MessageId = definition.Id,
                Message = definition,
                PacketSequence = NextSequence()
            };
            frame.SetFields(new Dictionary<string, object>
            {
                ["target"] = targetSystemId,
                ["x"] = x,
                ["y"] = y,
                ["z"] = z,
                ["r"] = r,
                ["buttons"] = buttons
            });
            return frame.ToBytes();
        }
    }

    public byte[] EncodeSetPositionTargetLocalNed(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        uint timeBootMilliseconds,
        byte coordinateFrame,
        ushort typeMask,
        float north,
        float east,
        float down,
        float velocityNorth,
        float velocityEast,
        float velocityDown)
        => EncodeSimpleMessage(
            MavlinkMessageIds.SetPositionTargetLocalNed,
            sourceSystemId,
            sourceComponentId,
            new Dictionary<string, object>
            {
                ["time_boot_ms"] = timeBootMilliseconds,
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId,
                ["coordinate_frame"] = coordinateFrame,
                ["type_mask"] = typeMask,
                ["x"] = north,
                ["y"] = east,
                ["z"] = down,
                ["vx"] = velocityNorth,
                ["vy"] = velocityEast,
                ["vz"] = velocityDown,
                ["afx"] = 0f,
                ["afy"] = 0f,
                ["afz"] = 0f,
                ["yaw"] = 0f,
                ["yaw_rate"] = 0f
            });

    public byte[] EncodeSetPositionTargetGlobalInt(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        uint timeBootMilliseconds,
        byte coordinateFrame,
        ushort typeMask,
        int latitudeE7,
        int longitudeE7,
        float altitude,
        float velocityNorth,
        float velocityEast,
        float velocityDown)
        => EncodeSimpleMessage(
            MavlinkMessageIds.SetPositionTargetGlobalInt,
            sourceSystemId,
            sourceComponentId,
            new Dictionary<string, object>
            {
                ["time_boot_ms"] = timeBootMilliseconds,
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId,
                ["coordinate_frame"] = coordinateFrame,
                ["type_mask"] = typeMask,
                ["lat_int"] = latitudeE7,
                ["lon_int"] = longitudeE7,
                ["alt"] = altitude,
                ["vx"] = velocityNorth,
                ["vy"] = velocityEast,
                ["vz"] = velocityDown,
                ["afx"] = 0f,
                ["afy"] = 0f,
                ["afz"] = 0f,
                ["yaw"] = 0f,
                ["yaw_rate"] = 0f
            });

    public byte[] EncodeParameterRequestList(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId)
        => EncodeSimpleMessage(
            MavlinkMessageIds.ParamRequestList,
            sourceSystemId,
            sourceComponentId,
            new Dictionary<string, object>
            {
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId
            });

    public byte[] EncodeParameterRequestRead(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        string parameterName,
        short parameterIndex)
        => EncodeSimpleMessage(
            MavlinkMessageIds.ParamRequestRead,
            sourceSystemId,
            sourceComponentId,
            new Dictionary<string, object>
            {
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId,
                ["param_id"] = EncodeParameterName(parameterName),
                ["param_index"] = parameterIndex
            });

    public byte[] EncodeParameterSet(
        byte sourceSystemId,
        byte sourceComponentId,
        byte targetSystemId,
        byte targetComponentId,
        string parameterName,
        float value,
        byte parameterType)
        => EncodeSimpleMessage(
            MavlinkMessageIds.ParamSet,
            sourceSystemId,
            sourceComponentId,
            new Dictionary<string, object>
            {
                ["target_system"] = targetSystemId,
                ["target_component"] = targetComponentId,
                ["param_id"] = EncodeParameterName(parameterName),
                ["param_value"] = value,
                ["param_type"] = parameterType
            });

    public byte[] EncodeMissionCount(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort count, byte missionType = 0)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionCount, sourceSystemId, sourceComponentId, missionType, new Dictionary<string, object>
        {
            ["target_system"] = targetSystemId,
            ["target_component"] = targetComponentId,
            ["count"] = count
        });

    public byte[] EncodeMissionSetCurrent(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort sequence)
        => EncodeSimpleMessage(MavlinkMessageIds.MissionSetCurrent, sourceSystemId, sourceComponentId, new Dictionary<string, object>
        {
            ["target_system"] = targetSystemId,
            ["target_component"] = targetComponentId,
            ["seq"] = sequence
        });

    public byte[] EncodeMissionClearAll(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte missionType = 0)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionClearAll, sourceSystemId, sourceComponentId, missionType, new Dictionary<string, object>
        { ["target_system"] = targetSystemId, ["target_component"] = targetComponentId });

    public byte[] EncodeMissionItemInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, MavlinkMissionItem item)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionItemInt, sourceSystemId, sourceComponentId, item.MissionType, new Dictionary<string, object>
        {
            ["target_system"] = targetSystemId,
            ["target_component"] = targetComponentId,
            ["seq"] = item.Sequence,
            ["frame"] = item.Frame,
            ["command"] = item.Command,
            ["current"] = item.Current ? (byte)1 : (byte)0,
            ["autocontinue"] = (byte)1,
            ["param1"] = item.Param1,
            ["param2"] = item.Param2,
            ["param3"] = item.Param3,
            ["param4"] = item.Param4,
            ["x"] = item.LatitudeE7,
            ["y"] = item.LongitudeE7,
            ["z"] = item.AltitudeMetres
        });

    public byte[] EncodeMissionItem(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, MavlinkMissionItem item)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionItem, sourceSystemId, sourceComponentId, item.MissionType, new Dictionary<string, object>
        {
            ["target_system"] = targetSystemId,
            ["target_component"] = targetComponentId,
            ["seq"] = item.Sequence,
            ["frame"] = item.Frame,
            ["command"] = item.Command,
            ["current"] = item.Current ? (byte)1 : (byte)0,
            ["autocontinue"] = (byte)1,
            ["param1"] = item.Param1,
            ["param2"] = item.Param2,
            ["param3"] = item.Param3,
            ["param4"] = item.Param4,
            ["x"] = item.LatitudeE7 / 10_000_000f,
            ["y"] = item.LongitudeE7 / 10_000_000f,
            ["z"] = item.AltitudeMetres
        });

    public byte[] EncodeMissionRequestList(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte missionType = 0)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionRequestList, sourceSystemId, sourceComponentId, missionType, new Dictionary<string, object>
        { ["target_system"] = targetSystemId, ["target_component"] = targetComponentId });

    public byte[] EncodeMissionRequestInt(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, ushort sequence, byte missionType = 0)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionRequestInt, sourceSystemId, sourceComponentId, missionType, new Dictionary<string, object>
        { ["target_system"] = targetSystemId, ["target_component"] = targetComponentId, ["seq"] = sequence });

    public byte[] EncodeMissionAck(byte sourceSystemId, byte sourceComponentId, byte targetSystemId, byte targetComponentId, byte result, byte missionType = 0)
        => EncodeMissionExtensionMessage(MavlinkMessageIds.MissionAck, sourceSystemId, sourceComponentId, missionType, new Dictionary<string, object>
        { ["target_system"] = targetSystemId, ["target_component"] = targetComponentId, ["type"] = result });

    private byte[] EncodeMissionExtensionMessage(
        uint messageId,
        byte sourceSystemId,
        byte sourceComponentId,
        byte missionType,
        IReadOnlyDictionary<string, object> fields)
    {
        var baseFrame = EncodeSimpleMessage(messageId, sourceSystemId, sourceComponentId, fields);
        if (missionType == 0) return baseFrame;

        // MavLinkSharp 1.8 knows the pre-extension Common dialect but omits
        // MAVLink 2 mission_type bytes. Append the extension after the base
        // payload and recompute the frame checksum so ArduPilot accepts fence
        // mission transfers as MAV_MISSION_TYPE_FENCE (1).
        return AppendMavlinkV2Extension(baseFrame, missionType, FindCrcExtra(baseFrame));
    }

    private static void AddMissionTypeExtension(
        ReadOnlySpan<byte> bytes,
        uint messageId,
        IDictionary<string, object> fields)
    {
        if (bytes.Length < 12 || bytes[0] != Protocol.V2.StartMarker || bytes[1] == 0) return;
        var baseLength = messageId switch
        {
            MavlinkMessageIds.MissionRequestList => 2,
            MavlinkMessageIds.MissionCount => 4,
            MavlinkMessageIds.MissionClearAll => 2,
            MavlinkMessageIds.MissionAck => 3,
            MavlinkMessageIds.MissionRequestInt => 4,
            MavlinkMessageIds.MissionItem or MavlinkMessageIds.MissionItemInt => 37,
            _ => -1
        };
        if (baseLength < 0 || bytes[1] <= baseLength) return;

        // MAVLink 2 extensions are serialized after the base payload. The
        // mission_type extension is the final byte for these messages. A zero
        // extension is normally trimmed from the wire, so only replace the
        // parser's default when the payload actually carries the extension.
        var extension = bytes[10 + bytes[1] - 1];
        fields["mission_type"] = extension;
    }

    private static byte[] AppendMavlinkV2Extension(byte[] frame, byte value, byte crcExtra)
    {
        if (frame.Length < 12 || frame[0] != Protocol.V2.StartMarker || (frame[2] & 0x01) != 0)
            throw new InvalidOperationException("Only unsigned MAVLink 2 frames can carry mission extensions.");

        var payloadLength = frame[1];
        if (frame.Length != 12 + payloadLength || payloadLength == byte.MaxValue)
            throw new InvalidOperationException("The MAVLink frame has an invalid payload length.");

        var result = new byte[frame.Length + 1];
        Buffer.BlockCopy(frame, 0, result, 0, 10 + payloadLength);
        result[1] = (byte)(payloadLength + 1);
        result[10 + payloadLength] = value;
        var checksum = CalculateMavlinkCrc(result.AsSpan(1, 9 + result[1]), crcExtra);
        result[^2] = (byte)(checksum & 0xFF);
        result[^1] = (byte)(checksum >> 8);
        return result;
    }

    private static byte FindCrcExtra(ReadOnlySpan<byte> frame)
    {
        var expected = (ushort)(frame[^2] | (frame[^1] << 8));
        var headerAndPayload = frame.Slice(1, 9 + frame[1]);
        for (var candidate = 0; candidate <= byte.MaxValue; candidate++)
        {
            if (CalculateMavlinkCrc(headerAndPayload, (byte)candidate) == expected)
                return (byte)candidate;
        }

        throw new InvalidOperationException("Could not determine the MAVLink message checksum extra.");
    }

    private static ushort CalculateMavlinkCrc(ReadOnlySpan<byte> bytes, byte crcExtra)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes) crc = AccumulateMavlinkCrc(value, crc);
        return AccumulateMavlinkCrc(crcExtra, crc);
    }

    private static ushort AccumulateMavlinkCrc(byte value, ushort crc)
    {
        var tmp = (byte)(value ^ (byte)(crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    private byte[] EncodeSimpleMessage(
        uint messageId,
        byte sourceSystemId,
        byte sourceComponentId,
        IReadOnlyDictionary<string, object> fields)
    {
        lock (_gate)
        {
            var definition = _context.Metadata.MessagesDictionary[messageId];
            var frame = new Frame
            {
                Context = _context,
                StartMarker = Protocol.V2.StartMarker,
                SystemId = sourceSystemId,
                ComponentId = sourceComponentId,
                MessageId = definition.Id,
                Message = definition,
                PacketSequence = NextSequence()
            };
            frame.SetFields(new Dictionary<string, object>(fields));
            return frame.ToBytes();
        }
    }

    private static char[] EncodeParameterName(string parameterName)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(parameterName);
        if (bytes.Length > 16)
        {
            throw new ArgumentException("MAVLink parameter names must be at most 16 ASCII bytes.", nameof(parameterName));
        }

        return parameterName.PadRight(16, '\0').ToCharArray();
    }

    private byte NextSequence()
        => unchecked((byte)Interlocked.Increment(ref _sequence));
}

public static class MavlinkMessageIds
{
    public const uint Heartbeat = 0;
    public const uint SystemStatus = 1;
    public const uint Ping = 4;
    public const uint SetMode = 11;
    public const uint ParamRequestRead = 20;
    public const uint ParamRequestList = 21;
    public const uint ParamValue = 22;
    public const uint ParamSet = 23;
    public const uint RcChannels = 65;
    public const uint GpsRawInt = 24;
    public const uint Attitude = 30;
    public const uint LocalPositionNed = 32;
    public const uint GlobalPositionInt = 33;
    public const uint HomePosition = 242;
    public const uint ManualControl = 69;
    public const uint SetPositionTargetLocalNed = 84;
    public const uint SetPositionTargetGlobalInt = 86;
    public const uint CommandAck = 77;
    public const uint CommandInt = 75;
    public const uint MissionItem = 39;
    public const uint MissionRequest = 40;
    public const uint MissionSetCurrent = 41;
    public const uint MissionClearAll = 45;
    public const uint MissionCurrent = 42;
    public const uint MissionRequestList = 43;
    public const uint MissionCount = 44;
    public const uint MissionItemReached = 46;
    public const uint MissionAck = 47;
    public const uint MissionRequestInt = 51;
    public const uint MissionItemInt = 73;
    public const uint RadioStatus = 109;
    public const uint BatteryStatus = 147;
    public const uint AutopilotVersion = 148;
    public const uint EstimatorStatus = 230;
    public const uint Vibration = 241;
    public const uint ExtendedSystemState = 245;
    public const uint StatusText = 253;
}

public static class MavlinkCommandIds
{
    public const ushort ConditionYaw = 115;
    public const ushort NavReturnToLaunch = 20;
    public const ushort NavLand = 21;
    public const ushort NavTakeoff = 22;
    public const ushort NavLoiterTime = 19;
    public const ushort DoChangeSpeed = 178;
    public const ushort DoSetMode = 176;
    public const ushort DoReposition = 192;
    public const ushort MissionStart = 300;
    public const ushort ComponentArmDisarm = 400;
    public const ushort SetMessageInterval = 511;
}

public static class MavlinkValues
{
    public const byte MavTypeGcs = 6;
    public const byte MavTypeOnboardController = 18;
    public const byte MavAutopilotInvalid = 8;
    public const byte MavAutopilotPx4 = 12;
    public const byte MavAutopilotArduPilot = 3;
    public const byte MavStateActive = 4;
    public const byte MavModeFlagCustomModeEnabled = 1;
    public const byte MavModeFlagManualInputEnabled = 64;
    public const byte MavModeFlagSafetyArmed = 128;
    public const byte MavCompIdAutopilot1 = 1;
    public const byte MavLandedStateOnGround = 1;
    public const byte MavLandedStateInAir = 2;

    public const byte MavResultAccepted = 0;
    public const byte MavResultTemporarilyRejected = 1;
    public const byte MavResultDenied = 2;
    public const byte MavResultUnsupported = 3;
    public const byte MavResultFailed = 4;
    public const byte MavResultInProgress = 5;
    public const byte MavResultCancelled = 6;

    // PX4 custom mode union: main mode in byte 2, sub-mode in byte 3.
    public const uint Px4PositionCustomMode = 3u << 16;
    public const uint Px4OffboardCustomMode = 6u << 16;
    public const uint Px4AutoLoiterCustomMode = (4u << 16) | (3u << 24);
    public const uint Px4AutoMissionCustomMode = (4u << 16) | (4u << 24);
    public const uint ArduPilotAutoCustomMode = 3;
    public const uint ArduPilotStabilizeCustomMode = 0;
    public const uint ArduPilotLoiterCustomMode = 5;
    public const uint ArduPilotLandCustomMode = 9;
    public const uint ArduPilotBrakeCustomMode = 17;
    public const byte MavFrameGlobalRelativeAltInt = 6;
}
