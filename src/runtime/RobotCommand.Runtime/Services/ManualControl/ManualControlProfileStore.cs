using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

public sealed class ManualControlProfileStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ManualControlProfileStore(AppConfiguration configuration)
        => _path = Path.Combine(AppContext.BaseDirectory, "data", "manual-control.json");

    public ManualControlProfileStore(string baseDirectory)
        => _path = Path.Combine(baseDirectory, "data", "manual-control.json");

    public async Task<ManualControlProfileLibrary> LoadLibraryAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path)) return ManualControlProfileLibrary.Default;
            await using var stream = File.OpenRead(_path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ManualControlProfileLibrary.Default;

            if (document.RootElement.TryGetProperty("Profiles", out _) || document.RootElement.TryGetProperty("profiles", out _))
            {
                var library = document.RootElement.Deserialize<ManualControlProfileLibrary>(Options);
                return NormalizeLibrary(library);
            }

            var legacy = document.RootElement.Deserialize<ManualControlProfile>(Options) ?? new ManualControlProfile();
            return new ManualControlProfileLibrary(
                ManualControlProfileLibrary.CurrentSchemaVersion,
                "default",
                [new ManualControlProfileRecord("default", "Default", legacy.Normalize())]);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ManualControlProfileLibrary.Default;
        }
    }

    public async Task<ManualControlProfile> LoadAsync(CancellationToken cancellationToken)
    {
        var library = await LoadLibraryAsync(cancellationToken);
        return library.Profiles.FirstOrDefault(item => item.Id == library.ActiveProfileId)?.Settings.Normalize()
            ?? new ManualControlProfile();
    }

    public async Task SaveAsync(ManualControlProfile profile, CancellationToken cancellationToken)
    {
        var library = await LoadLibraryAsync(cancellationToken);
        var activeId = library.ActiveProfileId;
        var profiles = library.Profiles
            .Select(item => item.Id == activeId ? item with { Settings = profile.Normalize() } : item)
            .ToArray();
        await SaveLibraryAsync(library with { Profiles = profiles }, cancellationToken);
    }

    public async Task SaveLibraryAsync(ManualControlProfileLibrary library, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLibrary(library);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(normalized, Options), cancellationToken);
        File.Move(temporary, _path, true);
    }

    private static ManualControlProfileLibrary NormalizeLibrary(ManualControlProfileLibrary? library)
    {
        var profiles = (library?.Profiles ?? [])
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return first with
                {
                    Name = first.Name.Trim(),
                    Settings = first.Settings?.Normalize() ?? new ManualControlProfile()
                };
            })
            .ToArray();
        if (profiles.Length == 0) return ManualControlProfileLibrary.Default;

        var activeId = profiles.Any(item => item.Id == library!.ActiveProfileId)
            ? library!.ActiveProfileId
            : profiles[0].Id;
        return new ManualControlProfileLibrary(ManualControlProfileLibrary.CurrentSchemaVersion, activeId, profiles);
    }
}
