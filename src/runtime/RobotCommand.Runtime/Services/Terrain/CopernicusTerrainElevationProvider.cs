using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RobotCommand.Services.Terrain;

/// <summary>
/// Copernicus DEM adapter for Auterion's carpet endpoint. The endpoint returns
/// 0.01-degree tiles containing one-arc-second elevation samples; this adapter
/// interpolates the requested points from those tiles.
/// </summary>
public sealed class CopernicusTerrainElevationProvider : ITerrainElevationProvider, IDisposable
{
    public const string DefaultEndpoint = "https://terrain-ce.suite.auterion.com";

    private const double TileSizeDegrees = 0.01;
    private const double TileSampleSpacingMetres = 30;
    private const int MaximumConcurrentTileRequests = 8;
    private const int MaximumTilesPerQuery = 64;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TerrainOptions _options;
    private readonly ILogger<CopernicusTerrainElevationProvider>? _logger;

    public CopernicusTerrainElevationProvider(
        TerrainOptions options,
        ILogger<CopernicusTerrainElevationProvider>? logger = null,
        HttpClient? httpClient = null)
    {
        _options = options.Normalize();
        _logger = logger;
        _httpClient = httpClient ?? CreateHttpClient(_options.TimeoutSeconds);
        _ownsHttpClient = httpClient is null;
    }

    public string ProviderId => "copernicus";

    public string DisplayName => "Copernicus DEM";

