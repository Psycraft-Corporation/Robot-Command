using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface ILocalVideoRecordingService : IAsyncDisposable, IDisposable
{
    event EventHandler? Changed;

    LocalVideoRecorderStatus Status { get; }

    LocalVideoTimelineSnapshot Timeline { get; }

    IVideoFrameSource PresentationFrames { get; }

    Task BeginSessionAsync(
        CameraStreamRecord stream,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task PlaySelectedAsync(double position, CancellationToken cancellationToken = default);

    Task PlaySegmentAsync(string segmentId, double position, CancellationToken cancellationToken = default);

    Task PlayExternalAsync(
        string path,
        string itemId,
        int width,
        int height,
        int frameRate,
        double position,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task GoLiveAsync(CancellationToken cancellationToken = default);

    Task RetainSelectedAsync(double position, CancellationToken cancellationToken = default);

    Task RetainSegmentAsync(string segmentId, CancellationToken cancellationToken = default);
}
