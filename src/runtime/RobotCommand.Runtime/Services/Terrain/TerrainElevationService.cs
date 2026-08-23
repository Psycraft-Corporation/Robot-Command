using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RobotCommand.Services.Terrain;

public sealed class TerrainElevationService : ITerrainElevationService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ITerrainElevationProvider _provider;
    private readonly TerrainOptions _options;
    private readonly bool _providerEnabled;
    private readonly ILogger<TerrainElevationService>? _logger;
    private readonly object _cacheGate = new();
    private readonly object _rateGate = new();
    private readonly SemaphoreSlim _providerGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<FetchResult>>> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _nextRequestAt;
    private bool _disposed;

    public TerrainElevationService(
        ITerrainElevationProvider provider,
        TerrainOptions options,
        string cacheRoot,
        ILogger<TerrainElevationService>? logger = null)
    {
        _provider = provider;
        _options = options.Normalize();
        _providerEnabled = string.Equals(_options.ProviderId, _provider.ProviderId, StringComparison.OrdinalIgnoreCase);
        _logger = logger;
        CachePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(_options.CachePath)
                ? Path.Combine(cacheRoot, "terrain", "elevation-cache.json")
                : _options.CachePath);
        LoadCache();
    }

    public string CachePath { get; }

    public Task<TerrainQueryResult> GetElevationAsync(
        double latitudeDegrees,
        double longitudeDegrees,
        TerrainQueryOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetElevationsAsync(
            [new TerrainCoordinate(latitudeDegrees, longitudeDegrees)],
            options,
            cancellationToken);

    public async Task<TerrainQueryResult> GetElevationsAsync(
        IReadOnlyList<TerrainCoordinate> coordinates,
        TerrainQueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new TerrainQueryOptions();

        if (!_providerEnabled)
        {
            return TerrainQueryResult.Failure(
                TerrainQueryStatus.Unavailable,
                "PROVIDER_NOT_CONFIGURED",
                $"Terrain provider '{_options.ProviderId}' is not available in this build.");
        }

        if (coordinates.Count == 0)
        {
            return TerrainQueryResult.Invalid("EMPTY_QUERY", "At least one terrain coordinate is required.");
        }

        if (coordinates.Any(coordinate => !coordinate.IsValid))
        {
            return TerrainQueryResult.Invalid(
                "INVALID_COORDINATE",
                "Terrain coordinates must contain finite WGS84 latitude and longitude values.");
        }

        var maximumAge = options.MaximumAge ?? TimeSpan.FromDays(_options.CacheMaximumAgeDays);
        if (maximumAge < TimeSpan.Zero)
        {
            return TerrainQueryResult.Invalid("INVALID_MAXIMUM_AGE", "Maximum cache age cannot be negative.");
        }

        var now = DateTimeOffset.UtcNow;
        var keys = coordinates
            .Select(coordinate => CacheKey(coordinate, options.ResolutionMetres))
            .ToArray();
        var samples = new TerrainElevationSample?[coordinates.Count];
        var missing = new List<int>();
        var stale = new List<int>();
        var fetchResult = FetchResult.Empty;

        for (var index = 0; index < coordinates.Count; index++)
        {
            CacheEntry? entry;
            lock (_cacheGate)
            {
                _cache.TryGetValue(keys[index], out entry);
            }

            if (entry is null)
            {
                missing.Add(index);
                continue;
            }

            var age = now - entry.RetrievedAt;
            if (age <= maximumAge)
            {
                samples[index] = entry.ToSample(fromCache: true, isStale: false);
            }
            else if (options.AllowStale)
            {
                stale.Add(index);
                missing.Add(index);
                samples[index] = entry.ToSample(fromCache: true, isStale: true);
            }
            else
            {
                missing.Add(index);
            }
        }

        if (missing.Count > 0)
        {
            FetchResult fetchedResult;
            if (missing.Count == 1)
            {
                var index = missing[0];
                fetchedResult = await FetchSingleDeduplicatedAsync(
                    coordinates[index],
                    options.ResolutionMetres,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                fetchedResult = await FetchBatchAsync(
                    missing.Select(index => coordinates[index]).ToArray(),
                    options.ResolutionMetres,
                    cancellationToken).ConfigureAwait(false);
            }

            fetchResult = fetchedResult;
            var fetched = fetchedResult.Entries;
            for (var offset = 0; offset < missing.Count; offset++)
            {
                var index = missing[offset];
                if (fetched.TryGetValue(offset, out var entry))
                {
                    samples[index] = entry.ToSample(fromCache: false, isStale: false);
                }
            }

            if (fetched.Count > 0)
            {
                SaveCache();
            }
        }

        var materialized = samples.Where(sample => sample is not null).Select(sample => sample!).ToArray();
        if (materialized.Length == coordinates.Count)
        {
            var isStale = materialized.Any(sample => sample.IsStale);
            return new(
                isStale ? TerrainQueryStatus.Stale : TerrainQueryStatus.Available,
                materialized,
                isStale ? "STALE_CACHE" : null,
                isStale ? "One or more terrain samples came from an expired cache entry." : null);
        }

        if (materialized.Length > 0)
        {
            return new(
                TerrainQueryStatus.Available,
                materialized,
                "PARTIAL_DATA",
                $"Terrain data was unavailable for {coordinates.Count - materialized.Length} coordinate(s).");
        }

        if (stale.Count > 0)
        {
            return new(
                TerrainQueryStatus.Stale,
                [],
                "STALE_CACHE",
                "Terrain data is only available from an expired cache entry.");
        }

        return TerrainQueryResult.Failure(
            fetchResult.Status == TerrainQueryStatus.NoData
                ? TerrainQueryStatus.NoData
                : TerrainQueryStatus.Unavailable,
            fetchResult.Code ?? "PROVIDER_UNAVAILABLE",
            fetchResult.Message ?? "The configured terrain provider did not return usable elevation data.");
    }

    public void Dispose()
    {
        _disposed = true;
        _providerGate.Dispose();
    }

    private async Task<FetchResult> FetchBatchAsync(
        IReadOnlyList<TerrainCoordinate> coordinates,
        int? resolutionMetres,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, CacheEntry>();
        await _providerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
            var response = await _provider.QueryAsync(coordinates, resolutionMetres, cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded || response.ElevationsMetres.Count != coordinates.Count)
            {
                _logger?.LogInformation(
                    "Terrain provider {ProviderId} returned no usable data: {Code} {Message}",
                    _provider.ProviderId,
                    response.ErrorCode,
                    response.ErrorMessage);
                return new(
                    result,
                    response.Succeeded && response.ElevationsMetres.Count == 0
                        ? TerrainQueryStatus.NoData
                        : TerrainQueryStatus.Unavailable,
                    response.ErrorCode ?? (response.Succeeded && response.ElevationsMetres.Count == 0
                        ? "NO_DATA"
                        : "PROVIDER_UNAVAILABLE"),
                    response.ErrorMessage ?? (response.Succeeded && response.ElevationsMetres.Count == 0
                        ? "The terrain provider returned no elevation data."
                        : "The terrain provider did not return a complete usable response."));
            }

            var retrievedAt = DateTimeOffset.UtcNow;
            for (var index = 0; index < coordinates.Count; index++)
            {
                var elevation = response.ElevationsMetres[index];
                if (elevation is not double value || !double.IsFinite(value))
                {
                    continue;
                }

                var entry = new CacheEntry(
                    CacheKey(coordinates[index], resolutionMetres),
                    _provider.ProviderId,
                    coordinates[index],
                    value,
                    response.VerticalReference,
                    response.VerticalDatum,
                    response.ResolutionMetres,
                    retrievedAt,
                    retrievedAt);
                lock (_cacheGate)
                {
                    _cache[entry.Key] = entry;
                }
                result[index] = entry;
            }

            TrimCache();
            return result.Count == 0
                ? new(result, TerrainQueryStatus.NoData, "NO_DATA", "The terrain provider returned no elevation data.")
                : new(result, TerrainQueryStatus.Available, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex, "Terrain provider {ProviderId} failed while querying elevation.", _provider.ProviderId);
            return new(
                result,
                TerrainQueryStatus.Unavailable,
                "PROVIDER_ERROR",
                "The configured terrain provider failed while querying elevation.");
        }
        finally
        {
            _providerGate.Release();
        }
    }

    private async Task<FetchResult> FetchSingleDeduplicatedAsync(
        TerrainCoordinate coordinate,
        int? resolutionMetres,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(coordinate, resolutionMetres);
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<FetchResult>>(
                () => FetchSingleAsync(coordinate, resolutionMetres, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<FetchResult>>>(key, lazy));
        }
    }

    private async Task<FetchResult> FetchSingleAsync(
        TerrainCoordinate coordinate,
        int? resolutionMetres,
        CancellationToken cancellationToken)
    {
        var fetched = await FetchBatchAsync([coordinate], resolutionMetres, cancellationToken).ConfigureAwait(false);
        return fetched;
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1d / _options.RequestsPerSecond);
        DateTimeOffset delayUntil;
        lock (_rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            delayUntil = _nextRequestAt > now ? _nextRequestAt : now;
            _nextRequestAt = delayUntil + interval;
        }

        var delay = delayUntil - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            using var stream = File.OpenRead(CachePath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(stream, JsonOptions) ?? [];
            lock (_cacheGate)
            {
                foreach (var entry in entries.Where(IsValidCacheEntry))
                {
                    _cache[entry.Key] = entry;
                }
            }
            TrimCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogInformation(ex, "Terrain cache could not be loaded; starting with an empty cache.");
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath) ?? AppContext.BaseDirectory);
            List<CacheEntry> entries;
            lock (_cacheGate)
            {
                entries = _cache.Values.OrderByDescending(entry => entry.RetrievedAt).ToList();
            }

            var temporaryPath = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entries, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogInformation(ex, "Terrain cache could not be persisted.");
        }
    }

    private void TrimCache()
    {
        lock (_cacheGate)
        {
            foreach (var key in _cache.Values
                         .OrderByDescending(entry => entry.RetrievedAt)
                         .Skip(_options.CacheMaximumEntries)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _cache.Remove(key);
            }
        }
    }

    private string CacheKey(TerrainCoordinate coordinate, int? resolutionMetres)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_provider.ProviderId}:{coordinate.LatitudeDegrees:0.000001}:{coordinate.LongitudeDegrees:0.000001}:{resolutionMetres.GetValueOrDefault(0)}");

    private bool IsValidCacheEntry(CacheEntry entry)
        => !string.IsNullOrWhiteSpace(entry.Key) &&
           string.Equals(entry.ProviderId, _provider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
           entry.Coordinate.IsValid &&
           double.IsFinite(entry.ElevationMetres) &&
           entry.RetrievedAt != default;

    private sealed record CacheEntry(
        string Key,
        string ProviderId,
        TerrainCoordinate Coordinate,
        double ElevationMetres,
        TerrainVerticalReference VerticalReference,
        string VerticalDatum,
        double? ResolutionMetres,
        DateTimeOffset SampledAt,
        DateTimeOffset RetrievedAt)
    {
        public TerrainElevationSample ToSample(bool fromCache, bool isStale)
            => new(
                Coordinate,
                ElevationMetres,
                VerticalReference,
                VerticalDatum,
                ProviderId,
                ResolutionMetres,
                SampledAt,
                RetrievedAt,
                fromCache,
                isStale);
    }

    private sealed record FetchResult(
        Dictionary<int, CacheEntry> Entries,
        TerrainQueryStatus Status,
        string? Code,
        string? Message)
    {
        public static FetchResult Empty { get; } = new([], TerrainQueryStatus.Unavailable, null, null);
    }
}
