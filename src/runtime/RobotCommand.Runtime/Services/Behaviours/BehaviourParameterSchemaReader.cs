using System.Globalization;
using RobotCommand.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Reads only optional operator-facing parameter metadata from a package manifest.
/// It does not inspect behaviour-tree nodes, ports, blackboard keys, plugins, or
/// runtime semantics. Logos remains authoritative for package validity.
/// </summary>
public sealed class BehaviourParameterSchemaReader
{
    private static readonly string[] SchemaKeys = ["parameters", "parameter_schema", "parameterSchema"];

    public async Task<BehaviourParameterSchema> ReadAsync(
        LocalBehaviourPackageRecord package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        try
        {
            var manifestPath = package.Layout.ManifestFile;
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return BehaviourParameterSchema.Unavailable(
                    package.Identity,
                    "The local manifest is unavailable, so Robot Command cannot read parameter metadata.");
            }

            var text = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            return Read(package.Identity, text, manifestPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BehaviourParameterSchema.Unavailable(
                package.Identity,
                $"Could not read parameter metadata: {ex.Message}");
        }
    }

    public static BehaviourParameterSchema Read(
        BehaviourPackageIdentity identity,
        string manifestYaml,
        string source = "manifest")
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(manifestYaml))
        {
            return BehaviourParameterSchema.Unavailable(identity, "The package manifest is empty.");
        }

        var yaml = new YamlStream();
        try
        {
            using var reader = new StringReader(manifestYaml);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            return Invalid(identity, source,
                Finding(
                    "BEHAVIOUR_PARAMETER_SCHEMA_YAML",
                    $"The manifest parameter metadata could not be parsed as YAML: {ex.Message}"));
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return Invalid(identity, source,
                Finding("BEHAVIOUR_PARAMETER_SCHEMA_MANIFEST", "The manifest must contain one YAML mapping document."));
        }

        var schemaNode = Find(root, SchemaKeys);
        if (schemaNode is null)
        {
            return BehaviourParameterSchema.NotDeclared(identity);
        }

        if (schemaNode is YamlMappingNode schemaMap)
        {
            schemaNode = Find(schemaMap, "fields", "properties", "parameters");
        }

        if (schemaNode is not YamlSequenceNode sequence)
        {
            return Invalid(identity, source,
                Finding("BEHAVIOUR_PARAMETER_SCHEMA_SHAPE", "Parameter metadata must be a YAML sequence, or a mapping containing a 'fields' sequence."));
        }

        var definitions = new List<BehaviourParameterDefinition>();
        var findings = new List<BehaviourParameterSchemaFinding>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var node in sequence.Children)
        {
            index++;
            if (node is not YamlMappingNode field)
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_FIELD_SHAPE",
                    $"Parameter entry {index} must be a YAML mapping.",
                    $"parameters[{index - 1}]"));
                continue;
            }

            var id = Scalar(field, "id", "key", "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_ID_REQUIRED",
                    $"Parameter entry {index} is missing an ID.",
                    $"parameters[{index - 1}].id"));
                continue;
            }

            id = id.Trim();
            if (!ids.Add(id))
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_ID_DUPLICATE",
                    $"Parameter ID '{id}' is declared more than once.",
                    id));
                continue;
            }

            var kindText = Scalar(field, "type", "kind") ?? "string";
            if (!TryKind(kindText, out var kind))
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_TYPE_UNSUPPORTED",
                    $"Parameter '{id}' uses unsupported type '{kindText}'.",
                    id));
                continue;
            }

            var options = ReadOptions(field, id, findings);
            if (kind == BehaviourParameterKind.Enum && options.Count == 0)
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_ENUM_OPTIONS_REQUIRED",
                    $"Enum parameter '{id}' must declare at least one option.",
                    id));
            }

            var minimum = Decimal(field, findings, id, "minimum", "min");
            var maximum = Decimal(field, findings, id, "maximum", "max");
            if (minimum is not null && maximum is not null && minimum > maximum)
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_RANGE_INVALID",
                    $"Parameter '{id}' has a minimum greater than its maximum.",
                    id));
            }

            var defaultValue = Scalar(field, "default", "default_value", "defaultValue");
            var definition = new BehaviourParameterDefinition(
                id,
                Scalar(field, "label", "display_name", "displayName") ?? Humanize(id),
                Scalar(field, "description", "help", "hint") ?? string.Empty,
                kind,
                Boolean(field, false, "required"),
                defaultValue,
                minimum,
                maximum,
                Scalar(field, "unit", "units") ?? DefaultUnit(kind),
                options,
                Boolean(field, false, "advanced"));

            var defaultIssue = BehaviourParameterValueCodec.ValidateDefault(definition);
            if (defaultIssue is not null)
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_DEFAULT_INVALID",
                    defaultIssue,
                    id));
            }

            definitions.Add(definition);
        }

        if (findings.Any(item => item.Severity == BehaviourPackageFindingSeverity.Error))
        {
            return new BehaviourParameterSchema(
                identity,
                BehaviourParameterSchemaState.Invalid,
                "The package declares parameter metadata that Robot Command cannot safely render. Raw JSON remains available.",
                definitions,
                findings,
                source);
        }

        return new BehaviourParameterSchema(
            identity,
            BehaviourParameterSchemaState.Valid,
            definitions.Count == 0
                ? "The package declares an empty parameter schema."
                : $"The package declares {definitions.Count} operator parameter(s).",
            definitions,
            findings,
            source);
    }

    private static BehaviourParameterSchema Invalid(
        BehaviourPackageIdentity identity,
        string source,
        params BehaviourParameterSchemaFinding[] findings) => new(
        identity,
        BehaviourParameterSchemaState.Invalid,
        "The package parameter schema is not usable. Raw JSON remains available.",
        [],
        findings,
        source);

    private static BehaviourParameterSchemaFinding Finding(
        string code,
        string message,
        string? field = null) => new(
        code,
        BehaviourPackageFindingSeverity.Error,
        message,
        field);

    private static YamlNode? Find(YamlMappingNode map, params string[] keys)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode key &&
                !string.IsNullOrWhiteSpace(key.Value) &&
                keys.Contains(key.Value, StringComparer.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        return null;
    }

    private static string? Scalar(YamlMappingNode map, params string[] keys)
        => Find(map, keys) is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value.Trim()
            : null;

    private static bool Boolean(YamlMappingNode map, bool fallback, params string[] keys)
        => bool.TryParse(Scalar(map, keys), out var value) ? value : fallback;

    private static decimal? Decimal(
        YamlMappingNode map,
        ICollection<BehaviourParameterSchemaFinding> findings,
        string id,
        params string[] keys)
    {
        var text = Scalar(map, keys);
        if (text is null) return null;
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        findings.Add(Finding(
            "BEHAVIOUR_PARAMETER_NUMBER_INVALID",
            $"Parameter '{id}' has invalid numeric value '{text}' for '{keys[0]}'.",
            id));
        return null;
    }

    private static IReadOnlyList<BehaviourParameterOption> ReadOptions(
        YamlMappingNode field,
        string id,
        ICollection<BehaviourParameterSchemaFinding> findings)
    {
        var node = Find(field, "options", "choices", "values");
        if (node is null) return [];
        if (node is not YamlSequenceNode sequence)
        {
            findings.Add(Finding(
                "BEHAVIOUR_PARAMETER_OPTIONS_SHAPE",
                $"Parameter '{id}' options must be a sequence.",
                id));
            return [];
        }

        var result = new List<BehaviourParameterOption>();
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in sequence.Children)
        {
            string? value;
            string? label;
            if (child is YamlScalarNode scalar)
            {
                value = scalar.Value?.Trim();
                label = value;
            }
            else if (child is YamlMappingNode option)
            {
                value = Scalar(option, "value", "id", "key");
                label = Scalar(option, "label", "name") ?? value;
            }
            else
            {
                value = null;
                label = null;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_OPTION_INVALID",
                    $"Parameter '{id}' contains an option without a scalar value.",
                    id));
                continue;
            }

            if (!values.Add(value))
            {
                findings.Add(Finding(
                    "BEHAVIOUR_PARAMETER_OPTION_DUPLICATE",
                    $"Parameter '{id}' declares option '{value}' more than once.",
                    id));
                continue;
            }

            result.Add(new BehaviourParameterOption(value, label ?? value));
        }
        return result;
    }

    private static bool TryKind(string value, out BehaviourParameterKind kind)
    {
        var normalized = value.Trim().Replace("_", "-").ToLowerInvariant();
        kind = normalized switch
        {
            "string" or "text" => BehaviourParameterKind.String,
            "multiline" or "multiline-text" or "textarea" => BehaviourParameterKind.Multiline,
            "integer" or "int" => BehaviourParameterKind.Integer,
            "decimal" or "number" or "float" or "double" => BehaviourParameterKind.Decimal,
            "boolean" or "bool" => BehaviourParameterKind.Boolean,
            "enum" or "choice" or "select" => BehaviourParameterKind.Enum,
            "duration" or "duration-seconds" or "seconds" => BehaviourParameterKind.DurationSeconds,
            "geometry" or "geometry-reference" or "geometry-id" => BehaviourParameterKind.GeometryReference,
            "distance" or "distance-metres" or "metres" or "meters" => BehaviourParameterKind.DistanceMetres,
            "heading" or "heading-degrees" => BehaviourParameterKind.HeadingDegrees,
            "altitude" or "altitude-metres" => BehaviourParameterKind.AltitudeMetres,
            _ => default
        };
        return normalized is
            "string" or "text" or "multiline" or "multiline-text" or "textarea" or
            "integer" or "int" or "decimal" or "number" or "float" or "double" or
            "boolean" or "bool" or "enum" or "choice" or "select" or
            "duration" or "duration-seconds" or "seconds" or
            "geometry" or "geometry-reference" or "geometry-id" or
            "distance" or "distance-metres" or "metres" or "meters" or
            "heading" or "heading-degrees" or "altitude" or "altitude-metres";
    }

    private static string DefaultUnit(BehaviourParameterKind kind) => kind switch
    {
        BehaviourParameterKind.DurationSeconds => "s",
        BehaviourParameterKind.DistanceMetres or BehaviourParameterKind.AltitudeMetres => "m",
        BehaviourParameterKind.HeadingDegrees => "deg",
        _ => string.Empty
    };

    private static string Humanize(string value)
    {
        var chars = value.Replace('_', ' ').Replace('-', ' ').ToCharArray();
        if (chars.Length > 0) chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }
}
