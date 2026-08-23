using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class UnifiedVideoTimelineTests
{
    [Fact]
    public void ItemAt_PrefersVehicleRecordingWhenSourcesOverlap()
    {
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        var snapshot = new UnifiedVideoTimelineSnapshot(
            [
                Item("console", VideoTimelineItemOrigin.ConsoleRolling, start, start.AddMinutes(1)),
                Item("vehicle", VideoTimelineItemOrigin.VehicleRecording, start, start.AddMinutes(1))
            ],
            start,
            start.AddMinutes(1),
            start.AddMinutes(1),
            LocalVideoTimelineMode.Live,
            null,
            1,
            100,
            0,
            start.AddMinutes(1));

        Assert.Equal("vehicle", snapshot.ItemAt(0.5)!.Id);
    }

    [Fact]
    public void ItemAt_ReturnsNullInsideGap()
    {
        var start = DateTimeOffset.Parse("2026-07-25T12:00:00Z");
        var snapshot = new UnifiedVideoTimelineSnapshot(
            [
                Item("one", VideoTimelineItemOrigin.ConsoleRolling, start, start.AddSeconds(10)),
                Item("two", VideoTimelineItemOrigin.VehicleRecording, start.AddSeconds(20), start.AddSeconds(30))
            ],
            start,
            start.AddSeconds(30),
            start.AddSeconds(30),
            LocalVideoTimelineMode.Live,
            null,
            1,
            100,
            0,
            start.AddSeconds(30));

        Assert.Null(snapshot.ItemAt(0.5));
    }

    [Fact]
    public void RemoteProfile_MatchesConnectionIdOrNormalizedTarget()
    {
        var definition = ConnectionDefinition.CreateDirect("Dracula", "http://localhost:50051");
        var byId = new RemoteVideoRecordingProfile(
            "id", "http://localhost:9996", "camera/{cameraSourceId}", ConnectionId: definition.Id);
        var byTarget = new RemoteVideoRecordingProfile(
            "target", "http://localhost:9996", "camera/{cameraSourceId}", ConnectionTarget: "http://localhost:50051/");

        Assert.True(byId.Matches(definition));
        Assert.True(byTarget.Matches(definition));
    }

    private static VideoTimelineItem Item(
        string id,
        VideoTimelineItemOrigin origin,
        DateTimeOffset start,
        DateTimeOffset end)
        => new(
            id,
            origin,
            origin == VideoTimelineItemOrigin.VehicleRecording
                ? VideoTimelineItemAvailability.Remote
                : VideoTimelineItemAvailability.Local,
            start,
            end,
            "front",
            "stream",
            "RTSP",
            640,
            360,
            30,
            100,
            false,
            false,
            SourceId: id);
}
