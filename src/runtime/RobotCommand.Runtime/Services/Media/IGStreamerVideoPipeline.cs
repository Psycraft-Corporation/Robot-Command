using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IGStreamerVideoPipeline : IAsyncDisposable, IDisposable
{
    event EventHandler? Changed;

    NativeVideoPipelineStatus Status { get; }

    IVideoFrameSource Frames { get; }

    Task StartTestPatternAsync(
        GStreamerTestSourceOptions options,
        CancellationToken cancellationToken = default);

    Task StartFileAsync(
        string path,
        GStreamerRawVideoOptions output,
        CancellationToken cancellationToken = default);

    Task StartRtspAsync(
        RtspPlaybackOptions options,
        CancellationToken cancellationToken = default);

    Task StartSrtAsync(
        SrtPlaybackOptions options,
        CancellationToken cancellationToken = default);

    Task StartHlsAsync(
        HlsPlaybackOptions options,
        CancellationToken cancellationToken = default);

    Task StartWhepAsync(
        WhepPlaybackOptions options,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
