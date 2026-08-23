namespace RobotCommand.Core;

/// <summary>Simulation parameters exposed for a Ghost profile.</summary>
public sealed record GhostSimulationStats(
    double MaximumHorizontalSpeedMetresPerSecond,
    double MaximumClimbRateMetresPerSecond,
    double MaximumDescentRateMetresPerSecond,
    double HorizontalAccelerationMetresPerSecondSquared,
    double VerticalAccelerationMetresPerSecondSquared,
    double MaximumYawRateDegreesPerSecond,
    double MaximumAltitudeAglMetres,
    double NominalEnduranceMinutes);

public enum GhostProfileAssetKind
{
    Image,
    Mesh
}

/// <summary>Validated, immutable metadata for the optional visual attached to a Ghost profile.</summary>
public sealed record GhostProfileAssetSnapshot(
    GhostProfileAssetKind Kind,
    string FileName,
    string ContentType,
    long ByteLength,
    string Sha256,
    DateTimeOffset ImportedAt,
    string ValidationStatus = "Valid",
    IReadOnlyList<string>? Diagnostics = null,
    int? Width = null,
    int? Height = null,
    int? VertexCount = null,
    int? TriangleCount = null,
    int? SubmeshCount = null,
    string? Format = null);

/// <summary>Runtime-owned read handle used by presentation clients to load an approved asset.</summary>
public sealed record GhostProfileAssetReadHandle(
    string FileName,
    string ContentType,
    string ManagedFilePath);

/// <summary>Immutable, built-in or future user-defined Ghost aircraft profile.</summary>
public sealed record GhostProfileSnapshot(
    string Id,
    string Name,
    string VehicleType,
    GhostSimulationStats Simulation,
    bool IsBuiltIn,
    bool IsEditable = false,
    GhostProfileAssetSnapshot? Asset = null);

public sealed record GhostProfileCreateRequest(
    string Name,
    GhostSimulationStats Simulation,
    string VehicleType = "Multicopter");

public sealed record GhostProfileUpdateRequest(
    string Name,
    GhostSimulationStats Simulation,
    string VehicleType = "Multicopter");

public interface IGhostProfileWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<GhostProfileSnapshot> Profiles { get; }
    IReadOnlyList<string> LibraryIssues { get; }
    GhostProfileSnapshot? Find(string profileId);
    Task<GhostProfileSnapshot> CreateAsync(GhostProfileCreateRequest request, CancellationToken cancellationToken = default);
    Task<GhostProfileSnapshot> UpdateAsync(string profileId, GhostProfileUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
}

public interface IGhostProfileAssetWorkflow
{
    event EventHandler? Changed;
    GhostProfileAssetSnapshot? Find(string profileId);
    Task<GhostProfileAssetSnapshot> ImportAsync(string profileId, string sourcePath, CancellationToken cancellationToken = default);
    Task RemoveAsync(string profileId, CancellationToken cancellationToken = default);
    Task<GhostProfileAssetReadHandle?> OpenReadAsync(string profileId, CancellationToken cancellationToken = default);
}

public static class GhostProfileDefaults
{
    public static GhostProfileSnapshot Dracula { get; } = new(
        "dracula", "Dracula", "Multicopter",
        new GhostSimulationStats(10, 2, 2, 8, 4, 90, 120, 30), true, false);
}
