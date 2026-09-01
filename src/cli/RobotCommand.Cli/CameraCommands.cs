using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Core;
using RobotCommand.Services.Mavlink;

namespace RobotCommand.Cli;

/// <summary>Small explicit CLI surface for immediate MAVLink camera controls.</summary>
internal static class CameraCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var parsed = CliArguments.Parse(arguments[1..]);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        var actionName = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        var connectionId = parsed.Get("connection") ?? throw new ArgumentException("--connection is required.");
        var vehicleId = parsed.Get("unit") ?? throw new ArgumentException("--unit is required.");
        var action = ParseAction(actionName, parsed);
        var reporter = new ConsoleReporter(parsed.Has("json"));
        using var stopping = ConsoleCancellation.Create();
        using var host = RobotCommandRuntimeHost.Build(
            parsed.Get("data-dir") ?? AppContext.BaseDirectory,
            RobotCommandRuntimeMode.Cli);
        var connections = host.Services.GetRequiredService<IConnectionManagementWorkflow>();
        var lifecycle = host.Services.GetRequiredService<IConnectionRuntimeLifecycle>();
        var control = host.Services.GetRequiredService<IMavlinkCameraControlService>();

        try
        {
            await connections.ConnectAsync(connectionId, null, stopping.Token);
            await lifecycle.StartAsync(false, connectionId, stopping.Token);
            await WaitForComponentDiscoveryAsync(stopping.Token);
            var result = await control.ExecuteAsync(
                connectionId,
                vehicleId,
                parsed.Get("camera"),
                action,
                stopping.Token);
            reporter.Event("camera.command", result);
            return result.Accepted ? 0 : 1;
        }
        catch (ArgumentException exception)
        {
            reporter.Error(exception.Message);
            return 2;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            reporter.Error(exception.Message);
            return 1;
        }
        finally
        {
            await lifecycle.DisposeAsync();
        }
    }

    private static async Task WaitForComponentDiscoveryAsync(CancellationToken cancellationToken)
    {
        // The control service remains authoritative. This short wait only gives
        // a serial camera/gimbal time to announce itself after connection.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }

    private static FlightMissionCameraAction ParseAction(string? value, CliArguments args)
        => value switch
        {
            "photo" or "photo-once" => FlightMissionCameraAction.PhotoOnce(),
            "photo-by-time" => FlightMissionCameraAction.PhotoByTime(RequiredNumber(args, "interval")),
            "photo-by-distance" => FlightMissionCameraAction.PhotoByDistance(RequiredNumber(args, "distance")),
            "stop-photos" => FlightMissionCameraAction.StopPhotos(),
            "start-video" => FlightMissionCameraAction.StartVideo(),
            "stop-video" => FlightMissionCameraAction.StopVideo(),
            "mode-photo" => FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Photo),
            "mode-video" => FlightMissionCameraAction.SetCameraMode(FlightMissionCameraMode.Video),
            "center-gimbal" => FlightMissionCameraAction.SetGimbal(0, 0),
            "gimbal" => FlightMissionCameraAction.SetGimbal(
                Number(args, "pitch"), Number(args, "yaw"), Number(args, "roll"),
                args.Get("frame")?.Equals("earth", StringComparison.OrdinalIgnoreCase) == true
                    ? FlightMissionGimbalFrame.Earth
                    : FlightMissionGimbalFrame.Vehicle),
            _ => throw new ArgumentException(
                "Camera action must be photo, photo-by-time, photo-by-distance, stop-photos, start-video, stop-video, mode-photo, mode-video, center-gimbal, or gimbal.")
        };

    private static double? Number(CliArguments args, string name)
    {
        if (args.Get(name) is not { } value) return null;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"--{name} must be a number.");
    }

    private static double RequiredNumber(CliArguments args, string name)
        => Number(args, name) ?? throw new ArgumentException($"--{name} is required.");
}
