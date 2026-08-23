namespace RobotCommand.Models;

public enum MediaMtxRuntimeState
{
    Unknown,
    Available,
    Missing,
    Faulted
}

public sealed record MediaMtxRuntimeDiagnostics(
    MediaMtxRuntimeState State,
    string Summary,
    string Detail,
    string? ExecutablePath = null,
    string? Version = null)
{
    public static MediaMtxRuntimeDiagnostics Unknown { get; } = new(
        MediaMtxRuntimeState.Unknown,
        "Playback server not inspected",
        "Runtime status is available in Settings.");

    public bool Available => State == MediaMtxRuntimeState.Available;
}

