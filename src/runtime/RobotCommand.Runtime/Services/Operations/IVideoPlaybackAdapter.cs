using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IVideoPlaybackAdapter
{
    event EventHandler? Changed;

    VideoPlaybackStatus Status { get; }

    Task AttachAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default);

    Task DetachAsync(CancellationToken cancellationToken = default);
}
