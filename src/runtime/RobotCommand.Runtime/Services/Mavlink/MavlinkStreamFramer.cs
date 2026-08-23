namespace RobotCommand.Services.Mavlink;

public sealed class MavlinkStreamFramer
{
    private const int MaximumBufferedBytes = 64 * 1024;
    private readonly List<byte> _buffer = [];

    public long FramesProduced { get; private set; }
    public long DiscardedBytes { get; private set; }
    public long FramingErrors { get; private set; }

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.IsEmpty) _buffer.AddRange(bytes.ToArray());
        if (_buffer.Count > MaximumBufferedBytes)
        {
            var remove = _buffer.Count - MaximumBufferedBytes;
            _buffer.RemoveRange(0, remove);
            DiscardedBytes += remove;
            FramingErrors++;
        }

        var frames = new List<byte[]>();
        while (true)
        {
            var markerIndex = FindMarker();
            if (markerIndex < 0)
            {
                DiscardedBytes += _buffer.Count;
                _buffer.Clear();
                break;
            }
            if (markerIndex > 0)
            {
                _buffer.RemoveRange(0, markerIndex);
                DiscardedBytes += markerIndex;
            }
            if (_buffer.Count < 2) break;

            var isV2 = _buffer[0] == 0xFD;
            var payloadLength = _buffer[1];
            var frameLength = isV2
                ? 12 + payloadLength + (_buffer.Count >= 3 && (_buffer[2] & 0x01) != 0 ? 13 : 0)
                : 8 + payloadLength;
            if (frameLength is < 8 or > 300)
            {
                _buffer.RemoveAt(0);
                DiscardedBytes++;
                FramingErrors++;
                continue;
            }
            if (_buffer.Count < frameLength) break;
            frames.Add(_buffer.GetRange(0, frameLength).ToArray());
            _buffer.RemoveRange(0, frameLength);
            FramesProduced++;
        }
        return frames;
    }

    public void Reset() => _buffer.Clear();

    private int FindMarker()
    {
        for (var index = 0; index < _buffer.Count; index++)
        {
            if (_buffer[index] is 0xFE or 0xFD) return index;
        }
        return -1;
    }
}
