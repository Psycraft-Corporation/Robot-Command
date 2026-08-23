namespace RobotCommand.Models;

public sealed record BehaviourGeometryBindingRecord(
    string ConnectionId,
    string BehaviourId,
    string Version,
    string SlotId,
    string? GeometryId,
    bool Bound,
    bool ObjectExists,
    bool RequiredRegistration,
    bool RequireObjectAtStart,
    bool AllowEmptyGeometry,
    string ExpectedPolicyKind,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Issues)
{
    public bool Required =>
        (RequiredRegistration || RequireObjectAtStart) && !AllowEmptyGeometry;

    public bool Ready => !Required ||
                         Bound &&
                         !string.IsNullOrWhiteSpace(GeometryId) &&
                         ObjectExists &&
                         Issues.Count == 0;
}

public sealed record BehaviourGeometryReadiness(
    string ConnectionId,
    string BehaviourId,
    string Version,
    IReadOnlyList<BehaviourGeometryBindingRecord> Bindings,
    IReadOnlyList<string> GeometryIds,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0;

    public static BehaviourGeometryReadiness NoRequirements(
        string connectionId,
        string behaviourId,
        string version)
        => new(connectionId, behaviourId, version, [], [], [], []);
}

public sealed record BehaviourGeometryBindingCommandRequest(
    string ConnectionId,
    string BehaviourId,
    string Version,
    string SlotId,
    string? GeometryId = null,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record BehaviourGeometryBindingCommandResult(
    bool Accepted,
    string Message,
    BehaviourGeometryBindingRecord? Binding = null,
    IReadOnlyList<string>? Issues = null);

public static class BehaviourGeometryCompatibility
{
    public static bool KindMatches(string? kindHint, GeometryDocumentKind kind)
    {
        var normalized = Normalize(kindHint);
        return normalized switch
        {
            "" or "unspecified" or "any" or "geometry" => true,
            "point" or "poi" or "pointofinterest" => kind == GeometryDocumentKind.PointOfInterest,
            "route" or "path" or "polyline" or "waypointsequence" or "waypoints" =>
                kind == GeometryDocumentKind.WaypointSequence,
            "zone" or "area" or "polygon" => kind == GeometryDocumentKind.Zone,
            _ => true
        };
    }

    public static bool PolicyMatches(string? expectedPolicyKind, GeometryPolicyAnnotation policy)
    {
        var expected = Normalize(expectedPolicyKind);
        if (expected is "" or "none" or "unspecified" or "any")
        {
            return true;
        }

        return string.Equals(expected, Normalize(policy.Kind), StringComparison.Ordinal) ||
               string.Equals(expected, Normalize(policy.Constraint), StringComparison.Ordinal);
    }

    public static string ExpectedKindLabel(string? kindHint)
        => Normalize(kindHint) switch
        {
            "point" or "poi" or "pointofinterest" => "Point of Interest",
            "route" or "path" or "polyline" or "waypointsequence" or "waypoints" => "Waypoint Sequence",
            "zone" or "area" or "polygon" => "Zone",
            _ => "Any geometry"
        };

    private static string Normalize(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
