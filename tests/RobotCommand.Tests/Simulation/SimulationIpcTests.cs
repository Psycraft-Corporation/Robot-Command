using RobotCommand.Simulation;
using Xunit;

namespace RobotCommand.Tests.Simulation;

public sealed class SimulationIpcTests
{
    [Fact]
    public void SharedSnapshotChannelRoundTripsNewestSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robotcommand-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "snapshot.bin");
        try
        {
            using var writer = SharedSnapshotChannel.CreateWriter(path);
            using var reader = SharedSnapshotChannel.OpenReader(path);
            var first = new SimulationSnapshot(1, "worker", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
            var second = first with { Sequence = 2, Ghosts = [new("ghost-1", "Ghost 1", 43, -79, 1, 90, 0, 0, 0, true, false, "Takeoff", "Takeoff", 0, 1)] };

            writer.Write(first);
            Assert.True(reader.TryRead(out var actualFirst));
            Assert.Equal(1, actualFirst!.Sequence);
            writer.Write(second);
            Assert.True(reader.TryRead(out var actualSecond));
            Assert.Equal(2, actualSecond!.Sequence);
            Assert.Single(actualSecond.Ghosts);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LocalControlRoundTripsCommandAndResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"robotcommand-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var endpoint = OperatingSystem.IsWindows()
            ? $"robotcommand-test-{Guid.NewGuid():N}"
            : Path.Combine(directory, "control.sock");
        var server = new LocalControlServer(endpoint, command => Task.FromResult(
            new SimulationCommandResult(command.RequestId, true, "OK", "accepted", 7)));
        try
        {
            await server.StartAsync();
            await using var client = new LocalControlClient(endpoint);
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var response = await client.SendAsync(new("request-1", SimulationCommandKind.Hold, "ghost-1"));

            Assert.True(response.Accepted);
            Assert.Equal("request-1", response.RequestId);
            Assert.Equal(7, response.Sequence);
        }
        finally
        {
            await server.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
