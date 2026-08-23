using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public sealed class MapDisplayPreferences : IMapDisplayPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _statePath;
    private Dictionary<string, string> _stylesByPackage;

    public MapDisplayPreferences(IMapPackageCatalog catalog)
    {
        _statePath = Path.Combine(catalog.RootPath, "map-display-state.json");
        _stylesByPackage = Load();
    }

    public event EventHandler? Changed;

    public string ResolveStyleId(InstalledMapPackage package)
    {
        lock (_gate)
        {
            if (_stylesByPackage.TryGetValue(package.Key, out var selected) &&
                package.Styles.Any(item => string.Equals(item.StyleId, selected, StringComparison.OrdinalIgnoreCase)))
            {
                return package.Styles.First(item =>
                    string.Equals(item.StyleId, selected, StringComparison.OrdinalIgnoreCase)).StyleId;
            }
        }

        if (!string.IsNullOrWhiteSpace(package.DefaultStyleId) &&
            package.Styles.Any(item => string.Equals(item.StyleId, package.DefaultStyleId, StringComparison.OrdinalIgnoreCase)))
        {
            return package.Styles.First(item =>
                string.Equals(item.StyleId, package.DefaultStyleId, StringComparison.OrdinalIgnoreCase)).StyleId;
        }

        return package.Styles.FirstOrDefault()?.StyleId ?? "operational";
    }

    public Task SelectStyleAsync(
        string packageKey,
        string styleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageKey))
        {
            throw new ArgumentException("A package key is required.", nameof(packageKey));
        }

        if (string.IsNullOrWhiteSpace(styleId))
        {
            throw new ArgumentException("A style ID is required.", nameof(styleId));
        }

        lock (_gate)
        {
            _stylesByPackage[packageKey] = styleId.Trim();
            PersistLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_statePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var stream = File.OpenRead(_statePath);
            var state = JsonSerializer.Deserialize<State>(stream, JsonOptions);
            return (state?.StylesByPackage ?? new Dictionary<string, string>())
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void PersistLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, new State(_stylesByPackage), JsonOptions);
        }

        File.Move(temporary, _statePath, overwrite: true);
    }

    private sealed record State(IReadOnlyDictionary<string, string> StylesByPackage);
}
