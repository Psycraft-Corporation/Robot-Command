using System.Text.Json;
using System.Text.Json.Nodes;
using RobotCommand.Models;

namespace RobotCommand.Services;

public interface IConnectionPersistence
{
    Task SaveAsync(IReadOnlyList<ConnectionProfile> connections, CancellationToken cancellationToken = default);
}

public sealed class ConnectionPersistence : IConnectionPersistence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public ConnectionPersistence(string baseDirectory)
        => _path = Path.Combine(baseDirectory, "appsettings.local.json");

    public async Task SaveAsync(
        IReadOnlyList<ConnectionProfile> connections,
        CancellationToken cancellationToken = default)
    {
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

        root.Remove("profiles");
        root["connections"] = JsonSerializer.SerializeToNode(connections, Json);

        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(Json),
            cancellationToken);

        if (File.Exists(_path))
        {
            File.Replace(temporaryPath, _path, null);
        }
        else
        {
            File.Move(temporaryPath, _path);
        }
    }
}