    public async Task<TerrainProviderResponse> QueryAsync(
        IReadOnlyList<TerrainCoordinate> coordinates,
        int? resolutionMetres,
        CancellationToken cancellationToken = default)
    {
        if (coordinates.Count == 0)
        {
            return Failure("EMPTY_QUERY", "At least one coordinate is required.");
        }

        if (coordinates.Any(coordinate => !coordinate.IsValid))
        {
            return Failure("INVALID_COORDINATE", "All coordinates must be valid WGS84 latitude and longitude values.");
        }

        var tileKeys = coordinates
            .Select(TileKey.FromCoordinate)
            .Distinct()
            .ToArray();
        if (tileKeys.Length > MaximumTilesPerQuery)
        {
            return Failure(
                "VIEW_TOO_BROAD",
                $"The terrain request spans {tileKeys.Length} DEM tiles; narrow the requested area to query elevation data.");
        }

        using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 30)));
        var queryCancellationToken = queryTimeout.Token;
        using var gate = new SemaphoreSlim(MaximumConcurrentTileRequests);
        var tiles = new Dictionary<TileKey, TileData?>();
        var tileFailures = new List<string>();
        var invalidTileResponse = false;

        async Task LoadTileAsync(TileKey key)
        {
            await gate.WaitAsync(queryCancellationToken).ConfigureAwait(false);
            try
            {
                var tileResult = await GetTileAsync(key, queryCancellationToken).ConfigureAwait(false);
                lock (tiles)
                {
                    tiles[key] = tileResult.Tile;
                    if (tileResult.Tile is null)
                    {
                        tileFailures.Add(key.ToString());
                    }

                    invalidTileResponse |= tileResult.InvalidResponse;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await Task.WhenAll(tileKeys.Select(LoadTileAsync)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("TIMEOUT", "Copernicus terrain tiles did not finish within the terrain query timeout.");
        }

        var elevations = coordinates
            .Select(coordinate =>
            {
                var key = TileKey.FromCoordinate(coordinate);
                return tiles.TryGetValue(key, out var tile) && tile is not null
                    ? tile.Interpolate(coordinate)
                    : null;
            })
            .ToArray();

        var successful = elevations.Count(value => value is double number && double.IsFinite(number));
        if (successful == 0)
        {
            return Failure(
                invalidTileResponse ? "INVALID_RESPONSE" : "NO_DATA",
                invalidTileResponse
                    ? "Copernicus returned an invalid terrain tile response."
                    : tileFailures.Count == 0
                    ? "Copernicus returned no usable elevation data."
                    : "Copernicus terrain tiles could not be loaded for the requested coordinates.");
        }

        return new(
            true,
            elevations,
            TerrainVerticalReference.ProviderNative,
            "Copernicus DEM provider-native",
            TileSampleSpacingMetres,
            successful == coordinates.Count ? null : "PARTIAL_DATA",
            successful == coordinates.Count
                ? null
                : $"Copernicus returned data for {successful} of {coordinates.Count} coordinates.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<TileFetchResult> GetTileAsync(TileKey key, CancellationToken cancellationToken)
    {
        var endpoint = _options.Endpoint.TrimEnd('/');
        var swLatitude = key.LatitudeIndex * TileSizeDegrees - 90d;
        var swLongitude = key.LongitudeIndex * TileSizeDegrees - 180d;
        var neLatitude = swLatitude + TileSizeDegrees;
        var neLongitude = swLongitude + TileSizeDegrees;
        var points = string.Create(
            CultureInfo.InvariantCulture,
            $"{swLatitude:0.##########},{swLongitude:0.##########},{neLatitude:0.##########},{neLongitude:0.##########}");
        var uriText = $"{endpoint}/api/v1/carpet?points={Uri.EscapeDataString(points)}";
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var requestUri) ||
            requestUri.Scheme is not ("http" or "https"))
        {
            _logger?.LogWarning("Invalid Copernicus terrain endpoint {Endpoint}.", _options.Endpoint);
            return TileFetchResult.Invalid;
        }

        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
                    if (!IsTransient(response.StatusCode) || attempt == 2)
                    {
                        break;
                    }

                    response.Dispose();
                    response = null;
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (response is null || !response.IsSuccessStatusCode)
            {
                _logger?.LogInformation(
                    "Copernicus terrain tile {Tile} returned HTTP {StatusCode}.",
                    key,
                    response is null ? "no response" : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                return TileFetchResult.Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var tile = ParseTile(document.RootElement);
            return tile is null ? TileFetchResult.Invalid : new(tile, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger?.LogInformation(ex, "Copernicus terrain tile {Tile} was not valid JSON.", key);
            return TileFetchResult.Invalid;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogInformation(ex, "Copernicus terrain tile {Tile} request failed.", key);
            return TileFetchResult.Unavailable;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static TileData? ParseTile(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("bounds", out var bounds) ||
            !data.TryGetProperty("carpet", out var carpet) ||
            carpet.ValueKind != JsonValueKind.Array ||
            !TryReadCoordinate(bounds, "sw", out var southWest) ||
            !TryReadCoordinate(bounds, "ne", out var northEast))
        {
            return null;
        }

        var rows = new List<double[]>();
        foreach (var rowElement in carpet.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var row = new List<double>();
            foreach (var valueElement in rowElement.EnumerateArray())
            {
                if (valueElement.ValueKind != JsonValueKind.Number ||
                    !valueElement.TryGetDouble(out var value) ||
                    !double.IsFinite(value))
                {
                    return null;
                }

                row.Add(value);
            }

            if (row.Count == 0 || (rows.Count > 0 && row.Count != rows[0].Length))
            {
                return null;
            }

            rows.Add(row.ToArray());
        }

        return rows.Count == 0
            ? null
            : new(southWest.LatitudeDegrees, southWest.LongitudeDegrees,
                northEast.LatitudeDegrees, northEast.LongitudeDegrees, rows.ToArray());
    }

    private static bool TryReadCoordinate(
        JsonElement parent,
        string propertyName,
        out TerrainCoordinate coordinate)
    {
        coordinate = new(0, 0);
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < 2 ||
            !value[0].TryGetDouble(out var latitude) ||
            !value[1].TryGetDouble(out var longitude))
        {
            return false;
        }

        coordinate = new(latitude, longitude);
        return coordinate.IsValid;
    }

    private static TerrainProviderResponse Failure(string code, string message)
        => new(false, [], TerrainVerticalReference.ProviderNative,
            "Copernicus DEM provider-native", TileSampleSpacingMetres, code, message);

    private static HttpClient CreateHttpClient(int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LogosRobotCommand", "1.0"));
        return client;
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private readonly record struct TileKey(int LatitudeIndex, int LongitudeIndex)
    {
        public static TileKey FromCoordinate(TerrainCoordinate coordinate)
        {
            // The decimal tile boundaries are not represented exactly in binary
            // floating point (for example, 43.70 can evaluate just below its
            // boundary), so include a small tolerance before flooring.
            var latitudeIndex = Math.Clamp((int)Math.Floor((coordinate.LatitudeDegrees + 90d) / TileSizeDegrees + 1e-8), 0, 17_999);
            var longitudeIndex = Math.Clamp((int)Math.Floor((coordinate.LongitudeDegrees + 180d) / TileSizeDegrees + 1e-8), 0, 35_999);
            return new(latitudeIndex, longitudeIndex);
        }

        public override string ToString() => $"{LatitudeIndex}:{LongitudeIndex}";
    }

    private readonly record struct TileFetchResult(TileData? Tile, bool InvalidResponse)
    {
        public static TileFetchResult Invalid { get; } = new(null, true);

        public static TileFetchResult Unavailable { get; } = new(null, false);
    }

    private sealed record TileData(
        double SouthWestLatitude,
        double SouthWestLongitude,
        double NorthEastLatitude,
        double NorthEastLongitude,
        IReadOnlyList<double[]> Rows)
    {
        public double? Interpolate(TerrainCoordinate coordinate)
        {
            if (Rows.Count == 0 || Rows[0].Length == 0 ||
                coordinate.LatitudeDegrees < SouthWestLatitude - 1e-9 ||
                coordinate.LatitudeDegrees > NorthEastLatitude + 1e-9 ||
                coordinate.LongitudeDegrees < SouthWestLongitude - 1e-9 ||
                coordinate.LongitudeDegrees > NorthEastLongitude + 1e-9)
            {
                return null;
            }

            var rowPosition = NorthEastLatitude == SouthWestLatitude
                ? 0
                : (coordinate.LatitudeDegrees - SouthWestLatitude) /
                  (NorthEastLatitude - SouthWestLatitude) * (Rows.Count - 1);
            var columnPosition = NorthEastLongitude == SouthWestLongitude
                ? 0
                : (coordinate.LongitudeDegrees - SouthWestLongitude) /
                  (NorthEastLongitude - SouthWestLongitude) * (Rows[0].Length - 1);
            var row0 = Math.Clamp((int)Math.Floor(rowPosition), 0, Rows.Count - 1);
            var row1 = Math.Clamp(row0 + 1, 0, Rows.Count - 1);
            var column0 = Math.Clamp((int)Math.Floor(columnPosition), 0, Rows[0].Length - 1);
            var column1 = Math.Clamp(column0 + 1, 0, Rows[0].Length - 1);
            var rowFraction = Math.Clamp(rowPosition - row0, 0, 1);
            var columnFraction = Math.Clamp(columnPosition - column0, 0, 1);
            var top = Rows[row0][column0] + (Rows[row0][column1] - Rows[row0][column0]) * columnFraction;
            var bottom = Rows[row1][column0] + (Rows[row1][column1] - Rows[row1][column0]) * columnFraction;
            var value = top + (bottom - top) * rowFraction;
            return double.IsFinite(value) ? value : null;
        }
    }
}
