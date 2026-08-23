namespace RobotCommand.Services.Terrain;

public enum TerrainQueryStatus
{
    Available,
    Unavailable,
    NoData,
    Stale,
    InvalidRequest
}

public enum TerrainVerticalReference
{
    Unknown,
    ProviderNative,
    Orthometric,
    Ellipsoidal
}

public sealed record TerrainCoordinate(
    double LatitudeDegrees,
    double LongitudeDegrees)
{
    public bool IsValid =>
        double.IsFinite(LatitudeDegrees) &&
        double.IsFinite(LongitudeDegrees) &&
        LatitudeDegrees is >= -90 and <= 90 &&
        LongitudeDegrees is >= -180 and <= 180;
}

public sealed record TerrainQueryOptions(
    TimeSpan? MaximumAge = null,
    bool AllowStale = true,
    int? ResolutionMetres = null);

public sealed record TerrainElevationSample(
    TerrainCoordinate Coordinate,
    double ElevationMetres,
    TerrainVerticalReference VerticalReference,
    string VerticalDatum,
    string ProviderId,
    double? ResolutionMetres,
    DateTimeOffset SampledAt,
    DateTimeOffset RetrievedAt,
    bool FromCache,
    bool IsStale);

public sealed record TerrainQueryResult(
    TerrainQueryStatus Status,
    IReadOnlyList<TerrainElevationSample> Samples,
    string? DiagnosticCode = null,
    string? Message = null)
{
    public bool IsSuccess => Status is TerrainQueryStatus.Available or TerrainQueryStatus.Stale;

    public TerrainElevationSample? Sample => Samples.Count == 1 ? Samples[0] : null;

    public static TerrainQueryResult Invalid(string code, string message)
        => new(TerrainQueryStatus.InvalidRequest, Array.Empty<TerrainElevationSample>(), code, message);

    public static TerrainQueryResult Failure(TerrainQueryStatus status, string code, string message)
        => new(status, Array.Empty<TerrainElevationSample>(), code, message);
}

public sealed record TerrainProviderResponse(
    bool Succeeded,
    IReadOnlyList<double?> ElevationsMetres,
    TerrainVerticalReference VerticalReference,
    string VerticalDatum,
    double? ResolutionMetres,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record TerrainOptions(
    string ProviderId = "copernicus",
    string Endpoint = CopernicusTerrainElevationProvider.DefaultEndpoint,
    int TimeoutSeconds = 15,
    int CacheMaximumAgeDays = 30,
    int CacheMaximumEntries = 10_000,
    int RequestsPerSecond = 5,
    string? CachePath = null)
{
    public TerrainOptions Normalize()
        => this with
        {
            ProviderId = string.IsNullOrWhiteSpace(ProviderId) ? "copernicus" : ProviderId.Trim().ToLowerInvariant(),
            Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? CopernicusTerrainElevationProvider.DefaultEndpoint : Endpoint.Trim(),
            TimeoutSeconds = Math.Clamp(TimeoutSeconds <= 0 ? 15 : TimeoutSeconds, 1, 120),
            CacheMaximumAgeDays = Math.Clamp(CacheMaximumAgeDays <= 0 ? 30 : CacheMaximumAgeDays, 1, 3650),
            CacheMaximumEntries = Math.Clamp(CacheMaximumEntries <= 0 ? 10_000 : CacheMaximumEntries, 1, 100_000),
            RequestsPerSecond = Math.Clamp(RequestsPerSecond <= 0 ? 5 : RequestsPerSecond, 1, 60)
        };
}

public interface ITerrainElevationProvider
{
    string ProviderId { get; }

    string DisplayName { get; }

    Task<TerrainProviderResponse> QueryAsync(
        IReadOnlyList<TerrainCoordinate> coordinates,
        int? resolutionMetres,
        CancellationToken cancellationToken = default);
}

public interface ITerrainElevationService
{
    Task<TerrainQueryResult> GetElevationAsync(
        double latitudeDegrees,
        double longitudeDegrees,
        TerrainQueryOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<TerrainQueryResult> GetElevationsAsync(
        IReadOnlyList<TerrainCoordinate> coordinates,
        TerrainQueryOptions? options = null,
        CancellationToken cancellationToken = default);
}
