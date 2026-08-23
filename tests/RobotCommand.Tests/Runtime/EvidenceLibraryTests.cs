using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Evidence;
using Xunit;

namespace RobotCommand.Tests;

public sealed class EvidenceLibraryTests
{
    [Fact]
    public async Task CaptureDisplayedFrame_WritesPngAndMetadataWithoutEndpointSecrets()
    {
        var root = CreateTempDirectory();
        var library = new EvidenceLibrary(new AppConfiguration { EvidenceLibraryPath = root });
        var context = Context() with { Protocol = "RTSP" };
        var frame = new VideoFrameInfo(1, 1, 4, 1, DateTimeOffset.Parse("2026-07-25T12:00:00Z"));

        var record = await library.CaptureDisplayedFrameAsync(new DisplayedFrameCaptureRequest(
            [10, 20, 30, 255],
            frame,
            VideoOverlayScene.Empty,
            false,
            context));

        Assert.True(File.Exists(record.FilePath));
        Assert.True(File.Exists(record.MetadataPath));
        Assert.Equal(EvidenceKind.DisplayedFrame, record.Kind);
        Assert.Equal("image/png", record.MediaType);
        var metadata = await File.ReadAllTextAsync(record.MetadataPath);
        Assert.False(metadata.Contains("rtsp://", StringComparison.OrdinalIgnoreCase));
        Assert.False(metadata.Contains("token=", StringComparison.OrdinalIgnoreCase));
        using var json = JsonDocument.Parse(metadata);
        Assert.Equal(EvidenceManifest.CurrentSchema, json.RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public async Task ExportClip_CopiesRecordingAndRefreshesCatalogue()
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(CreateTempDirectory(), "segment.mkv");
        await File.WriteAllBytesAsync(source, [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4]);
        var library = new EvidenceLibrary(new AppConfiguration { EvidenceLibraryPath = root });
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");

        var record = await library.ExportClipAsync(new VideoClipExportRequest(
            source,
            start,
            start.AddSeconds(10),
            Context() with { TimelineItemId = "timeline-1" }));
        await library.RefreshAsync();

        Assert.Equal(EvidenceKind.VideoClip, record.Kind);
        Assert.True(record.FilePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(record.FilePath));
        Assert.Single(library.Snapshot.Items);
        Assert.Equal("timeline-1", library.Snapshot.Items[0].Context.TimelineItemId);
    }

    [Fact]
    public async Task Delete_RemovesMediaAndSidecar()
    {
        var root = CreateTempDirectory();
        var library = new EvidenceLibrary(new AppConfiguration { EvidenceLibraryPath = root });
        var frame = new VideoFrameInfo(1, 1, 4, 1, DateTimeOffset.UtcNow);
        var record = await library.CaptureDisplayedFrameAsync(new DisplayedFrameCaptureRequest(
            [0, 0, 0, 255], frame, VideoOverlayScene.Empty, false, Context()));

        await library.DeleteAsync(record.Id);

        Assert.False(File.Exists(record.FilePath));
        Assert.False(File.Exists(record.MetadataPath));
        Assert.Empty(library.Snapshot.Items);
    }

    [Fact]
    public async Task Refresh_IgnoresMalformedOrEscapingMetadata()
    {
        var root = CreateTempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(root, "bad.evidence.json"),
            """
            {
              "schema": "logos.evidence.v1",
              "id": "bad",
              "kind": "displayedFrame",
              "title": "bad",
              "fileName": "../outside.png",
              "mediaType": "image/png",
              "sizeBytes": 1,
              "sha256": "00",
              "createdAt": "2026-07-25T12:00:00Z",
              "includesOverlays": false,
              "context": null
            }
            """);
        var library = new EvidenceLibrary(new AppConfiguration { EvidenceLibraryPath = root });

        await library.RefreshAsync();

        Assert.Empty(library.Snapshot.Items);
    }

    private static EvidenceCaptureContext Context() => new(
        "connection",
        "logos",
        "vehicle",
        "Dracula",
        "front",
        "stream",
        "RTSP",
        "mission",
        "task",
        43.6,
        -79.4,
        100,
        20,
        90,
        null,
        DateTimeOffset.Parse("2026-07-25T12:00:00Z"));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "logos-evidence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
