using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IGStreamerRuntime
{
    GStreamerRuntimeDiagnostics Diagnostics { get; }

    Task<GStreamerRuntimeDiagnostics> InspectAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}
