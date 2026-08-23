using System.Globalization;
using RobotCommand.Models;

namespace RobotCommand.Services;

public interface IUnitSettingsService
{
    AppUnitSettings Current { get; }
    event EventHandler? Changed;
    Task SetAsync(AppUnitSettings settings, CancellationToken cancellationToken = default);
}

public sealed class UnitSettingsService : IUnitSettingsService
{
    private readonly IApplicationSettingsService _applicationSettings;
    private readonly object _gate = new();
    private AppUnitSettings _current;

    // NativeOperationalMapControl is created by Avalonia markup rather than DI.
    // This small bridge keeps the map on the same global settings instance.
    public static UnitSettingsService? Instance { get; private set; }

    public UnitSettingsService(IApplicationSettingsService applicationSettings)
    {
        _applicationSettings = applicationSettings;
        _current = applicationSettings.Current.Units ?? new AppUnitSettings();
        Instance = this;
    }

    public AppUnitSettings CurrentSettings
    {
        get { lock (_gate) return _current; }
    }

    AppUnitSettings IUnitSettingsService.Current => CurrentSettings;

    public AppUnitSettings Current
        => CurrentSettings;

    public event EventHandler? Changed;

    public async Task SetAsync(AppUnitSettings settings, CancellationToken cancellationToken = default)
    {
        settings ??= new AppUnitSettings();
        lock (_gate)
        {
            if (_current == settings)
                return;
            _current = settings;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await _applicationSettings.SetUnitsAsync(settings, cancellationToken);
    }
}

public static class UnitFormatting
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Distance(double metres, DistanceUnit unit, string format = "0.#")
    {
        var value = unit == DistanceUnit.Feet ? metres * 3.280839895013123 : metres;
        var suffix = unit == DistanceUnit.Feet ? "ft" : "m";
        return $"{value.ToString(format, Culture)} {suffix}";
    }

    public static string AdaptiveDistance(double metres, DistanceUnit unit)
    {
        metres = Math.Max(0, metres);
        if (unit == DistanceUnit.Feet)
        {
            var feet = metres * 3.280839895013123;
            return feet >= 5280 ? $"{(feet / 5280).ToString("0.#", Culture)} mi" : $"{feet.ToString("0.#", Culture)} ft";
        }

        return metres >= 1000
            ? $"{(metres / 1000).ToString("0.#", Culture)} km"
            : metres >= 1
                ? $"{metres.ToString("0.#", Culture)} m"
                : $"{(metres * 100).ToString("0.#", Culture)} cm";
    }

    public static string Speed(double metresPerSecond, SpeedUnit unit, string format = "0.#")
    {
        var value = unit switch
        {
            SpeedUnit.FeetPerSecond => metresPerSecond * 3.280839895013123,
            SpeedUnit.KilometersPerHour => metresPerSecond * 3.6,
            SpeedUnit.MilesPerHour => metresPerSecond * 2.2369362920544,
            SpeedUnit.Knots => metresPerSecond * 1.9438444924406,
            _ => metresPerSecond,
        };
        var suffix = unit switch
        {
            SpeedUnit.FeetPerSecond => "ft/s",
            SpeedUnit.KilometersPerHour => "km/h",
            SpeedUnit.MilesPerHour => "mph",
            SpeedUnit.Knots => "kt",
            _ => "m/s",
        };
        return $"{value.ToString(format, Culture)} {suffix}";
    }

    public static double ToMetres(double value, DistanceUnit unit)
        => unit == DistanceUnit.Feet ? value / 3.280839895013123 : value;

    public static double ToMetresPerSecond(double value, SpeedUnit unit)
        => unit switch
        {
            SpeedUnit.FeetPerSecond => value / 3.280839895013123,
            SpeedUnit.KilometersPerHour => value / 3.6,
            SpeedUnit.MilesPerHour => value / 2.2369362920544,
            SpeedUnit.Knots => value / 1.9438444924406,
            _ => value,
        };

    public static string DistanceUnitLabel(DistanceUnit unit)
        => unit == DistanceUnit.Feet ? "ft" : "m";

    public static string Area(double squareMetres, AreaUnit unit, string format = "0.#")
    {
        var value = unit switch
        {
            AreaUnit.SquareFeet => squareMetres * 10.7639104167097,
            AreaUnit.SquareKilometers => squareMetres / 1_000_000,
            AreaUnit.SquareMiles => squareMetres / 2_589_988.110336,
            AreaUnit.Hectares => squareMetres / 10_000,
            AreaUnit.Acres => squareMetres / 4_046.8564224,
            _ => squareMetres,
        };
        var suffix = unit switch
        {
            AreaUnit.SquareFeet => "ft²",
            AreaUnit.SquareKilometers => "km²",
            AreaUnit.SquareMiles => "mi²",
            AreaUnit.Hectares => "ha",
            AreaUnit.Acres => "ac",
            _ => "m²",
        };
        return $"{value.ToString(format, Culture)} {suffix}";
    }

    public static string Temperature(double celsius, TemperatureUnit unit, string format = "0.#")
    {
        var value = unit == TemperatureUnit.Fahrenheit ? celsius * 9 / 5 + 32 : celsius;
        return $"{value.ToString(format, Culture)} {(unit == TemperatureUnit.Fahrenheit ? "°F" : "°C")}";
    }
}
