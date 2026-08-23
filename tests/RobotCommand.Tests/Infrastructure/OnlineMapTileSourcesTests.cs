using BruTile.Cache;
using RobotCommand.Models;
using RobotCommand.Services.Maps;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OnlineMapTileSourcesTests
{
    [Theory]
    [InlineData(null, "standard")]
    [InlineData("unknown", "standard")]
    [InlineData("STANDARD", "standard")]
    [InlineData("topographic", "topographic")]
    [InlineData("satellite", "satellite")]
    public void NormalizeStyleId_ProducesKnownStableCacheKeys(string? value, string expected)
        => Assert.Equal(expected, OnlineMapTileSources.NormalizeStyleId(value));

    [Fact]
    public void CacheDirectory_IsPersistentAndSeparateForEveryOnlineStyle()
    {
        var standard = OnlineMapTileSources.CacheDirectory("standard");
        var contours = OnlineMapTileSources.CacheDirectory("topographic");
        var satellite = OnlineMapTileSources.CacheDirectory("satellite");

        Assert.Contains("MapTileCache", standard);
        Assert.EndsWith("standard", standard);
        Assert.EndsWith("topographic", contours);
        Assert.EndsWith("satellite", satellite);
        Assert.Equal(3, new[] { standard, contours, satellite }.Distinct().Count());
    }

    [Fact]
    public void Create_UsesFileBackedCacheForEveryOnlineStyle()
    {
        foreach (var styleId in OnlineMapTileSources.StyleIds)
        {
            var source = OnlineMapTileSources.Create(styleId);

            Assert.IsType<FileCache>(source.PersistentCache);
        }
    }

    [Fact]
    public void ViewportState_RetainsTheLastOperatorViewport()
    {
        var state = new MapViewportState();
        var viewport = new MapViewportSnapshot(-79.1, 43.8, 17.5, 32);

        state.Update(viewport);

        Assert.Equal(viewport, state.Current);
    }
}
