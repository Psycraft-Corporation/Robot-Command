using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IApplicationSettingsService _settings;
    private readonly ILocalizationService _localization;
    private bool _brightModeEnabled;
    private string _language;
    private LanguageOption _selectedLanguage;
    private readonly IUnitSettingsService _unitSettings;
    private readonly IApplicationPreferencesWorkflow? _preferences;

    public SettingsViewModel(
        IApplicationSettingsService settings,
        ILocalizationService localization,
        IUnitSettingsService? unitSettings = null,
        IApplicationPreferencesWorkflow? preferences = null)
    {
        _settings = settings;
        _localization = localization;
        _unitSettings = unitSettings ?? new UnitSettingsService(settings);
        _preferences = preferences;
        _brightModeEnabled = settings.Current.BrightModeEnabled;
        _language = localization.CurrentLanguage;
        _selectedLanguage = localization.AvailableLanguages.First(item => item.Code == _language);
        _localization.PropertyChanged += OnLocalizationChanged;
        _unitSettings.Changed += OnUnitSettingsChanged;
    }

    public bool BrightModeEnabled
    {
        get => _brightModeEnabled;
        set
        {
            if (!SetProperty(ref _brightModeEnabled, value))
            {
                return;
            }

            _ = PersistAsync(value);
        }
    }

    public string Language
    {
        get => _language;
        set => SelectLanguage(value);
    }

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null)
            {
                return;
            }

            SelectLanguage(value.Code);
        }
    }

    public int SelectedLanguageIndex
    {
        get
        {
            var index = Languages
                .Select((item, index) => (item, index))
                .FirstOrDefault(pair => string.Equals(pair.item.Code, _language, StringComparison.OrdinalIgnoreCase))
                .index;
            return index;
        }
        set
        {
            SelectLanguageIndex(value);
        }
    }

    public void SelectLanguageIndex(int index)
    {
        if (index < 0 || index >= Languages.Count)
        {
            return;
        }

        SelectLanguage(Languages[index].Code);
    }

    public void SelectLanguage(string language)
    {
        var normalized = NormalizeLanguage(language);

        // Do not rely on the SelectedItem/Language binding to have changed the
        // view-model first. Avalonia may update the selected item without calling
        // the setter, or may call the setter with the same value while the
        // localized item list is being rebuilt. Always forward the explicit user
        // selection to the localization service.
        SetProperty(ref _language, normalized, nameof(Language));
        _ = SetLanguageAsync(normalized);
    }

    public IReadOnlyList<LanguageOption> Languages => _localization.AvailableLanguages;

    public string Title => _localization.Get("Settings");
    public string Description => _localization.Get("SettingsDescription");
    public string LanguageTitle => _localization.Get("SettingsLanguage");
    public string LanguageDescription => _localization.Get("SettingsLanguageDescription");
    public string BrightModeTitle => _localization.Get("BrightMode");
    public string UnitsTitle => _localization.Get("SettingsUnits");
    public string HorizontalDistanceTitle => _localization.Get("SettingsHorizontalDistance");
    public string VerticalDistanceTitle => _localization.Get("SettingsVerticalDistance");
    public string AreaTitle => _localization.Get("SettingsArea");
    public string SpeedTitle => _localization.Get("SettingsSpeed");
    public string TemperatureTitle => _localization.Get("SettingsTemperature");

    public IReadOnlyList<UnitChoice> HorizontalDistanceOptions =>
        [new("Meters", _localization.Get("UnitMeters")), new("Feet", _localization.Get("UnitFeet"))];
    public IReadOnlyList<UnitChoice> VerticalDistanceOptions => HorizontalDistanceOptions;
    public IReadOnlyList<UnitChoice> AreaOptions =>
        [new("SquareMeters", _localization.Get("UnitSquareMeters")), new("SquareFeet", _localization.Get("UnitSquareFeet")),
         new("SquareKilometers", _localization.Get("UnitSquareKilometers")), new("SquareMiles", _localization.Get("UnitSquareMiles")),
         new("Hectares", _localization.Get("UnitHectares")), new("Acres", _localization.Get("UnitAcres"))];
    public IReadOnlyList<UnitChoice> SpeedOptions =>
        [new("MetersPerSecond", _localization.Get("UnitMetersPerSecond")), new("FeetPerSecond", _localization.Get("UnitFeetPerSecond")),
         new("KilometersPerHour", _localization.Get("UnitKilometersPerHour")), new("MilesPerHour", _localization.Get("UnitMilesPerHour")),
         new("Knots", _localization.Get("UnitKnots"))];
    public IReadOnlyList<UnitChoice> TemperatureOptions =>
        [new("Celsius", _localization.Get("UnitCelsius")), new("Fahrenheit", _localization.Get("UnitFahrenheit"))];

    public int HorizontalDistanceIndex => (int)_unitSettings.Current.HorizontalDistance;
    public int VerticalDistanceIndex => (int)_unitSettings.Current.VerticalDistance;
    public int AreaIndex => (int)_unitSettings.Current.Area;
    public int SpeedIndex => (int)_unitSettings.Current.Speed;
    public int TemperatureIndex => (int)_unitSettings.Current.Temperature;

    public void SelectUnit(string kind, int index)
    {
        var current = _unitSettings.Current;
        AppUnitSettings next = kind switch
        {
            "horizontal" when Enum.IsDefined(typeof(DistanceUnit), index) => current with { HorizontalDistance = (DistanceUnit)index },
            "vertical" when Enum.IsDefined(typeof(DistanceUnit), index) => current with { VerticalDistance = (DistanceUnit)index },
            "area" when Enum.IsDefined(typeof(AreaUnit), index) => current with { Area = (AreaUnit)index },
            "speed" when Enum.IsDefined(typeof(SpeedUnit), index) => current with { Speed = (SpeedUnit)index },
            "temperature" when Enum.IsDefined(typeof(TemperatureUnit), index) => current with { Temperature = (TemperatureUnit)index },
            _ => current,
        };
        _ = _preferences is null
            ? _unitSettings.SetAsync(next)
            : _preferences.SetUnitsAsync(next.HorizontalDistance.ToString(), next.VerticalDistance.ToString(), next.Area.ToString(), next.Speed.ToString(), next.Temperature.ToString());
        RaiseUnitProperties();
    }

    private async Task PersistAsync(bool enabled)
    {
        try
        {
            if (_preferences is not null)
                await _preferences.SetBrightModeAsync(enabled);
            else
                await _settings.SetBrightModeEnabledAsync(enabled);
        }
        catch
        {
            // The in-memory setting remains active; the next toggle retries persistence.
        }
    }

    private async Task SetLanguageAsync(string language)
    {
        try
        {
            if (_preferences is not null)
                await _preferences.SetLanguageAsync(language);
            else
                await _localization.SetLanguageAsync(language);
        }
        catch
        {
            // The selected language remains usable in memory; the next change retries persistence.
        }
    }

    private static string NormalizeLanguage(string? language)
        => string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _language = _localization.CurrentLanguage;
        _selectedLanguage = _localization.AvailableLanguages.First(item => item.Code == _language);
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(SelectedLanguageIndex));
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(LanguageTitle));
        OnPropertyChanged(nameof(LanguageDescription));
        OnPropertyChanged(nameof(BrightModeTitle));
        OnPropertyChanged(nameof(UnitsTitle));
        OnPropertyChanged(nameof(HorizontalDistanceTitle));
        OnPropertyChanged(nameof(VerticalDistanceTitle));
        OnPropertyChanged(nameof(AreaTitle));
        OnPropertyChanged(nameof(SpeedTitle));
        OnPropertyChanged(nameof(TemperatureTitle));
        OnPropertyChanged(nameof(HorizontalDistanceOptions));
        OnPropertyChanged(nameof(VerticalDistanceOptions));
        OnPropertyChanged(nameof(AreaOptions));
        OnPropertyChanged(nameof(SpeedOptions));
        OnPropertyChanged(nameof(TemperatureOptions));
        // Re-publish the indices after replacing localized ItemsSource lists.
        // Avalonia can clear a ComboBox selection during that replacement and
        // will not re-read an unchanged OneWay index automatically.
        RaiseUnitProperties();
    }

    private void RaiseUnitProperties()
    {
        OnPropertyChanged(nameof(HorizontalDistanceIndex));
        OnPropertyChanged(nameof(VerticalDistanceIndex));
        OnPropertyChanged(nameof(AreaIndex));
        OnPropertyChanged(nameof(SpeedIndex));
        OnPropertyChanged(nameof(TemperatureIndex));
    }

    private void OnUnitSettingsChanged(object? sender, EventArgs e)
        => RaiseUnitProperties();
}

public sealed record UnitChoice(string Code, string DisplayName);
