using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Services.Simulation;
using RobotCommand.Simulation;

namespace RobotCommand.Cli;

internal static class SimulationCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var count = IntegerOption(args, "--count", 25);
        var seconds = IntegerOption(args, "--seconds", 10);
        var json = args.Any(item => string.Equals(item, "--json", StringComparison.OrdinalIgnoreCase));
        if (count is < 1 or > 128 || seconds is < 1 or > 3600)
        {
            Console.Error.WriteLine("simulation worker requires --count between 1 and 128 and --seconds between 1 and 3600.");
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        using var host = RobotCommandRuntimeHost.Build(AppContext.BaseDirectory, RobotCommandRuntimeMode.Headless);
        await host.StartAsync(stopping.Token);
        var worker = host.Services.GetRequiredService<IGhostSimulationWorkerSupervisor>();
        try
        {
            await worker.StartAsync(stopping.Token);
            var ids = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var created = await worker.SendAsync(new($"create-{index}", SimulationCommandKind.Create,
                    LatitudeDegrees: 43.65 + index * 0.00005, LongitudeDegrees: -79.38), stopping.Token);
                if (!created.Accepted) throw new InvalidOperationException(created.Message);
                ids.Add(created.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd('.'));
            }
            foreach (var id in ids)
            {
                await worker.SendAsync(new($"arm-{id}", SimulationCommandKind.Arm, id), stopping.Token);
                await worker.SendAsync(new($"takeoff-{id}", SimulationCommandKind.Takeoff, id, AltitudeAglMetres: 10), stopping.Token);
            }

            var started = DateTimeOffset.UtcNow;
            while (!stopping.IsCancellationRequested && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(seconds))
            {
                worker.RefreshSnapshot();
                if (worker.LatestSnapshot is { } snapshot)
                {
                    if (json) Console.WriteLine(JsonSerializer.Serialize(snapshot));
                    else Console.WriteLine($"worker sequence={snapshot.Sequence} ghosts={snapshot.Ghosts.Count} heartbeat={snapshot.HeartbeatAt:O} ticks={snapshot.Metrics?.PhysicsTicks ?? 0} missed={snapshot.Metrics?.MissedDeadlines ?? 0} p95TickMs={snapshot.Metrics?.PhysicsP95Milliseconds ?? 0:0.###} maxTickMs={snapshot.Metrics?.MaxTickMilliseconds ?? 0:0.###} publications={snapshot.Metrics?.SnapshotPublications ?? 0}");
                }
                await Task.Delay(250, stopping.Token);
            }
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ghost simulation worker failed: {exception.Message}");
            return 1;
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static int IntegerOption(string[] args, string name, int fallback)
    {
        var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
    }
}
