using System.Text.Json.Serialization;

namespace RobotCommand.Models;

public enum DistanceUnit
{
    Meters,
    Feet,
}

public enum AreaUnit
{
    SquareMeters,
    SquareFeet,
    SquareKilometers,
    SquareMiles,
    Hectares,
    Acres,
}

public enum SpeedUnit
{
    MetersPerSecond,
    FeetPerSecond,
    KilometersPerHour,
    MilesPerHour,
    Knots,
}

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

public sealed record AppUnitSettings(
    [property: JsonPropertyName("horizontalDistance"), JsonConverter(typeof(JsonStringEnumConverter))] DistanceUnit HorizontalDistance = DistanceUnit.Meters,
    [property: JsonPropertyName("verticalDistance"), JsonConverter(typeof(JsonStringEnumConverter))] DistanceUnit VerticalDistance = DistanceUnit.Meters,
    [property: JsonPropertyName("area"), JsonConverter(typeof(JsonStringEnumConverter))] AreaUnit Area = AreaUnit.SquareMeters,
    [property: JsonPropertyName("speed"), JsonConverter(typeof(JsonStringEnumConverter))] SpeedUnit Speed = SpeedUnit.MetersPerSecond,
    [property: JsonPropertyName("temperature"), JsonConverter(typeof(JsonStringEnumConverter))] TemperatureUnit Temperature = TemperatureUnit.Celsius);

public sealed record AppUiSettings(
    [property: JsonPropertyName("brightMode")] bool BrightModeEnabled = false,
    [property: JsonPropertyName("language")] string Language = "en",
    [property: JsonPropertyName("operateInspectorWidth")] double OperateInspectorWidth = 380)
{
    [JsonPropertyName("units")]
    public AppUnitSettings Units { get; init; } = new();
}
