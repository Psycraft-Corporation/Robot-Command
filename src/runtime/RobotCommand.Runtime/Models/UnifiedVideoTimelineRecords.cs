using System.Security.Cryptography;
using System.Text;

namespace RobotCommand.Models;

public enum VideoTimelineItemOrigin
{
    ConsoleRolling,
    ConsoleRetained,
    VehicleRecording,
    VehicleCached
}

public enum VideoTimelineItemAvailability
{
    Local,
    Remote,
    Unavailable
}

public sealed record VideoTimelineItem(
    string Id,
    VideoTimelineItemOrigin Origin,
    VideoTimelineItemAvailability Availability,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string CameraSourceId,
    string StreamId,
    string Protocol,
    int Width,
    int Height,
    int FrameRate,
    long SizeBytes,
    bool Retained,
    bool GapBefore,
    string? LocalPath = null,
    string? SourceId = null)
{
    public TimeSpan Duration => EndedAt > StartedAt
        ? EndedAt - StartedAt
        : TimeSpan.Zero;

    public bool IsVehicleSide => Origin is
        VideoTimelineItemOrigin.VehicleRecording or
        VideoTimelineItemOrigin.VehicleCached;

    public bool CanPlay => Availability is
        VideoTimelineItemAvailability.Local or
        VideoTimelineItemAvailability.Remote;

    public bool CanDownload => IsVehicleSide && Availability == VideoTimelineItemAvailability.Remote;
}

public sealed record UnifiedVideoTimelineSnapshot(
    IReadOnlyList<VideoTimelineItem> Items,
    DateTimeOffset? RangeStart,
    DateTimeOffset? RangeEnd,
    DateTimeOffset LiveEdge,
    LocalVideoTimelineMode Mode,
    string? ActiveItemId,
    double Position,
    long ConsoleBytes,
    long CachedVehicleBytes,
    DateTimeOffset UpdatedAt)
{
    public static UnifiedVideoTimelineSnapshot Empty { get; } = new(
        [],
        null,
        null,
        DateTimeOffset.UtcNow,
        LocalVideoTimelineMode.Live,
        null,
        1,
        0,
        0,
        DateTimeOffset.UtcNow);

    public bool HasItems => Items.Count > 0;

    public VideoTimelineItem? ItemAt(double position)
    {
        if (Items.Count == 0)
        {
            return null;
        }

        var clamped = Math.Clamp(position, 0, 1);
        var rangeStart = RangeStart ?? Items.Min(item => item.StartedAt);
        var rangeEnd = RangeEnd ?? Items.Max(item => item.EndedAt);
        if (rangeEnd <= rangeStart)
        {
            return Preferred(Items);
        }

        var target = rangeStart + TimeSpan.FromTicks((long)((rangeEnd - rangeStart).Ticks * clamped));
        return Preferred(Items.Where(item => item.StartedAt <= target && target <= item.EndedAt));
    }

    private static VideoTimelineItem? Preferred(IEnumerable<VideoTimelineItem> candidates)
        => candidates
            .OrderBy(item => item.Origin switch
            {
                VideoTimelineItemOrigin.VehicleCached => 0,
                VideoTimelineItemOrigin.VehicleRecording => 1,
                VideoTimelineItemOrigin.ConsoleRetained => 2,
                _ => 3
            })
            .ThenByDescending(item => item.StartedAt)
            .FirstOrDefault();
}

public sealed record RemoteVideoRecordingProfile(
    string Name,
    string PlaybackBaseUrl,
    string PathTemplate,
    string? ConnectionId = null,
    string? ConnectionTarget = null,
    string? BearerTokenEnvironmentVariable = null,
    bool Enabled = true)
{
    public bool Matches(ConnectionDefinition? connection)
    {
        if (!Enabled || connection is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ConnectionId) &&
            string.Equals(ConnectionId, connection.Id, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(ConnectionTarget) &&
               string.Equals(
                   NormalizeTarget(ConnectionTarget),
                   NormalizeTarget(connection.Target),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTarget(string value)
        => value.Trim().TrimEnd('/');
}

public enum RemoteVideoRecordingState
{
    NotConfigured,
    Idle,
    Discovering,
    Available,
    Offline,
    Downloading,
    Faulted
}

public sealed record RemoteVideoRecordingStatus(
    RemoteVideoRecordingState State,
    string Summary,
    string Detail,
    DateTimeOffset UpdatedAt,
    int RemoteSpanCount = 0,
    int CachedSpanCount = 0)
{
    public static RemoteVideoRecordingStatus NotConfigured { get; } = new(
        RemoteVideoRecordingState.NotConfigured,
        "Vehicle recordings not configured",
        "Configure a playback profile to discover remote recordings.",
        DateTimeOffset.UtcNow);
}

public sealed record RemoteVideoRecordingSpan(
    string Id,
    string ProfileName,
    string ConnectionId,
    string CameraSourceId,
    string StreamId,
    string MediaPath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationSeconds,
    string Protocol,
    int Width,
    int Height,
    int FrameRate,
    string? CachedPath,
    long CachedSizeBytes,
    bool Retained)
{
    public bool Cached => !string.IsNullOrWhiteSpace(CachedPath) && File.Exists(CachedPath);

    public static string CreateId(
        string connectionId,
        string mediaPath,
        DateTimeOffset startedAt,
        double durationSeconds)
    {
        var input = $"{connectionId}\n{mediaPath}\n{startedAt:O}\n{durationSeconds:R}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "vehicle-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}

public sealed record RemoteVideoCacheManifest(
    string Schema,
    string Id,
    string ProfileName,
    string ConnectionId,
    string CameraSourceId,
    string StreamId,
    string MediaPath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationSeconds,
    string Protocol,
    int Width,
    int Height,
    int FrameRate,
    string FileName,
    long SizeBytes,
    DateTimeOffset CachedAt);
