namespace RobotCommand.Models;

public enum OperatorLocationState
{
    Unavailable,
    Available
}

public sealed record OperatorLocationSnapshot(
    OperatorLocationState State,
    double? LongitudeDegrees,
    double? LatitudeDegrees,
    double? AccuracyMeters,
    DateTimeOffset? LastUpdated,
    string Status)
{
    public bool IsAvailable => State == OperatorLocationState.Available &&
                                LongitudeDegrees is not null &&
                                LatitudeDegrees is not null;

    public static OperatorLocationSnapshot Unavailable(string status = "Location is unavailable.")
        => new(OperatorLocationState.Unavailable, null, null, null, null, status);

    public static OperatorLocationSnapshot AvailableAt(
        double longitude,
        double latitude,
        double? accuracyMeters,
        DateTimeOffset updatedAt)
        => new(
            OperatorLocationState.Available,
            longitude,
            latitude,
            accuracyMeters,
            updatedAt,
            "Operator location available.");
}
