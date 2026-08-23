using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

internal static class FormationCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length < 2) return Usage();
        var parsed = CliArguments.Parse(arguments[1..]);
        if (parsed.Error is not null) { Console.Error.WriteLine(parsed.Error); return 2; }
        var reporter = new ConsoleReporter(parsed.Has("json"));
        using var host = RobotCommandRuntimeHost.Build(parsed.Get("data-dir") ?? AppContext.BaseDirectory, RobotCommandRuntimeMode.Cli);
        var workflow = host.Services.GetRequiredService<IFormationAuthoringWorkflow>();
        try
        {
            var command = arguments[1].ToLowerInvariant();
            switch (command)
            {
                case "list": reporter.Event("formation.list", new { workflow.Formations, workflow.LibraryIssues }); break;
                case "show": reporter.Event("formation.show", Find(workflow, arguments, 2)); break;
                case "create": reporter.Event("formation.created", await workflow.CreateAsync(new(parsed.Required("name")))); break;
                case "rename": reporter.Event("formation.renamed", await workflow.RenameAsync(RequiredId(arguments, 2), parsed.Required("name"))); break;
                case "delete": await workflow.RemoveAsync(RequiredId(arguments, 2)); reporter.Event("formation.deleted", new { Id = RequiredId(arguments, 2) }); break;
                case "member": await MemberAsync(arguments, parsed, workflow, reporter); break;
                default: return Usage();
            }
            return 0;
        }
        catch (Exception exception) { reporter.Error(exception.Message); return 1; }
        finally { try { await host.StopAsync(CancellationToken.None); } catch { } }
    }

    private static async Task MemberAsync(string[] arguments, CliArguments parsed, IFormationAuthoringWorkflow workflow, ConsoleReporter reporter)
    {
        var action = arguments.Length > 2 ? arguments[2].ToLowerInvariant() : throw new ArgumentException("A formation member command is required.");
        var id = RequiredId(arguments, 3);
        switch (action)
        {
            case "add": reporter.Event("formation.member.added", await workflow.AddMemberAsync(id, Request(parsed))); break;
            case "set": reporter.Event("formation.member.updated", await workflow.UpdateMemberAsync(id, arguments.Length > 4 ? arguments[4] : throw new ArgumentException("A member ID is required."), Request(parsed))); break;
            case "remove": await workflow.RemoveMemberAsync(id, arguments.Length > 4 ? arguments[4] : throw new ArgumentException("A member ID is required.")); reporter.Event("formation.member.removed", new { FormationId = id }); break;
            default: throw new ArgumentException("Formation member command must be add, set, or remove.");
        }
    }
    private static FormationMemberRequest Request(CliArguments args)
        => new(args.Get("name"), Number(args, "x"), Number(args, "y"), Number(args, "z"));
    private static double Number(CliArguments args, string key)
        => double.TryParse(args.Required(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new ArgumentException($"--{key} must be a finite number.");
    private static FormationWorkflowSnapshot Find(IFormationAuthoringWorkflow workflow, string[] arguments, int index)
        => workflow.TryGet(RequiredId(arguments, index), out var result) && result is not null ? result : throw new KeyNotFoundException("Formation was not found.");
    private static string RequiredId(string[] args, int index) => args.Length > index ? args[index] : throw new ArgumentException("A formation ID is required.");
    private static int Usage() { Console.Error.WriteLine("Formation commands: formation list|show <id>|create --name <name>|rename <id> --name <name>|delete <id>|member add <id> --x <east> --y <up> --z <north>|member set <id> <member-id> --x <east> --y <up> --z <north>|member remove <id> <member-id>."); return 2; }
}
