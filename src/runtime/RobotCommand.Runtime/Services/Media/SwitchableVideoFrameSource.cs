using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class SwitchableVideoFrameSource : IVideoFrameSource
{
    private readonly object _gate = new();
    private IVideoFrameSource? _source;

    public event EventHandler? FrameAvailable;

    public VideoFrameInfo? LatestInfo
    {
        get
        {
            lock (_gate)
            {
                return _source?.LatestInfo;
            }
        }
    }

    public void SetSource(IVideoFrameSource? source)
    {
        IVideoFrameSource? previous;
        lock (_gate)
        {
            if (ReferenceEquals(_source, source))
            {
                return;
            }

            previous = _source;
            _source = source;
        }

        if (previous is not null)
        {
            previous.FrameAvailable -= OnFrameAvailable;
        }
        if (source is not null)
        {
            source.FrameAvailable += OnFrameAvailable;
        }

        FrameAvailable?.Invoke(this, EventArgs.Empty);
    }

    public bool TryCopyLatest(byte[] destination, out VideoFrameInfo? info)
    {
        IVideoFrameSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is null)
        {
            info = null;
            return false;
        }

        return source.TryCopyLatest(destination, out info);
    }

    private void OnFrameAvailable(object? sender, EventArgs e)
        => FrameAvailable?.Invoke(this, EventArgs.Empty);
}
