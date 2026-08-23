namespace RobotCommand.Models;

/// <summary>
/// Portable, non-executable Robot Command geometry collection. Group membership
/// is library organisation only and has no deployment or mission meaning.
/// </summary>
public sealed record GeometryCollection
{
    public const string CurrentSchemaVersion = "robotcommand.geometry-set.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = "Geometry set";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<GeometryDocument> Geometry { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record GeometryGroupManifest
{
    public const string CurrentSchemaVersion = "robotcommand.geometry-groups.v1";
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}
