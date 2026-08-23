using RobotCommand.Models;
using YamlDotNet.RepresentationModel;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Reads only the package-level fields Robot Command needs for local cataloguing.
/// It deliberately does not validate BehaviorTree.CPP nodes, ports, plugins, or
/// runtime semantics; Logos remains authoritative for behaviour validity.
/// </summary>
public sealed class BehaviourPackageManifestReader
{
    public async Task<BehaviourPackageManifestSummary> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("A manifest path is required.", nameof(manifestPath));
        }

        var text = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var yaml = new YamlStream();
        using var reader = new StringReader(text);
        yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("The behaviour manifest must contain one YAML mapping document.");
        }

        var behaviourId = RequiredScalar(root, "bt_id", "behaviour_id", "behavior_id");
        var version = OptionalScalar(root, "version", "package_version");
        var displayName = OptionalScalar(root, "name", "display_name")
                          ?? behaviourId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                          ?? behaviourId;
        var description = OptionalScalar(root, "description") ?? string.Empty;
        var treeFile = OptionalScalar(root, "tree_file", "tree");
        var geometryFile = OptionalScalar(root, "geometry_file", "geometry");
        var schemaText = OptionalScalar(root, "schema_version");
        var schemaVersion = int.TryParse(schemaText, out var parsedSchema) ? parsedSchema : 0;

        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "schema_version",
            "bt_id",
            "behaviour_id",
            "behavior_id",
            "version",
            "package_version",
            "name",
            "display_name",
            "description",
            "tree_file",
            "tree",
            "geometry_file",
            "geometry"
        };
        var attributes = root.Children
            .Select(pair => (Key: (pair.Key as YamlScalarNode)?.Value, Value: (pair.Value as YamlScalarNode)?.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && item.Value is not null && !knownKeys.Contains(item.Key!))
            .ToDictionary(item => item.Key!, item => item.Value!, StringComparer.OrdinalIgnoreCase);

        return new BehaviourPackageManifestSummary(
            schemaVersion,
            behaviourId,
            displayName,
            description,
            version,
            treeFile,
            geometryFile,
            attributes);
    }

    private static string RequiredScalar(YamlMappingNode root, params string[] keys)
        => OptionalScalar(root, keys) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"The behaviour manifest is missing required field '{keys[0]}'.");

    private static string? OptionalScalar(YamlMappingNode root, params string[] keys)
    {
        foreach (var pair in root.Children)
        {
            if (pair.Key is not YamlScalarNode keyNode ||
                string.IsNullOrWhiteSpace(keyNode.Value) ||
                !keys.Contains(keyNode.Value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value is not YamlScalarNode valueNode)
            {
                throw new InvalidDataException($"Manifest field '{keyNode.Value}' must be a scalar value.");
            }

            return string.IsNullOrWhiteSpace(valueNode.Value) ? null : valueNode.Value.Trim();
        }

        return null;
    }
}
