using RobotCommand.Simulation;
using Xunit;

namespace RobotCommand.Tests.Simulation;

public sealed class GhostSimulationEngineTests
{
    [Fact]
    public void TwentyFiveGhostsAdvanceFromOneSharedEngine()
    {
        var engine = new GhostSimulationEngine("test-worker");
        var ids = Enumerable.Range(0, 25)
            .Select(index => engine.Apply(new($"create-{index}", SimulationCommandKind.Create, LatitudeDegrees: 43.65 + index * 0.0001, LongitudeDegrees: -79.38)).Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd('.'))
            .ToArray();

        Assert.Equal(25, engine.Count);
        foreach (var id in ids)
        {
            Assert.True(engine.Apply(new($"arm-{id}", SimulationCommandKind.Arm, id)).Accepted);
            Assert.True(engine.Apply(new($"takeoff-{id}", SimulationCommandKind.Takeoff, id, AltitudeAglMetres: 10)).Accepted);
        }

        for (var tick = 0; tick < 600; tick++) engine.Tick();
        var snapshot = engine.Snapshot();

        Assert.Equal(25, snapshot.Ghosts.Count);
        Assert.All(snapshot.Ghosts, ghost => Assert.True(ghost.AltitudeAglMetres > 0));
    }

    [Fact]
    public void SharedSnapshotRoundTripsAndRejectsPartialData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"robotcommand-snapshot-{Guid.NewGuid():N}.bin");
        try
        {
            var engine = new GhostSimulationEngine("snapshot-worker");
            engine.Apply(new("create", SimulationCommandKind.Create));
            using var writer = SharedSnapshotChannel.CreateWriter(path);
            writer.Write(engine.Snapshot());
            using var reader = SharedSnapshotChannel.OpenReader(path);

            Assert.True(reader.TryRead(out var snapshot));
            Assert.NotNull(snapshot);
            Assert.Equal("snapshot-worker", snapshot!.WorkerInstanceId);
            Assert.Single(snapshot.Ghosts);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RoutePauseResumeAndDeleteAreSafe()
    {
        var engine = new GhostSimulationEngine();
        var created = engine.Apply(new("create", SimulationCommandKind.Create));
        var id = created.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd('.');
        Assert.True(engine.Apply(new("arm", SimulationCommandKind.Arm, id)).Accepted);
        Assert.True(engine.Apply(new("route", SimulationCommandKind.StartRoute, id, Route: [new(43.651, -79.38, 5), new(43.652, -79.38, 5)])).Accepted);
        Assert.True(engine.Apply(new("pause", SimulationCommandKind.PauseRoute, id)).Accepted);
        Assert.True(engine.Apply(new("resume", SimulationCommandKind.ResumeRoute, id)).Accepted);
        Assert.True(engine.Apply(new("delete", SimulationCommandKind.Delete, id)).Accepted);
        Assert.Equal(0, engine.Count);
    }
}
