using RobotCommand.Core;

namespace RobotCommand.Cli;

/// <summary>Read-only/headless commands for presentation workflows. Device mutation is deliberately absent.</summary>
internal static class WorkspaceCommands
{
    public const string Help = "media list|show|watch; evidence list|show|export; map package list|import|validate|activate|remove; team discover|probe|connect|status|disconnect; settings show|language|bright|units";

    public static async Task<bool> ExecuteAsync(string group, CliArguments args, IEvidenceWorkflow evidence, IMediaWorkflow media, IMapLibraryWorkflow maps, ITeamObserverClientWorkflow team, IApplicationPreferencesWorkflow preferences, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var command = args.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "";
        switch (group)
        {
            case "media":
                if (command is "list" or "show" or "watch")
                {
                    if (command == "watch") media.Changed += (_, _) => reporter.Event("media", media.Current);
                    await media.RefreshAsync(cancellationToken); reporter.Event("media", media.Current); return true;
                }
                break;
            case "evidence":
                if (command == "list") { await evidence.RefreshAsync(cancellationToken); reporter.Event("evidence", evidence.Current); return true; }
                if (command == "show") { var id = Required(args, 1, "Evidence ID is required."); reporter.Event("evidence", evidence.Current.Items.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException($"Evidence '{id}' was not found.")); return true; }
                if (command == "export") { await evidence.ExportAsync(Required(args, 1, "Evidence ID is required."), Required(args, 2, "Destination file is required."), cancellationToken); reporter.Info("Evidence exported."); return true; }
                break;
            case "map":
                if (!string.Equals(command, "package", StringComparison.OrdinalIgnoreCase)) break;
                var packageCommand = Required(args, 1, "Map package command is required.").ToLowerInvariant();
                if (packageCommand == "list") { await maps.RefreshAsync(cancellationToken); reporter.Event("map.packages", maps.Current); return true; }
                if (packageCommand == "import") { reporter.Event("map.package", await maps.ImportAsync(Required(args, 2, "Package path is required."), cancellationToken)); return true; }
                if (packageCommand == "validate") { reporter.Event("map.package", await maps.ValidateAsync(Required(args, 2, "Package key is required."), cancellationToken)); return true; }
                if (packageCommand == "activate") { await maps.ActivateAsync(Required(args, 2, "Package key is required."), cancellationToken); reporter.Event("map.packages", maps.Current); return true; }
                if (packageCommand == "remove") { await maps.RemoveAsync(Required(args, 2, "Package key is required."), cancellationToken); reporter.Event("map.packages", maps.Current); return true; }
                break;
            case "team":
                if (command == "discover") { await team.DiscoverAsync(cancellationToken); reporter.Event("team", team.Current); return true; }
                if (command == "probe") { await team.ProbeAsync(Required(args, 1, "Endpoint is required."), cancellationToken); reporter.Event("team", team.Current); return true; }
                if (command == "connect") { await team.ConnectAsync(Required(args, 1, "Endpoint is required."), args.Get("name") ?? "Observer", args.Get("passphrase"), cancellationToken); reporter.Event("team", team.Current); return true; }
                if (command == "status") { reporter.Event("team", team.Current); return true; }
                if (command == "disconnect") { await team.DisconnectAsync(Required(args, 1, "Observer ID is required."), null, cancellationToken); reporter.Event("team", team.Current); return true; }
                break;
            case "settings":
                if (command == "show") { reporter.Event("settings", preferences.Current); return true; }
                if (command == "language") { await preferences.SetLanguageAsync(Required(args, 1, "Language is required."), cancellationToken); reporter.Event("settings", preferences.Current); return true; }
                if (command == "bright") { var enabled = Required(args, 1, "Bright mode value is required.").Equals("on", StringComparison.OrdinalIgnoreCase); await preferences.SetBrightModeAsync(enabled, cancellationToken); reporter.Event("settings", preferences.Current); return true; }
                if (command == "units") { await preferences.SetUnitsAsync(args.Get("horizontal") ?? preferences.Current.HorizontalDistance, args.Get("vertical") ?? preferences.Current.VerticalDistance, args.Get("area") ?? preferences.Current.Area, args.Get("speed") ?? preferences.Current.Speed, args.Get("temperature") ?? preferences.Current.Temperature, cancellationToken); reporter.Event("settings", preferences.Current); return true; }
                break;
        }
        throw new ArgumentException($"Unknown {group} command.");
    }

    private static string Required(CliArguments args, int index, string message) => args.Positionals.Count > index ? args.Positionals[index] : throw new ArgumentException(message);
}
