using System.Text.Json;
using System.Text.Json.Nodes;
using RobotCommand.Models;

namespace RobotCommand.Services.Team;

public interface ITeamServerSettingsService
{
    TeamServerSettings Current { get; }
    event EventHandler? Changed;
    Task SaveAsync(TeamServerSettings settings, CancellationToken cancellationToken = default);
}

public sealed class TeamServerSettingsService : ITeamServerSettingsService
{
    private readonly object _gate = new();
    private readonly string _path;
    private TeamServerSettings _current;

    public TeamServerSettingsService(string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, "appsettings.local.json");
        _current = Load(_path);
    }

    public TeamServerSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public async Task SaveAsync(TeamServerSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        JsonObject root;
        if (File.Exists(_path))
        {
            await using var input = File.OpenRead(_path);
            root = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken) as JsonObject ?? [];
        }
        else
        {
            root = [];
        }

        root["teamServer"] = new JsonObject
        {
            ["displayName"] = normalized.DisplayName,
            ["port"] = normalized.Port,
            ["maximumClients"] = normalized.MaximumClients,
            ["requirePassphrase"] = normalized.RequirePassphrase
        };

        var temporary = _path + ".team.tmp";
        await File.WriteAllTextAsync(
            temporary,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        if (File.Exists(_path)) File.Replace(temporary, _path, null);
        else File.Move(temporary, _path);

        lock (_gate) _current = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static TeamServerSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return TeamServerSettings.Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (!document.RootElement.TryGetProperty("teamServer", out var element) ||
                element.ValueKind != JsonValueKind.Object)
            {
                return TeamServerSettings.Default;
            }

            var fallback = TeamServerSettings.Default;
            var name = element.TryGetProperty("displayName", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString() ?? fallback.DisplayName
                : fallback.DisplayName;
            var port = element.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out var parsedPort)
                ? parsedPort : fallback.Port;
            var max = element.TryGetProperty("maximumClients", out var maxValue) && maxValue.TryGetInt32(out var parsedMax)
                ? parsedMax : fallback.MaximumClients;
            var requirePassphrase = !element.TryGetProperty("requirePassphrase", out var authValue) ||
                authValue.ValueKind != JsonValueKind.False;
            return new TeamServerSettings(name, port, max, requirePassphrase).Normalize();
        }
        catch
        {
            return TeamServerSettings.Default;
        }
    }
}
