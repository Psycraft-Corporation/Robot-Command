using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IMediaMtxRuntime
{
    MediaMtxRuntimeDiagnostics Diagnostics { get; }

    Task<MediaMtxRuntimeDiagnostics> InspectAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}

