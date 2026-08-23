using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services;

public interface IApplicationSettingsPersistence
{
    Task SaveAsync(AppUiSettings settings, CancellationToken cancellationToken = default);
}

public sealed class ApplicationSettingsPersistence(string baseDirectory) : IApplicationSettingsPersistence
{
    private static readonly JsonSerializerOptions SettingsJson = CreateSettingsJson();
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(baseDirectory, "appsettings.local.json");

    public async Task SaveAsync(AppUiSettings settings, CancellationToken cancellationToken = default)
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

        root["ui"] = JsonSerializer.SerializeToNode(settings, SettingsJson);

        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            root.ToJsonString(IndentedJson),
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

    private static JsonSerializerOptions CreateSettingsJson()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public interface IApplicationSettingsService
{
    AppUiSettings Current { get; }

    event EventHandler? Changed;

    Task SetBrightModeEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetLanguageAsync(string language, CancellationToken cancellationToken = default);

    Task SetUnitsAsync(AppUnitSettings units, CancellationToken cancellationToken = default);

    Task SetOperateInspectorWidthAsync(double width, CancellationToken cancellationToken = default);
}

public sealed class ApplicationSettingsService(
    AppConfiguration configuration,
    IApplicationSettingsPersistence persistence) : IApplicationSettingsService
{
    private readonly object _gate = new();
    private AppUiSettings _current = configuration.Ui;

    public AppUiSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public async Task SetBrightModeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        AppUiSettings next;
        lock (_gate)
        {
            if (_current.BrightModeEnabled == enabled)
            {
                return;
            }

            next = _current with { BrightModeEnabled = enabled };
            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await persistence.SaveAsync(next, cancellationToken);
    }

    public async Task SetLanguageAsync(
        string language,
        CancellationToken cancellationToken = default)
    {
        var normalized = language?.ToLowerInvariant() switch
        {
            "fr" => "fr",
            "ja" => "ja",
            "uk" or "uk-ua" => "uk",
            "ko" or "ko-kr" => "ko",
            _ => "en"
        };

        AppUiSettings next;
        lock (_gate)
        {
            if (string.Equals(_current.Language, normalized, StringComparison.Ordinal))
            {
                return;
            }

            next = _current with { Language = normalized };
            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await persistence.SaveAsync(next, cancellationToken);
    }

    public async Task SetUnitsAsync(
        AppUnitSettings units,
        CancellationToken cancellationToken = default)
    {
        units ??= new AppUnitSettings();
        AppUiSettings next;
        lock (_gate)
        {
            if (_current.Units == units)
            {
                return;
            }

            next = _current with { Units = units };
            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await persistence.SaveAsync(next, cancellationToken);
    }

    public async Task SetOperateInspectorWidthAsync(
        double width,
        CancellationToken cancellationToken = default)
    {
        var normalized = Math.Clamp(width, 340, 460);
        AppUiSettings next;
        lock (_gate)
        {
            if (Math.Abs(_current.OperateInspectorWidth - normalized) < 0.5)
            {
                return;
            }

            next = _current with { OperateInspectorWidth = normalized };
            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await persistence.SaveAsync(next, cancellationToken);
    }
}
