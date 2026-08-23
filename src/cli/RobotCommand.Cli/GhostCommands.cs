using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Core;

namespace RobotCommand.Cli;

internal static class GhostCommands
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length < 2)
            return Usage();

        var parsed = CliArguments.Parse(arguments[2..]);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        var dataDirectory = parsed.Get("data-dir") ?? AppContext.BaseDirectory;
        var reporter = new ConsoleReporter(parsed.Has("json"));
        using var host = RobotCommandRuntimeHost.Build(dataDirectory, RobotCommandRuntimeMode.Cli);
        var profiles = host.Services.GetRequiredService<IGhostProfileWorkflow>();
        var assets = host.Services.GetRequiredService<IGhostProfileAssetWorkflow>();
        var command = arguments[1].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "profile":
                    return await ProfileAsync(arguments, parsed, profiles, assets, reporter, CancellationToken.None);
                case "create":
                    var profileId = parsed.Get("profile") ?? throw new ArgumentException("--profile is required. Choose one with 'ghost profile list'.");
                    var profile = profiles.Find(profileId)
                        ?? throw new ArgumentException($"Unknown Ghost profile '{profileId}'. Choose one with 'ghost profile list'.");
                    var count = parsed.Int("count", 1) ?? 1;
                    await host.StartAsync(CancellationToken.None);
                    var ghosts = host.Services.GetRequiredService<IGhostUnitWorkflow>();
                    var created = await ghosts.CreateAsync(new GhostCreateRequest(profile.Id, count), CancellationToken.None);
                    reporter.Event("ghost.created", created);
                    return 0;
                case "list":
                    await host.StartAsync(CancellationToken.None);
                    reporter.Event("ghost.list", host.Services.GetRequiredService<IGhostUnitWorkflow>().Ghosts);
                    return 0;
                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            reporter.Error(exception.Message);
            return 1;
        }
        finally
        {
            try { await host.StopAsync(CancellationToken.None); }
            catch { /* process teardown remains best effort for one-shot CLI commands */ }
        }
    }

    private static async Task<int> ProfileAsync(string[] arguments, CliArguments parsed, IGhostProfileWorkflow profiles, IGhostProfileAssetWorkflow assets, ConsoleReporter reporter, CancellationToken cancellationToken)
    {
        var command = arguments.Length > 2 ? arguments[2].ToLowerInvariant() : "list";
        if (command == "asset")
        {
            var assetCommand = arguments.Length > 3 ? arguments[3].ToLowerInvariant() : "show";
            var profileId = arguments.Length > 4 ? arguments[4] : throw new ArgumentException("A Ghost profile ID is required.");
            switch (assetCommand)
            {
                case "show": reporter.Event("ghost.profile.asset.show", assets.Find(profileId)); return 0;
                case "upload":
                    var path = arguments.Length > 5 ? arguments[5] : throw new ArgumentException("An asset path is required.");
                    reporter.Event("ghost.profile.asset.uploaded", await assets.ImportAsync(profileId, path, cancellationToken)); return 0;
                case "remove": await assets.RemoveAsync(profileId, cancellationToken); reporter.Event("ghost.profile.asset.removed", new { Id = profileId }); return 0;
                default: throw new ArgumentException("Ghost profile asset command must be show, upload, or remove.");
            }
        }
        switch (command)
        {
            case "list":
                reporter.Event("ghost.profile.list", profiles.Profiles);
                return 0;
            case "show":
                if (arguments.Length < 4)
                    throw new ArgumentException("A Ghost profile ID is required.");
                var profile = profiles.Find(arguments[3])
                    ?? throw new ArgumentException($"Unknown Ghost profile '{arguments[3]}'.");
                reporter.Event("ghost.profile.show", profile);
                return 0;
            case "create":
                var created = await profiles.CreateAsync(new(
                    parsed.Required("name"),
                    GhostProfileCliParsing.Simulation(parsed, GhostProfileDefaults.Dracula.Simulation)), cancellationToken);
                reporter.Event("ghost.profile.created", created);
                return 0;
            case "update":
                if (arguments.Length < 4) throw new ArgumentException("A Ghost profile ID is required.");
                var current = profiles.Find(arguments[3]) ?? throw new ArgumentException($"Unknown Ghost profile '{arguments[3]}'.");
                var updated = await profiles.UpdateAsync(current.Id, new(
                    parsed.Get("name") ?? current.Name,
                    GhostProfileCliParsing.Simulation(parsed, current.Simulation)), cancellationToken);
                reporter.Event("ghost.profile.updated", updated);
                return 0;
            case "delete":
                if (arguments.Length < 4) throw new ArgumentException("A Ghost profile ID is required.");
                await profiles.DeleteAsync(arguments[3], cancellationToken);
                reporter.Event("ghost.profile.deleted", new { Id = arguments[3] });
                return 0;
            default:
                throw new ArgumentException("Ghost profile command must be list, show, create, update, delete, or asset.");
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Ghost commands: ghost profile list|show|create|update|delete; ghost profile asset show|upload|remove; ghost list; ghost create --profile <profile-id> [--count <n>].");
        return 2;
    }
}

internal static class GhostProfileCliParsing
{
    public static GhostSimulationStats Simulation(CliArguments args, GhostSimulationStats defaults)
        => new(
            Number(args, "max-speed", defaults.MaximumHorizontalSpeedMetresPerSecond),
            Number(args, "climb-rate", defaults.MaximumClimbRateMetresPerSecond),
            Number(args, "descent-rate", defaults.MaximumDescentRateMetresPerSecond),
            Number(args, "horizontal-acceleration", defaults.HorizontalAccelerationMetresPerSecondSquared),
            Number(args, "vertical-acceleration", defaults.VerticalAccelerationMetresPerSecondSquared),
            Number(args, "max-yaw-rate", defaults.MaximumYawRateDegreesPerSecond),
            Number(args, "altitude-limit", defaults.MaximumAltitudeAglMetres),
            Number(args, "endurance", defaults.NominalEnduranceMinutes));

    private static double Number(CliArguments args, string key, double fallback)
        => args.Get(key) is not { } raw
            ? fallback
            : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? value
                : throw new ArgumentException($"--{key} must be a finite number.");
}
