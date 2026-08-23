using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IVideoProtocolPolicy
{
    IReadOnlyList<VideoProtocolPreference> BuildPlan(
        ConnectionDefinition? connection,
        VideoProtocolPreference requested,
        GStreamerRuntimeDiagnostics diagnostics);
}
