using System.Buffers.Binary;
using System.IO.Compression;
using RobotCommand.Models;
using RobotCommand.Services.Evidence;
using Xunit;

namespace RobotCommand.Tests;

public sealed class PngFrameEncoderTests
{
    [Fact]
    public void EncodeBgra_WritesValidRgbaScanline()
    {
        byte[] bgra = [10, 20, 30, 255, 40, 50, 60, 255];
        var frame = new VideoFrameInfo(2, 1, 8, 1, DateTimeOffset.UtcNow);

        var png = PngFrameEncoder.EncodeBgra(bgra, frame);
        var scanline = InflateIdat(png);

        Assert.Equal(new byte[] { 0, 30, 20, 10, 255, 60, 50, 40, 255 }, scanline);
    }

    [Fact]
    public void EncodeBgra_DrawsOverlayWhenRequested()
    {
        var bgra = new byte[8 * 8 * 4];
        var frame = new VideoFrameInfo(8, 8, 32, 1, DateTimeOffset.UtcNow);
        var overlay = new VideoOverlayScene(
            "front",
            8,
            8,
            [new VideoTrackVisual("track", "person", 0.9, 0.5, 0.5, 0.5, 0.5)]);

        var png = PngFrameEncoder.EncodeBgra(bgra, frame, overlay, includeOverlays: true);
        var scanlines = InflateIdat(png);

        Assert.Contains(scanlines, value => value != 0);
    }

    [Fact]
    public void EncodeBgra_IgnoresNonFiniteOverlayCoordinates()
    {
        var bgra = new byte[4 * 4 * 4];
        var frame = new VideoFrameInfo(4, 4, 16, 1, DateTimeOffset.UtcNow);
        var overlay = new VideoOverlayScene(
            "front",
            4,
            4,
            [new VideoTrackVisual("track", "person", 0.9, double.NaN, 0.5, 0.5, 0.5)]);

        var png = PngFrameEncoder.EncodeBgra(bgra, frame, overlay, includeOverlays: true);
        var scanlines = InflateIdat(png);

        for (var row = 0; row < 4; row++)
        {
            var offset = row * 17;
            Assert.Equal(0, scanlines[offset]);
            for (var pixel = 0; pixel < 4; pixel++)
            {
                var channel = offset + 1 + pixel * 4;
                Assert.Equal(0, scanlines[channel]);
                Assert.Equal(0, scanlines[channel + 1]);
                Assert.Equal(0, scanlines[channel + 2]);
                Assert.Equal(255, scanlines[channel + 3]);
            }
        }
    }

    private static byte[] InflateIdat(byte[] png)
    {
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT") compressed.Write(png, offset + 8, length);
            offset += 12 + length;
            if (type == "IEND") break;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        return raw.ToArray();
    }
}
