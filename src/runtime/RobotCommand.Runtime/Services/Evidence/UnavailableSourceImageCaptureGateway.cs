using RobotCommand.Models;

namespace RobotCommand.Services.Evidence;

public sealed class UnavailableSourceImageCaptureGateway : ISourceImageCaptureGateway
{
    private const string Message =
        "The installed Psycraft.Logos.Api.Sdk package does not expose an authoritative still-image capture operation. " +
        "Robot Command will not substitute a decoded video frame and label it as a source-camera image.";

    public SourceImageCaptureStatus Status { get; } = SourceImageCaptureStatus.Unavailable(Message);

    public Task<SourceImageCaptureResult> CaptureAsync(
        SourceImageCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SourceImageCaptureResult(false, Message));
    }
}
