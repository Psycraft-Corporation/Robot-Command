using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourParameterFormViewModelTests
{
    [Fact]
    public void Load_UsesDefaultsAndBuildsCanonicalTypedJson()
    {
        var form = new BehaviourParameterFormViewModel();

        form.Load(Schema());

        Assert.True(form.ShowTypedFields);
        Assert.True(form.IsValid);
        var result = form.Build();
        Assert.True(result.Valid);
        using var json = JsonDocument.Parse(result.CanonicalJson);
        Assert.Equal(35m, json.RootElement.GetProperty("altitude_metres").GetDecimal());
        Assert.Equal("lawnmower", json.RootElement.GetProperty("pattern").GetString());
        Assert.False(json.RootElement.GetProperty("confirm_detection").GetBoolean());
    }

    [Fact]
    public void TypedField_EnforcesRequiredAndRangeConstraints()
    {
        var form = new BehaviourParameterFormViewModel();
        form.Load(Schema());
        var altitude = form.Fields.Single(item => item.Id == "altitude_metres");

        altitude.TextValue = "500";

        Assert.False(form.IsValid);
        Assert.Contains("at most 120", form.ValidationSummary, StringComparison.Ordinal);
        Assert.False(form.Build().Valid);
    }

    [Fact]
    public void RawFallback_RequiresAJsonObject()
    {
        var form = new BehaviourParameterFormViewModel();
        form.Load(BehaviourParameterSchema.NotDeclared(new BehaviourPackageIdentity("test/raw")));

        form.RawJson = "[1,2,3]";
        Assert.False(form.IsValid);

        form.RawJson = "{\"custom\":true}";
        var result = form.Build();
        Assert.True(result.Valid);
        Assert.Contains("\"custom\": true", result.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingToRawJsonPreservesCurrentTypedValues()
    {
        var form = new BehaviourParameterFormViewModel();
        form.Load(Schema());
        form.Fields.Single(item => item.Id == "altitude_metres").TextValue = "42.5";

        form.UseRawJson = true;

        using var json = JsonDocument.Parse(form.RawJson);
        Assert.Equal(42.5m, json.RootElement.GetProperty("altitude_metres").GetDecimal());
    }

    [Fact]
    public void TypedModePreservesUnmodelledPropertiesFromExistingJson()
    {
        var form = new BehaviourParameterFormViewModel();
        form.Load(Schema(), "{\"altitude_metres\":50,\"vendor_extension\":{\"enabled\":true}}");

        var result = form.Build();

        Assert.True(result.Valid);
        using var json = JsonDocument.Parse(result.CanonicalJson);
        Assert.True(json.RootElement.GetProperty("vendor_extension").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Load_LeavesMalformedExistingJsonInRawMode()
    {
        var form = new BehaviourParameterFormViewModel();

        form.Load(Schema(), "{invalid");

        Assert.True(form.UseRawJson);
        Assert.False(form.IsValid);
        Assert.Equal("{invalid", form.RawJson);

        form.UseRawJson = false;
        Assert.True(form.UseRawJson);
        Assert.False(form.IsValid);
    }

    [Fact]
    public void RequiredChoiceWithoutDefaultRemainsInvalidUntilSelected()
    {
        var schema = Schema() with
        {
            Parameters =
            [
                new BehaviourParameterDefinition(
                    "pattern",
                    "Pattern",
                    string.Empty,
                    BehaviourParameterKind.Enum,
                    true,
                    null,
                    null,
                    null,
                    string.Empty,
                    [new BehaviourParameterOption("orbit", "Orbit")])
            ]
        };
        var form = new BehaviourParameterFormViewModel();

        form.Load(schema);

        Assert.False(form.IsValid);
        var field = Assert.Single(form.Fields);
        field.SelectedOptionItem = Assert.Single(field.Options);
        Assert.True(form.IsValid);
    }

    [Fact]
    public void ExistingKnownPropertyWithWrongJsonTypeIsNotSilentlyCoerced()
    {
        var form = new BehaviourParameterFormViewModel();

        form.Load(Schema(), "{\"pattern\":42}");

        Assert.False(form.IsValid);
        Assert.Contains("non-string", form.ValidationSummary, StringComparison.OrdinalIgnoreCase);
        form.UseRawJson = true;
        Assert.True(form.IsValid);
        Assert.Contains("\"pattern\": 42", form.RawJson, StringComparison.Ordinal);
    }

    private static BehaviourParameterSchema Schema() => new(
        new BehaviourPackageIdentity("survey/search", "2.1.0"),
        BehaviourParameterSchemaState.Valid,
        "Three parameters",
        [
            new BehaviourParameterDefinition(
                "altitude_metres",
                "Search altitude",
                "Altitude used by the behaviour.",
                BehaviourParameterKind.AltitudeMetres,
                true,
                "35",
                5,
                120,
                "m",
                []),
            new BehaviourParameterDefinition(
                "pattern",
                "Pattern",
                string.Empty,
                BehaviourParameterKind.Enum,
                true,
                "lawnmower",
                null,
                null,
                string.Empty,
                [
                    new BehaviourParameterOption("lawnmower", "Lawn mower"),
                    new BehaviourParameterOption("orbit", "Orbit")
                ]),
            new BehaviourParameterDefinition(
                "confirm_detection",
                "Confirm detection",
                string.Empty,
                BehaviourParameterKind.Boolean,
                false,
                "false",
                null,
                null,
                string.Empty,
                [])
        ],
        [],
        "manifest");
}
