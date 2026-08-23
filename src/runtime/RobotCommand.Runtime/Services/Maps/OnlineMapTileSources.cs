using System.Collections.Concurrent;
using System.Net.Http.Headers;
using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;
using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public static class OnlineMapTileSources
{
    public static readonly IReadOnlyList<string> StyleIds =
        ["standard", "topographic", "satellite"];

    private const int MaximumPrefetchTilesPerStyle = 24;
    private static readonly ConcurrentDictionary<string, IPersistentCache<byte[]>> Caches =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient PrefetchClient = CreateHttpClient();

    public static HttpTileSource Create(string? styleId)
    {
        var normalized = NormalizeStyleId(styleId);
        var cache = Caches.GetOrAdd(normalized, CreateCache);
        return normalized switch
        {
            "topographic" => new HttpTileSource(
                new GlobalSphericalMercator(0, 17),
                "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png",
                ["a", "b", "c"],
                name: "OpenTopoMap",
                persistentCache: cache),
            "satellite" => new HttpTileSource(
                new GlobalSphericalMercator(0, 19),
                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
                [],
                name: "Esri World Imagery",
                persistentCache: cache),
            _ => new HttpTileSource(
                new GlobalSphericalMercator(0, 19),
                "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
                ["a", "b", "c"],
                name: "OpenStreetMap Standard",
                persistentCache: cache)
        };
    }

    public static async Task PrefetchAlternatesAsync(
        string? activeStyleId,
        MapViewportSnapshot viewport,
        double viewportWidthPixels,
        double viewportHeightPixels,
        CancellationToken cancellationToken)
    {
        if (viewportWidthPixels <= 0 ||
            viewportHeightPixels <= 0 ||
            viewport.Resolution <= 0 ||
            !MapCoordinateProjector.TryProject(
                viewport.LongitudeDegrees,
                viewport.LatitudeDegrees,
                out var center))
        {
            return;
        }

        var halfWidth = viewportWidthPixels * viewport.Resolution * 0.5;
        var halfHeight = viewportHeightPixels * viewport.Resolution * 0.5;
        var extent = new Extent(
            center.X - halfWidth,
            center.Y - halfHeight,
            center.X + halfWidth,
            center.Y + halfHeight);
        var active = NormalizeStyleId(activeStyleId);

        foreach (var styleId in StyleIds.Where(id => !string.Equals(id, active, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Create(styleId);
            var tiles = source.Schema
                .GetTileInfos(extent, viewport.Resolution)
                .Take(MaximumPrefetchTilesPerStyle)
                .ToArray();
            foreach (var tile in tiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await source.GetTileAsync(PrefetchClient, tile, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    // Prefetch is opportunistic. Normal foreground loading will
                    // retry if a provider or network is temporarily unavailable.
                    break;
                }
            }
        }
    }

    public static string NormalizeStyleId(string? styleId)
        => styleId?.Trim().ToLowerInvariant() switch
        {
            "topographic" => "topographic",
            "satellite" => "satellite",
            _ => "standard"
        };

    public static string CacheDirectory(string styleId)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = AppContext.BaseDirectory;
        }

        return Path.Combine(
            localData,
            "Psycraft",
            "Robot Command",
            "MapTileCache",
            NormalizeStyleId(styleId));
    }

    private static IPersistentCache<byte[]> CreateCache(string styleId)
        => new FileCache(CacheDirectory(styleId), "tile", TimeSpan.FromDays(30));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LogosRobotCommand", "1.0"));
        return client;
    }
}
