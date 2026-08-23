namespace RobotCommand.Models;

public enum GeometryRegistryWatchStatusKind
{
    Stopped,
    Starting,
    Connecting,
    Live,
    Stale,
    BackingOff,
    Unsupported,
    Faulted
}

public sealed record GeometryRegistryWatchState(
    string ConnectionId,
    GeometryRegistryWatchStatusKind Status,
    string Summary,
    DateTimeOffset? LastMessageAt,
    int RestartCount,
    string? LastError,
    DateTimeOffset UpdatedAt)
{
    public bool IsLive => Status == GeometryRegistryWatchStatusKind.Live;

    public bool IsTerminal => Status is
        GeometryRegistryWatchStatusKind.Unsupported or
        GeometryRegistryWatchStatusKind.Faulted;

    public static GeometryRegistryWatchState Stopped(
        string connectionId,
        string summary = "Geometry registry supervision is stopped.")
        => new(
            connectionId,
            GeometryRegistryWatchStatusKind.Stopped,
            summary,
            null,
            0,
            null,
            DateTimeOffset.UtcNow);

    public static GeometryRegistryWatchState Starting(string connectionId)
        => new(
            connectionId,
            GeometryRegistryWatchStatusKind.Starting,
            "Starting geometry registry supervision.",
            null,
            0,
            null,
            DateTimeOffset.UtcNow);
}
