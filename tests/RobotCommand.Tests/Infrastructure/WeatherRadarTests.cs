using System.Net;
using System.Net.Http;
using BruTile.Cache;
using RobotCommand.Models;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class WeatherRadarTests
{
    [Fact]
    public void Parser_SelectsNewestSecurePastFrame()
    {
        var newest = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        var older = newest - 600;
        var json = $$"""
            {
              "host": "https://tilecache.rainviewer.com",
              "radar": {
                "past": [
                  { "time": {{older}}, "path": "/v2/older" },
                  { "time": {{newest}}, "path": "v2/newest" }
                ]
              }
            }
            """;

        var snapshot = RainViewerWeatherRadarParser.Parse(json, DateTimeOffset.UtcNow);

        Assert.Equal(newest.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.FrameId);
        Assert.Contains("https://tilecache.rainviewer.com/v2/newest/256/{z}/{x}/{y}", snapshot.TileUrlTemplate);
        Assert.Equal(7, snapshot.MaximumZoom);
        Assert.True(snapshot.HasFrame);
        Assert.Equal("Weather data by RainViewer", snapshot.Attribution);
    }

    [Fact]
    public void Parser_RejectsInsecureHostAndMissingFrames()
    {
        const string insecure = "{\"host\":\"http://example.test\",\"radar\":{\"past\":[]}}";

        var exception = Assert.Throws<InvalidDataException>(() =>
            RainViewerWeatherRadarParser.Parse(insecure, DateTimeOffset.UtcNow));

        Assert.Contains("secure", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Source_RetainsLastFrameWhenRefreshFailsAndStopsWhenDisabled()
    {
        var unixTime = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var json = $$"""
            {
              "host": "https://tilecache.rainviewer.com",
              "radar": { "past": [ { "time": {{unixTime}}, "path": "/v2/frame" } ] }
            }
            """;
        using var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            },
            new HttpRequestException("offline"));
        using var client = new HttpClient(handler);
        await using var source = new RainViewerWeatherRadarSource(client);

        await source.SetEnabledAsync(true);
        await source.RefreshAsync();
        var loaded = source.Current;
        Assert.True(loaded.HasFrame);

        await source.RefreshAsync();

        Assert.True(source.Current.HasFrame);
        Assert.True(source.Current.IsStale);
        Assert.Contains("last frame", source.Current.Status, StringComparison.OrdinalIgnoreCase);

        source.SetViewActive(false);
        await source.SetEnabledAsync(false);
        Assert.False(source.Current.HasFrame);
        Assert.Equal("Weather radar disabled", source.Current.Status);
    }

    [Fact]
    public void TileSource_UsesWeatherZoomAndDedicatedCache()
    {
        var snapshot = new WeatherRadarSnapshot(
            "frame-1",
            "https://tilecache.rainviewer.com/v2/frame/256/{z}/{x}/{y}/2/1_1.png",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            false,
            "Radar current",
            "Weather data by RainViewer",
            7);

        var source = OnlineMapTileSources.CreateWeather(snapshot);

        Assert.IsType<FileCache>(source.PersistentCache);
        Assert.Equal(8, source.Schema.Resolutions.Count);
        Assert.NotEqual(OnlineMapTileSources.CacheDirectory("standard"), OnlineMapTileSources.CacheDirectory("weather"));
    }

    private sealed class SequencedHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = _responses.Count == 0 ? new HttpRequestException("no response") : _responses.Dequeue();
            return response switch
            {
                HttpResponseMessage message => Task.FromResult(message),
                Exception exception => Task.FromException<HttpResponseMessage>(exception),
                _ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("Unsupported test response."))
            };
        }
    }
}
