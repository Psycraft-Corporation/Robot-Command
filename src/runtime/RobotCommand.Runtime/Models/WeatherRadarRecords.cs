namespace RobotCommand.Models;

public sealed record WeatherRadarSnapshot(
    string? FrameId,
    string? TileUrlTemplate,
    DateTimeOffset? FrameTimestamp,
    DateTimeOffset? RetrievedAt,
    bool IsStale,
    string Status,
    string Attribution,
    int MaximumZoom)
{
    public static WeatherRadarSnapshot Empty { get; } = new(
        null,
        null,
        null,
        null,
        false,
        "Weather radar unavailable",
        "Weather data by RainViewer",
        7);

    public bool HasFrame =>
        !string.IsNullOrWhiteSpace(FrameId) &&
        !string.IsNullOrWhiteSpace(TileUrlTemplate) &&
        FrameTimestamp is not null;
}
