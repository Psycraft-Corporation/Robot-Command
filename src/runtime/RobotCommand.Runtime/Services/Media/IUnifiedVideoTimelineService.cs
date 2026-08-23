using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IUnifiedVideoTimelineService : IAsyncDisposable, IDisposable
{
    event EventHandler? Changed;

    LocalVideoRecorderStatus LocalStatus { get; }

    RemoteVideoRecordingStatus RemoteStatus { get; }

    UnifiedVideoTimelineSnapshot Timeline { get; }

    IVideoFrameSource PresentationFrames { get; }

    Task BeginSessionAsync(
        CameraStreamRecord stream,
        ConnectionDefinition? connection,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task PlaySelectedAsync(double position, CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task GoLiveAsync(CancellationToken cancellationToken = default);

    Task RetainSelectedAsync(double position, CancellationToken cancellationToken = default);

    Task DownloadSelectedAsync(double position, CancellationToken cancellationToken = default);
}
