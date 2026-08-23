using RobotCommand.Models;

namespace RobotCommand.Services.Evidence;

public interface ISourceImageCaptureGateway
{
    SourceImageCaptureStatus Status { get; }

    Task<SourceImageCaptureResult> CaptureAsync(
        SourceImageCaptureRequest request,
        CancellationToken cancellationToken = default);
}
