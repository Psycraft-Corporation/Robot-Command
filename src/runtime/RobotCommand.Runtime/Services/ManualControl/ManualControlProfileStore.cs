using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

public sealed class ManualControlProfileStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ManualControlProfileStore(AppConfiguration configuration)
        => _path = Path.Combine(AppContext.BaseDirectory, "data", "manual-control.json");

    public ManualControlProfileStore(string baseDirectory)
        => _path = Path.Combine(baseDirectory, "data", "manual-control.json");

    public async Task<ManualControlProfile> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path)) return new ManualControlProfile();
            await using var stream = File.OpenRead(_path);
            return (await JsonSerializer.DeserializeAsync<ManualControlProfile>(stream, Options, cancellationToken) ?? new ManualControlProfile()).Normalize();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManualControlProfile();
        }
    }

    public async Task SaveAsync(ManualControlProfile profile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile.Normalize(), Options), cancellationToken);
        File.Move(temporary, _path, true);
    }
}
