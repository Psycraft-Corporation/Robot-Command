using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Localization;

public sealed class LocalizationService : ILocalizationService
{
    private static readonly ResourceManager Resources = new(
        "RobotCommand.Resources.Strings",
        Assembly.GetExecutingAssembly());

    private readonly IApplicationSettingsService _settings;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _languageChangeGate = new(1, 1);
    private string _currentLanguage;

    public LocalizationService(IApplicationSettingsService settings)
    {
        _settings = settings;
        _currentLanguage = Normalize(settings.Current.Language);
        ApplyCulture(_currentLanguage);
        Current = this;
        LocalizationBindingSource.Instance.Attach(this);
    }

    public static LocalizationService Current { get; private set; } = CreateDefault();

    public string CurrentLanguage
    {
        get { lock (_gate) return _currentLanguage; }
    }

    public IReadOnlyList<LanguageOption> AvailableLanguages =>
    [
        new("en", Get("SettingsLanguageEnglish")),
        new("fr", Get("SettingsLanguageFrench"))
    ];

    public string this[string key] => Get(key);

    internal IReadOnlyList<string> ResourceKeys
        => Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)
            ?.Cast<System.Collections.DictionaryEntry>()
            .Select(entry => entry.Key)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray()
           ?? [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        return GetResourceString(key, culture) ??
               GetResourceString(key, CultureInfo.InvariantCulture) ??
               $"[{key}]";
    }

    private static string? GetResourceString(string key, CultureInfo culture)
    {
        var value = Resources.GetString(key, culture);
        if (value is not null)
        {
            return value;
        }

        // Older shell bindings used camelCase resource keys. Resolve those aliases
        // without exposing the key to operators when the underlying resource exists.
        var resourceSet = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: true);
        if (resourceSet is null)
        {
            return null;
        }

        foreach (System.Collections.DictionaryEntry entry in resourceSet)
        {
            if (entry.Key is string resourceKey &&
                string.Equals(resourceKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value as string;
            }
        }

        return null;
    }

    public async Task SetLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(language);
        await _languageChangeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (string.Equals(_currentLanguage, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _currentLanguage = normalized;
            }

            ApplyCulture(normalized);
            RaisePropertyChangedSafely(string.Empty);
            RaisePropertyChangedSafely("Item[]");
            await _settings.SetLanguageAsync(normalized, cancellationToken);
        }
        finally
        {
            _languageChangeGate.Release();
        }
    }

    private void RaisePropertyChangedSafely(string propertyName)
    {
        var handlers = PropertyChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        var args = new PropertyChangedEventArgs(propertyName);
        foreach (var handler in handlers.OfType<PropertyChangedEventHandler>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                CrashDiagnostics.WriteException("ERROR", "Localization", ex);
            }
        }
    }

    private static string Normalize(string? language)
        => string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    private static void ApplyCulture(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    private static LocalizationService CreateDefault()
        => new(new DefaultApplicationSettingsService());

    private sealed class DefaultApplicationSettingsService : IApplicationSettingsService
    {
        public AppUiSettings Current => new();
        public event EventHandler? Changed { add { } remove { } }
        public Task SetBrightModeEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLanguageAsync(string language, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetUnitsAsync(AppUnitSettings units, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetOperateInspectorWidthAsync(double width, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
