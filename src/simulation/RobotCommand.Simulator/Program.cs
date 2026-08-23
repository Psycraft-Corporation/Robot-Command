using System.Diagnostics;
using RobotCommand.Simulation;

namespace RobotCommand.Simulator;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var snapshotPath = Option(args, "--snapshot");
        var endpoint = Option(args, "--endpoint");
        if (string.IsNullOrWhiteSpace(snapshotPath) || string.IsNullOrWhiteSpace(endpoint))
        {
            Console.Error.WriteLine("Usage: RobotCommand.Simulator --snapshot <path> --endpoint <pipe-name-or-socket-path>");
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        var engine = new GhostSimulationEngine();
        using var snapshots = SharedSnapshotChannel.CreateWriter(snapshotPath);
        await using var control = new LocalControlServer(endpoint, command =>
        {
            var result = engine.Apply(command);
            var sequence = engine.Snapshot().Sequence;
            if (command.Kind == SimulationCommandKind.Shutdown) stopping.Cancel();
            return Task.FromResult(result with { Sequence = sequence });
        });
        await control.StartAsync(stopping.Token);
        snapshots.Write(engine.Snapshot());

        var tickTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000d / 60d));
        var publishTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var lastTick = Stopwatch.GetTimestamp();
        try
        {
            var tick = tickTimer.WaitForNextTickAsync(stopping.Token).AsTask();
            var publish = publishTimer.WaitForNextTickAsync(stopping.Token).AsTask();
            while (!stopping.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(tick, publish);
                if (completed == tick)
                {
                    if (!await tick) break;
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = (now - lastTick) / (double)Stopwatch.Frequency;
                    lastTick = now;
                    engine.Tick(Math.Clamp(elapsed, 0.001, 0.1));
                    tick = tickTimer.WaitForNextTickAsync(stopping.Token).AsTask();
                }
                else
                {
                    if (!await publish) break;
                    snapshots.Write(engine.Snapshot());
                    publish = publishTimer.WaitForNextTickAsync(stopping.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            tickTimer.Dispose();
            publishTimer.Dispose();
        }
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }
}
