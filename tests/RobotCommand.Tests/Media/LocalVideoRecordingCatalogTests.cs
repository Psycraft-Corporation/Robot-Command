using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class LocalVideoRecordingCatalogTests
{
    [Fact]
    public async Task LoadAsync_RecognizesFinalizedSegmentsAndRecordingGap()
    {
        using var fixture = new CatalogFixture();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await fixture.WriteSessionAsync("session-a", startedAt, segmentSeconds: 10);
        fixture.WriteSegment("session-a", 0, startedAt.AddSeconds(10));
        fixture.WriteSegment("session-a", 1, startedAt.AddSeconds(30));

        var segments = await fixture.Catalog.LoadAsync(StoppedContext());

        Assert.Equal(2, segments.Count);
        Assert.All(segments, segment => Assert.True(segment.Finalized));
        Assert.False(segments[0].GapBefore);
        Assert.True(segments[1].GapBefore);
    }

    [Fact]
    public async Task CleanupAndLoadAsync_RemovesExpiredUnretainedSegment()
    {
        using var fixture = new CatalogFixture(rollingMinutes: 1);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await fixture.WriteSessionAsync("session-old", startedAt, segmentSeconds: 10);
        fixture.WriteSegment("session-old", 0, startedAt.AddSeconds(10));

        var segments = await fixture.Catalog.CleanupAndLoadAsync(StoppedContext());

        Assert.Empty(segments);
        Assert.False(File.Exists(fixture.SegmentPath("session-old", 0)));
    }

    [Fact]
    public async Task RetainAsync_ProtectsExpiredSegmentFromCleanup()
    {
        using var fixture = new CatalogFixture(rollingMinutes: 1);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await fixture.WriteSessionAsync("session-retained", startedAt, segmentSeconds: 10);
        fixture.WriteSegment("session-retained", 0, startedAt.AddSeconds(10));
        var segment = Assert.Single(await fixture.Catalog.LoadAsync(StoppedContext()));

        await fixture.Catalog.RetainAsync(segment);
        var segments = await fixture.Catalog.CleanupAndLoadAsync(StoppedContext());

        var retained = Assert.Single(segments);
        Assert.True(retained.Retained);
        Assert.True(File.Exists(retained.Path));
    }

    [Fact]
    public async Task LoadAsync_DoesNotFinalizeInvalidMatroskaFile()
    {
        using var fixture = new CatalogFixture();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await fixture.WriteSessionAsync("session-invalid", startedAt, segmentSeconds: 10);
        var path = fixture.SegmentPath("session-invalid", 0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0, 1, 2, 3, 4, 5]);
        File.SetLastWriteTimeUtc(path, startedAt.AddSeconds(10).UtcDateTime);

        var segment = Assert.Single(await fixture.Catalog.LoadAsync(StoppedContext()));

        Assert.False(segment.Finalized);
    }

    [Fact]
    public async Task RetainAsync_RejectsPathOutsideRecordingRoot()
    {
        using var fixture = new CatalogFixture();
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv");
        await File.WriteAllBytesAsync(outside, [0x1A, 0x45, 0xDF, 0xA3]);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var segment = new LocalVideoSegment(
                "outside", "session", outside, "front", "stream", "RTSP",
                now.AddSeconds(-10), now, 640, 360, 30, 4, false, true, false);

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.RetainAsync(segment));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private static LocalVideoCatalogContext StoppedContext()
        => new(null, LocalVideoRecorderState.Stopped, null, null);

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root;

        public CatalogFixture(int rollingMinutes = 10)
        {
            _root = Path.Combine(Path.GetTempPath(), "logos-robot-command-video-tests", Guid.NewGuid().ToString("N"));
            Catalog = new LocalVideoRecordingCatalog(
                new AppConfiguration
                {
                    LocalVideoRecordingPath = _root,
                    LocalVideoRollingBufferMinutes = rollingMinutes,
                    LocalVideoMaximumStorageGigabytes = 1
                },
                NullLogger<LocalVideoRecordingCatalog>.Instance);
        }

        public LocalVideoRecordingCatalog Catalog { get; }

        public async Task WriteSessionAsync(string sessionId, DateTimeOffset startedAt, int segmentSeconds)
        {
            var directory = Path.Combine(_root, sessionId);
            await Catalog.WriteSessionAsync(
                directory,
                new LocalVideoSessionManifest(
                    LocalVideoRecordingCatalog.SessionSchema,
                    sessionId,
                    "front",
                    "stream-1",
                    "connection-1",
                    "RTSP",
                    startedAt,
                    640,
                    360,
                    30,
                    segmentSeconds));
        }

        public void WriteSegment(string sessionId, int index, DateTimeOffset lastWrite)
        {
            var path = SegmentPath(sessionId, index);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x1A, 0x45, 0xDF, 0xA3, 0, 1, 2, 3]);
            File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        }

        public string SegmentPath(string sessionId, int index)
            => Path.Combine(_root, sessionId, $"segment-{index:D5}.mkv");

        public void Dispose()
        {
            Catalog.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
