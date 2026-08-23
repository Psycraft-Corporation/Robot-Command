using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.Services;
using Xunit;

namespace RobotCommand.Tests;

public sealed class UnitSettingsTests
{
    [Fact]
    public void Defaults_AreMetric()
    {
        var units = new AppUnitSettings();
        Assert.Equal(DistanceUnit.Meters, units.HorizontalDistance);
        Assert.Equal(DistanceUnit.Meters, units.VerticalDistance);
        Assert.Equal(AreaUnit.SquareMeters, units.Area);
        Assert.Equal(SpeedUnit.MetersPerSecond, units.Speed);
        Assert.Equal(TemperatureUnit.Celsius, units.Temperature);
    }

    [Fact]
    public void Formatting_ConvertsAllConfiguredFamilies()
    {
        Assert.Equal("3.3 ft", UnitFormatting.Distance(1, DistanceUnit.Feet));
        Assert.Equal("3.6 km/h", UnitFormatting.Speed(1, SpeedUnit.KilometersPerHour));
        Assert.Equal("10763.9 ft²", UnitFormatting.Area(1000, AreaUnit.SquareFeet));
        Assert.Equal("32 °F", UnitFormatting.Temperature(0, TemperatureUnit.Fahrenheit));
    }

    [Fact]
    public void Configuration_ReadsAndPersistsUnitSettingsWithoutDroppingOtherUiSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "robot-command-units-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.local.json"),
                "{\"connections\":[{\"name\":\"Keep\",\"target\":\"http://localhost:50051\"}],\"ui\":{\"brightMode\":true,\"units\":{\"horizontalDistance\":\"Feet\",\"verticalDistance\":\"Feet\",\"area\":\"Acres\",\"speed\":\"Knots\",\"temperature\":\"Fahrenheit\"}}}");
            var configuration = AppConfiguration.Load(directory);
            Assert.True(configuration.Ui.BrightModeEnabled);
            Assert.Equal(DistanceUnit.Feet, configuration.Ui.Units.HorizontalDistance);
            Assert.Equal(AreaUnit.Acres, configuration.Ui.Units.Area);

            var persistence = new ApplicationSettingsPersistence(directory);
            persistence.SaveAsync(configuration.Ui).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "appsettings.local.json")));
            Assert.Equal("Keep", json.RootElement.GetProperty("connections")[0].GetProperty("name").GetString());
            Assert.Equal("Fahrenheit", json.RootElement.GetProperty("ui").GetProperty("units").GetProperty("temperature").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
