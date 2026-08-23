using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class LocalVideoTimelineTests
{
    [Fact]
    public void SegmentAt_MapsNormalizedPositionAcrossTimelineRange()
    {
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        var segments = new[]
        {
            Segment("one", start, start.AddSeconds(10)),
            Segment("two", start.AddSeconds(10), start.AddSeconds(20)),
            Segment("three", start.AddSeconds(20), start.AddSeconds(30))
        };
        var timeline = new LocalVideoTimelineSnapshot(
            segments,
            start,
            start.AddSeconds(30),
            start.AddSeconds(30),
            LocalVideoTimelineMode.Live,
            null,
            1,
            300,
            start.AddSeconds(30));

        Assert.Equal("one", timeline.SegmentAt(0.1)!.Id);
        Assert.Equal("two", timeline.SegmentAt(0.5)!.Id);
        Assert.Equal("three", timeline.SegmentAt(0.95)!.Id);
    }


    [Fact]
    public void SegmentAt_ReturnsNullInsideRecordingGap()
    {
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        var timeline = new LocalVideoTimelineSnapshot(
            [
                Segment("one", start, start.AddSeconds(10)),
                Segment("two", start.AddSeconds(20), start.AddSeconds(30))
            ],
            start,
            start.AddSeconds(30),
            start.AddSeconds(30),
            LocalVideoTimelineMode.Live,
            null,
            1,
            200,
            start.AddSeconds(30));

        Assert.Null(timeline.SegmentAt(0.5));
    }

    [Fact]
    public void SegmentAt_ReturnsNullForEmptyTimeline()
    {
        Assert.Null(LocalVideoTimelineSnapshot.Empty.SegmentAt(0.5));
    }

    private static LocalVideoSegment Segment(string id, DateTimeOffset start, DateTimeOffset end)
        => new(
            id,
            "session",
            id + ".mkv",
            "front",
            "stream",
            "RTSP",
            start,
            end,
            640,
            360,
            30,
            100,
            false,
            true,
            false);
}
