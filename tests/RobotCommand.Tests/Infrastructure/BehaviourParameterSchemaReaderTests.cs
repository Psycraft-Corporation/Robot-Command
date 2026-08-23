using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourParameterSchemaReaderTests
{
    private static readonly string[] expected = new[] { "lawnmower", "orbit" };

    [Fact]
    public void Read_ParsesOperatorMetadataWithoutInspectingTreeNodesOrPorts()
    {
        const string manifest = """
            schema_version: 1
            bt_id: survey/search
            version: 2.1.0
            tree_file: tree.xml
            parameters:
              - id: altitude_metres
                label: Search altitude
                description: Altitude used by the behaviour.
                type: altitude
                required: true
                default: 35
                min: 5
                max: 120
              - id: pattern
                type: enum
                required: true
                default: lawnmower
                options:
                  - value: lawnmower
                    label: Lawn mower
                  - orbit
              - id: operator_note
                type: multiline
              - id: confirm_detection
                type: bool
                default: false
            arbitrary_node_contracts:
              - node: VendorSpecificNode
                ports:
                  arbitrary_port: value
            """;

        var schema = BehaviourParameterSchemaReader.Read(
            new BehaviourPackageIdentity("survey/search", "2.1.0"),
            manifest);

        Assert.Equal(BehaviourParameterSchemaState.Valid, schema.State);
        Assert.Equal(4, schema.Parameters.Count);
        var altitude = schema.Parameters.Single(item => item.Id == "altitude_metres");
        Assert.Equal(BehaviourParameterKind.AltitudeMetres, altitude.Kind);
        Assert.Equal(5m, altitude.Minimum);
        Assert.Equal(120m, altitude.Maximum);
        Assert.Equal("m", altitude.Unit);
        var pattern = schema.Parameters.Single(item => item.Id == "pattern");
        Assert.Equal(expected, pattern.Options.Select(item => item.Value).ToArray());
        Assert.Empty(schema.Findings);
    }

    [Fact]
    public void Read_ReturnsNotDeclaredWhenManifestHasNoParameterMetadata()
    {
        const string manifest = """
            schema_version: 1
            bt_id: test/arm_disarm
            tree_file: tree.xml
            """;

        var schema = BehaviourParameterSchemaReader.Read(
            new BehaviourPackageIdentity("test/arm_disarm"),
            manifest);

        Assert.Equal(BehaviourParameterSchemaState.NotDeclared, schema.State);
        Assert.False(schema.Usable);
        Assert.Empty(schema.Parameters);
    }

    [Fact]
    public void Read_InvalidMetadataFallsBackWithoutClaimingPackageInvalidity()
    {
        const string manifest = """
            bt_id: survey/search
            parameters:
              - id: speed
                type: warp-factor
              - id: speed
                type: decimal
            """;

        var schema = BehaviourParameterSchemaReader.Read(
            new BehaviourPackageIdentity("survey/search"),
            manifest);

        Assert.Equal(BehaviourParameterSchemaState.Invalid, schema.State);
        Assert.False(schema.Usable);
        Assert.Contains(schema.Findings, item => item.Code == "BEHAVIOUR_PARAMETER_TYPE_UNSUPPORTED");
        Assert.Contains(schema.Findings, item => item.Code == "BEHAVIOUR_PARAMETER_ID_DUPLICATE");
        Assert.Contains("Raw JSON", schema.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsInvalidDefaultsAndRanges()
    {
        const string manifest = """
            bt_id: survey/search
            parameters:
              - id: altitude
                type: altitude
                default: high
                min: 100
                max: 20
            """;

        var schema = BehaviourParameterSchemaReader.Read(
            new BehaviourPackageIdentity("survey/search"),
            manifest);

        Assert.Equal(BehaviourParameterSchemaState.Invalid, schema.State);
        Assert.Contains(schema.Findings, item => item.Code == "BEHAVIOUR_PARAMETER_RANGE_INVALID");
        Assert.Contains(schema.Findings, item => item.Code == "BEHAVIOUR_PARAMETER_DEFAULT_INVALID");
    }

    [Fact]
    public void Read_MalformedYamlReturnsInvalidSchemaInsteadOfThrowing()
    {
        const string manifest = "parameters: [unterminated";

        var schema = BehaviourParameterSchemaReader.Read(
            new BehaviourPackageIdentity("survey/search"),
            manifest);

        Assert.Equal(BehaviourParameterSchemaState.Invalid, schema.State);
        Assert.Contains(schema.Findings, item => item.Code == "BEHAVIOUR_PARAMETER_SCHEMA_YAML");
    }
}
