using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

internal static class ThreeDCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var options = CliArguments.Parse(arguments);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            return 2;
        }

        var reporter = new ConsoleReporter(options.Has("json"));
        var dataDirectory = options.Get("data-dir") ?? AppContext.BaseDirectory;
        using var stopping = ConsoleCancellation.Create();
        using var host = RobotCommandRuntimeHost.Build(dataDirectory, RobotCommandRuntimeMode.Cli);
        try
        {
            await host.StartAsync(stopping.Token);
            var workflow = host.Services.GetRequiredService<IThreeDSceneWorkflow>();
            var scene = workflow.Current;
            reporter.Event("3d.status", new
            {
                scene.Revision,
                scene.CapturedAt,
                scene.Origin,
                scene.Camera,
                PrimitiveCount = scene.Primitives.Count,
                LineCount = scene.Lines.Count,
                workflow.BackendPolicy,
                Renderer = scene.RendererStatus
            });
            return 0;
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
            await host.StopAsync(CancellationToken.None);
        }
    }
}
