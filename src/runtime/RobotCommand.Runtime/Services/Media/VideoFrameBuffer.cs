using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class VideoFrameBuffer : IVideoFrameSource
{
    private readonly object _gate = new();
    private byte[] _pixels = [];
    private VideoFrameInfo? _latestInfo;
    private long _sequence;

    public event EventHandler? FrameAvailable;

    public VideoFrameInfo? LatestInfo
    {
        get
        {
            lock (_gate)
            {
                return _latestInfo;
            }
        }
    }

    public void Publish(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        DateTimeOffset timestamp)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (stride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        var requiredBytes = checked(stride * height);
        if (pixels.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"A {width}x{height} BGRA frame with stride {stride} requires {requiredBytes} bytes.",
                nameof(pixels));
        }

        lock (_gate)
        {
            if (_pixels.Length != requiredBytes)
            {
                _pixels = new byte[requiredBytes];
            }

            pixels[..requiredBytes].CopyTo(_pixels);
            _latestInfo = new VideoFrameInfo(
                width,
                height,
                stride,
                ++_sequence,
                timestamp);
        }

        FrameAvailable?.Invoke(this, EventArgs.Empty);
    }

    public bool TryCopyLatest(byte[] destination, out VideoFrameInfo? info)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (_gate)
        {
            info = _latestInfo;
            if (info is null || destination.Length < info.RequiredBytes)
            {
                return false;
            }

            Buffer.BlockCopy(_pixels, 0, destination, 0, info.RequiredBytes);
            return true;
        }
    }

    public void Clear()
    {
        var changed = false;
        lock (_gate)
        {
            if (_latestInfo is not null)
            {
                _latestInfo = null;
                changed = true;
            }
        }

        if (changed)
        {
            FrameAvailable?.Invoke(this, EventArgs.Empty);
        }
    }
}
