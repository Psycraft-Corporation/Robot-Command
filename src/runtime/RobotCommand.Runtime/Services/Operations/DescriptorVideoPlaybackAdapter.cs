using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public sealed class DescriptorVideoPlaybackAdapter : IVideoPlaybackAdapter
{
    public event EventHandler? Changed;

    public VideoPlaybackStatus Status { get; private set; } = VideoPlaybackStatus.Detached;

    public Task AttachAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Status = new VideoPlaybackStatus(
            VideoPlaybackState.AdapterUnavailable,
            "Stream session negotiated",
            "The Logos stream descriptor is available, but no WebRTC/RTSP/MJPEG playback adapter is installed in this framework drop.",
            stream.StreamId,
            stream.Protocol,
            string.IsNullOrWhiteSpace(stream.StreamUrl) ? null : stream.StreamUrl);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task DetachAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Status = VideoPlaybackStatus.Detached;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
