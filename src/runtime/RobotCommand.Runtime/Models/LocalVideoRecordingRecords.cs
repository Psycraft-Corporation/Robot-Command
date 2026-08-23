namespace RobotCommand.Models;

public enum LocalVideoRecorderState
{
    Disabled,
    Stopped,
    WaitingForFrame,
    Starting,
    Recording,
    Stopping,
    Faulted
}

public sealed record LocalVideoRecorderStatus(
    LocalVideoRecorderState State,
    string Summary,
    string Detail,
    DateTimeOffset UpdatedAt,
    string? SessionId = null,
    string? Directory = null,
    string? LastError = null)
{
    public static LocalVideoRecorderStatus Stopped { get; } = new(
        LocalVideoRecorderState.Stopped,
        "Local recording stopped",
        "Open a camera stream to start the configured console-side rolling buffer.",
        DateTimeOffset.UtcNow);

    public bool Active => State is
        LocalVideoRecorderState.WaitingForFrame or
        LocalVideoRecorderState.Starting or
        LocalVideoRecorderState.Recording;
}

public enum LocalVideoTimelineMode
{
    Live,
    Playback,
    Paused
}

public sealed record LocalVideoSegment(
    string Id,
    string SessionId,
    string Path,
    string CameraSourceId,
    string StreamId,
    string Protocol,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int Width,
    int Height,
    int FrameRate,
    long SizeBytes,
    bool Retained,
    bool Finalized,
    bool GapBefore)
{
    public TimeSpan Duration => EndedAt > StartedAt
        ? EndedAt - StartedAt
        : TimeSpan.Zero;
}

public sealed record LocalVideoTimelineSnapshot(
    IReadOnlyList<LocalVideoSegment> Segments,
    DateTimeOffset? RangeStart,
    DateTimeOffset? RangeEnd,
    DateTimeOffset LiveEdge,
    LocalVideoTimelineMode Mode,
    string? ActiveSegmentId,
    double Position,
    long TotalBytes,
    DateTimeOffset UpdatedAt)
{
    public static LocalVideoTimelineSnapshot Empty { get; } = new(
        [],
        null,
        null,
        DateTimeOffset.UtcNow,
        LocalVideoTimelineMode.Live,
        null,
        1,
        0,
        DateTimeOffset.UtcNow);

    public bool HasSegments => Segments.Count > 0;

    public LocalVideoSegment? SegmentAt(double position)
    {
        if (Segments.Count == 0)
        {
            return null;
        }

        var clamped = Math.Clamp(position, 0, 1);
        var rangeStart = RangeStart ?? Segments[0].StartedAt;
        var rangeEnd = RangeEnd ?? Segments[^1].EndedAt;
        if (rangeEnd <= rangeStart)
        {
            return Segments[^1];
        }

        var target = rangeStart + TimeSpan.FromTicks((long)((rangeEnd - rangeStart).Ticks * clamped));
        return Segments.FirstOrDefault(item => item.StartedAt <= target && target <= item.EndedAt);
    }
}

public sealed record LocalVideoSessionManifest(
    string Schema,
    string SessionId,
    string CameraSourceId,
    string StreamId,
    string ConnectionId,
    string Protocol,
    DateTimeOffset StartedAt,
    int Width,
    int Height,
    int FrameRate,
    int SegmentSeconds);

public sealed record LocalVideoCatalogContext(
    string? ActiveSessionId,
    LocalVideoRecorderState RecorderState,
    string? ProtectedSegmentId,
    string? ActiveSessionDirectory);
