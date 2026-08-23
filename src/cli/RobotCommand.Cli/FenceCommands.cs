using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

internal static class FenceCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length < 2) return Usage();
        var parsed = CliArguments.Parse(arguments[1..]);
        if (parsed.Error is not null) { Console.Error.WriteLine(parsed.Error); return 2; }
        var reporter = new ConsoleReporter(parsed.Has("json"));
        using var host = RobotCommandRuntimeHost.Build(parsed.Get("data-dir") ?? AppContext.BaseDirectory, RobotCommandRuntimeMode.Cli);
        try
        {
            await host.StartAsync(CancellationToken.None);
            var workflow = host.Services.GetRequiredService<IFenceWorkflow>();
            var reviewed = host.Services.GetRequiredService<IReviewedOperationWorkflow>();
            var command = arguments[1].ToLowerInvariant();
            switch (command)
            {
                case "list": reporter.Event("fence.list", new { workflow.Fences, workflow.Targets }); break;
                case "show": reporter.Event("fence.show", Find(workflow, Required(arguments, 2, "A fence ID is required."))); break;
                case "create-from-zone":
                    reporter.Event("fence.created", await workflow.CreateFromZoneAsync(parsed.Required("zone"), parsed.Get("name"), Kind(parsed.Get("kind"))));
                    break;
                case "validate":
                    reporter.Event("fence.validate", await workflow.ValidateAsync(Required(arguments, 2, "A fence ID is required."), parsed.Get("vehicle")));
                    break;
                case "import": reporter.Event("fence.imported", await workflow.ImportAsync(parsed.Required("path"), parsed.Has("replace"))); break;
                case "export":
                    await workflow.ExportAsync(Required(arguments, 2, "A fence ID is required."), parsed.Required("path"));
                    reporter.Event("fence.exported", new { Id = arguments[2], Path = parsed.Get("path") });
                    break;
                case "delete":
                    await workflow.DeleteAsync(Required(arguments, 2, "A fence ID is required."));
                    reporter.Event("fence.deleted", new { Id = arguments[2] });
                    break;
                case "upload":
                    await PlanAsync(await workflow.PlanUploadAsync(Required(arguments, 2, "A fence ID is required."), parsed.Get("connection") ?? string.Empty, parsed.Required("vehicle")), parsed, reviewed, reporter);
                    break;
                case "download":
                    await PlanAsync(await workflow.PlanDownloadAsync(parsed.Get("connection") ?? string.Empty, parsed.Required("vehicle")), parsed, reviewed, reporter);
                    break;
                case "clear":
                    await PlanAsync(await workflow.PlanClearAsync(parsed.Get("connection") ?? string.Empty, parsed.Required("vehicle")), parsed, reviewed, reporter);
                    break;
                case "watch": reporter.Event("fence.watch", new { workflow.Fences, workflow.Targets }); break;
                default: return Usage();
            }
            return 0;
        }
        catch (Exception exception) { reporter.Error(exception.Message); return 1; }
        finally { try { await host.StopAsync(CancellationToken.None); } catch { } }
    }

    private static async Task PlanAsync(ReviewedOperationSnapshot plan, CliArguments args, IReviewedOperationWorkflow reviewed, ConsoleReporter reporter)
    {
        reporter.Event("fence.plan", plan);
        if (!args.Has("execute")) return;
        var result = await reviewed.ExecuteAsync(plan.Id);
        reporter.Event("fence.executed", result);
    }

    private static FenceKind Kind(string? value)
        => string.Equals(value, "exclusion", StringComparison.OrdinalIgnoreCase) ? FenceKind.Exclusion : FenceKind.Inclusion;

    private static FenceSnapshot Find(IFenceWorkflow workflow, string id)
        => workflow.Fences.FirstOrDefault(item => item.Document.FenceId == id)
           ?? throw new KeyNotFoundException("Fence was not found.");

    private static string Required(string[] arguments, int index, string message)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException(message);

    private static int Usage()
    {
        Console.Error.WriteLine("Fence commands: fence list|show <id>|create-from-zone --zone <id> [--kind inclusion|exclusion]|validate <id> [--vehicle <id>]|import --path <file>|export <id> --path <file>|delete <id>|upload <id> --vehicle <id> [--connection <id>] [--execute]|download --vehicle <id> [--connection <id>] [--execute]|clear --vehicle <id> [--connection <id>] [--execute]|watch.");
        return 2;
    }
}
