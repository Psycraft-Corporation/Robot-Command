using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RobotCommand.Models;

namespace RobotCommand.Services.Evidence;

public static class PngFrameEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeBgra(
        ReadOnlySpan<byte> pixels,
        VideoFrameInfo frame,
        VideoOverlayScene? overlay = null,
        bool includeOverlays = false)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }
        if (pixels.Length < frame.RequiredBytes)
        {
            throw new ArgumentException("The BGRA buffer is smaller than the declared frame.", nameof(pixels));
        }

        var working = pixels[..frame.RequiredBytes].ToArray();
        if (includeOverlays && overlay is not null)
        {
            DrawOverlay(working, frame, overlay);
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), frame.Height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream(checked((frame.Width * 4 + 1) * frame.Height));
        for (var y = 0; y < frame.Height; y++)
        {
            raw.WriteByte(0);
            var row = y * frame.Stride;
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = row + (x * 4);
                raw.WriteByte(working[offset + 2]);
                raw.WriteByte(working[offset + 1]);
                raw.WriteByte(working[offset]);
                raw.WriteByte(255);
            }
        }

        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.CopyTo(zlib);
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void DrawOverlay(
        byte[] pixels,
        VideoFrameInfo frame,
        VideoOverlayScene overlay)
    {
        foreach (var track in overlay.Tracks)
        {
            if (!double.IsFinite(track.CenterX) ||
                !double.IsFinite(track.CenterY) ||
                !double.IsFinite(track.Width) ||
                !double.IsFinite(track.Height))
            {
                continue;
            }

            var left = (int)Math.Round((track.CenterX - track.Width / 2) * frame.Width);
            var right = (int)Math.Round((track.CenterX + track.Width / 2) * frame.Width);
            var top = (int)Math.Round((track.CenterY - track.Height / 2) * frame.Height);
            var bottom = (int)Math.Round((track.CenterY + track.Height / 2) * frame.Height);
            left = Math.Clamp(left, 0, frame.Width - 1);
            right = Math.Clamp(right, 0, frame.Width - 1);
            top = Math.Clamp(top, 0, frame.Height - 1);
            bottom = Math.Clamp(bottom, 0, frame.Height - 1);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            var colour = track.Selected
                ? (B: (byte)255, G: (byte)255, R: (byte)255)
                : (B: (byte)35, G: (byte)186, R: (byte)255);
            for (var thickness = 0; thickness < 3; thickness++)
            {
                DrawHorizontal(pixels, frame, left, right, top + thickness, colour);
                DrawHorizontal(pixels, frame, left, right, bottom - thickness, colour);
                DrawVertical(pixels, frame, top, bottom, left + thickness, colour);
                DrawVertical(pixels, frame, top, bottom, right - thickness, colour);
            }
        }
    }

    private static void DrawHorizontal(
        byte[] pixels,
        VideoFrameInfo frame,
        int left,
        int right,
        int y,
        (byte B, byte G, byte R) colour)
    {
        if (y < 0 || y >= frame.Height) return;
        for (var x = left; x <= right; x++) SetPixel(pixels, frame, x, y, colour);
    }

    private static void DrawVertical(
        byte[] pixels,
        VideoFrameInfo frame,
        int top,
        int bottom,
        int x,
        (byte B, byte G, byte R) colour)
    {
        if (x < 0 || x >= frame.Width) return;
        for (var y = top; y <= bottom; y++) SetPixel(pixels, frame, x, y, colour);
    }

    private static void SetPixel(
        byte[] pixels,
        VideoFrameInfo frame,
        int x,
        int y,
        (byte B, byte G, byte R) colour)
    {
        if (x < 0 || x >= frame.Width || y < 0 || y >= frame.Height) return;
        var offset = y * frame.Stride + x * 4;
        pixels[offset] = colour.B;
        pixels[offset + 1] = colour.G;
        pixels[offset + 2] = colour.R;
        pixels[offset + 3] = 255;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes) crc = UpdateCrc(crc, value);
        foreach (var value in data) crc = UpdateCrc(crc, value);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, byte value)
        => CrcTable[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0; n < table.Length; n++)
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) == 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
