using RobotCommand.Models;

namespace RobotCommand.Services.Autonomy;

public static class AutonomyDeploymentBundleRules
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("Bundle paths cannot be empty.");
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Bundle path '{path}' must be relative.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(item => item is "." or ".."))
        {
            throw new InvalidDataException($"Bundle path '{path}' is unsafe.");
        }

        return string.Join('/', segments);
    }

    public static string SafeSegment(string value, string fallback = "asset")
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var result = new string(source
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray())
            .Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    public static string ResolveContainedPath(string rootPath, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison) &&
            !string.Equals(candidate, root, PathComparison))
        {
            throw new InvalidDataException($"Bundle path '{relativePath}' escapes the extraction root.");
        }
        return candidate;
    }

    public static IReadOnlyList<AutonomyDeploymentStep> BuildPlan(
        AutonomyDeploymentBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var steps = new List<AutonomyDeploymentStep>();
        var sequence = 1;

        foreach (var geometry in manifest.Geometry.OrderBy(item => item.AssetId, StringComparer.Ordinal))
        {
            steps.Add(new AutonomyDeploymentStep(
                sequence++,
                AutonomyBundleAssetKind.Geometry,
                geometry.AssetId,
                "Import geometry",
                AutonomyDeploymentStepState.Planned,
                "Import through the local geometry document store before deploying dependent behaviours."));
        }

        foreach (var behaviour in manifest.Behaviours
                     .OrderBy(item => item.BehaviourId, StringComparer.Ordinal)
                     .ThenBy(item => item.Version, StringComparer.Ordinal))
        {
            steps.Add(new AutonomyDeploymentStep(
                sequence++,
                AutonomyBundleAssetKind.BehaviourPackage,
                behaviour.Identity.Key,
                "Import behaviour package",
                AutonomyDeploymentStepState.Planned,
                "Import the complete package folder through the managed behaviour library."));
        }

        foreach (var binding in manifest.Bindings
                     .OrderBy(item => item.BehaviourId, StringComparer.Ordinal)
                     .ThenBy(item => item.Version, StringComparer.Ordinal)
                     .ThenBy(item => item.SlotId, StringComparer.Ordinal))
        {
            steps.Add(new AutonomyDeploymentStep(
                sequence++,
                AutonomyBundleAssetKind.BehaviourBinding,
                $"{binding.Identity.Key}:{binding.SlotId}",
                "Apply geometry binding",
                AutonomyDeploymentStepState.Deferred,
                $"Bind slot '{binding.SlotId}' to geometry '{binding.GeometryId}' after both assets are installed on a selected Logos runtime."));
        }

        foreach (var policy in manifest.Policies.OrderBy(item => item.AssetId, StringComparer.Ordinal))
        {
            steps.Add(new AutonomyDeploymentStep(
                sequence++,
                AutonomyBundleAssetKind.PolicyProfile,
                policy.AssetId,
                "Import policy profile",
                AutonomyDeploymentStepState.Deferred,
                "The policy document is staged for the Policy workspace; activation remains an explicit Logos operation."));
        }

        foreach (var mission in manifest.Missions.OrderBy(item => item.AssetId, StringComparer.Ordinal))
        {
            steps.Add(new AutonomyDeploymentStep(
                sequence++,
                AutonomyBundleAssetKind.MissionTemplate,
                mission.AssetId,
                "Import mission template",
                AutonomyDeploymentStepState.Planned,
                "Import as a local mission draft after its referenced assets are available."));
        }

        foreach (var task in manifest.Tasks.OrderBy(item => item.AssetId, StringComparer.Ordinal))
        {
            steps.Add(new AutonomyDeploymentStep(
                sequence++,
                AutonomyBundleAssetKind.TaskTemplate,
                task.AssetId,
                "Import task template",
                AutonomyDeploymentStepState.Planned,
                "Import as a local task draft after its referenced assets are available."));
        }

        return steps;
    }

    public static IReadOnlyList<string> ValidateManifest(AutonomyDeploymentBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<string>();
        var files = manifest.Files ?? [];
        var behaviours = manifest.Behaviours ?? [];
        var geometry = manifest.Geometry ?? [];
        var policies = manifest.Policies ?? [];
        var missions = manifest.Missions ?? [];
        var tasks = manifest.Tasks ?? [];
        var bindings = manifest.Bindings ?? [];

        if (manifest.Files is null) issues.Add("The bundle file catalogue is required.");
        if (manifest.Behaviours is null) issues.Add("The behaviour asset catalogue is required.");
        if (manifest.Geometry is null) issues.Add("The geometry asset catalogue is required.");
        if (manifest.Policies is null) issues.Add("The policy asset catalogue is required.");
        if (manifest.Missions is null) issues.Add("The mission asset catalogue is required.");
        if (manifest.Tasks is null) issues.Add("The task asset catalogue is required.");
        if (manifest.Bindings is null) issues.Add("The binding-intent catalogue is required.");

        if (!string.Equals(
                manifest.SchemaVersion,
                AutonomyDeploymentBundleManifest.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            issues.Add($"Unsupported bundle schema '{manifest.SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(manifest.BundleId))
        {
            issues.Add("The bundle ID is required.");
        }
        else if (!string.Equals(
                     SafeSegment(manifest.BundleId, "autonomy-bundle"),
                     manifest.BundleId,
                     StringComparison.Ordinal))
        {
            issues.Add("The bundle ID may contain only letters, numbers, periods, hyphens, and underscores.");
        }
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            issues.Add("The bundle display name is required.");
        }

        ValidateUniquePaths(files.Select(item => item.Path), "file", issues);
        ValidateUniquePaths(behaviours.Select(item => item.RelativeDirectory), "behaviour directory", issues);
        ValidateUniquePaths(geometry.Select(item => item.RelativePath), "geometry document", issues);
        ValidateUniquePaths(policies.Select(item => item.RelativePath), "policy document", issues);
        ValidateUniquePaths(missions.Select(item => item.RelativePath), "mission document", issues);
        ValidateUniquePaths(tasks.Select(item => item.RelativePath), "task document", issues);
        ValidateUniqueIds(geometry.Select(item => item.AssetId), "geometry", issues);
        ValidateUniqueIds(policies.Select(item => item.AssetId), "policy", issues);
        ValidateUniqueIds(missions.Select(item => item.AssetId), "mission", issues);
        ValidateUniqueIds(tasks.Select(item => item.AssetId), "task", issues);

        var behaviourIdentities = new HashSet<BehaviourPackageIdentity>();
        foreach (var behaviour in behaviours)
        {
            if (!behaviourIdentities.Add(behaviour.Identity))
            {
                issues.Add($"Behaviour '{behaviour.Identity.Key}' is declared more than once.");
            }
        }

        var declaredFiles = files
            .Select(item => NormalizeOrIssue(item.Path, issues))
            .Where(item => item is not null)
            .Cast<string>()
            .ToHashSet(PathComparer);
        foreach (var file in files)
        {
            if (file.SizeBytes < 0)
            {
                issues.Add($"Bundle file '{file.Path}' has an invalid size.");
            }
            if (string.IsNullOrWhiteSpace(file.Sha256) ||
                file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit))
            {
                issues.Add($"Bundle file '{file.Path}' does not declare a valid SHA-256 hash.");
            }

            var path = NormalizeOrIssue(file.Path, issues);
            var expectedKind = path is null ? null : ExpectedKind(path);
            if (expectedKind is null)
            {
                if (path is not null)
                {
                    issues.Add($"Bundle file '{path}' is outside a supported asset directory.");
                }
            }
            else if (!Enum.IsDefined(file.Kind) || file.Kind != expectedKind.Value)
            {
                issues.Add($"Bundle file '{file.Path}' declares asset kind '{file.Kind}', expected '{expectedKind}'.");
            }
        }

        foreach (var asset in geometry.Concat(policies).Concat(missions).Concat(tasks))
        {
            var path = NormalizeOrIssue(asset.RelativePath, issues);
            if (path is not null && !declaredFiles.Contains(path))
            {
                issues.Add($"Asset '{asset.AssetId}' references undeclared file '{path}'.");
            }
        }

        foreach (var behaviour in behaviours)
        {
            var root = NormalizeOrIssue(behaviour.RelativeDirectory, issues);
            if (root is null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(behaviour.BehaviourId))
            {
                issues.Add("Every behaviour asset requires a behaviour ID.");
            }
            if (!declaredFiles.Any(path => path.StartsWith(root + "/", PathComparison)))
            {
                issues.Add($"Behaviour '{behaviour.Identity.Key}' has no declared package files.");
            }
            if (!declaredFiles.Any(path =>
                    path.StartsWith(root + "/", PathComparison) &&
                    (Path.GetFileName(path).Equals("manifest.yaml", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(path).Equals("manifest.yml", StringComparison.OrdinalIgnoreCase))))
            {
                issues.Add($"Behaviour '{behaviour.Identity.Key}' does not contain a manifest.yaml or manifest.yml file.");
            }
        }

        var bindingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.BehaviourId) ||
                string.IsNullOrWhiteSpace(binding.SlotId) ||
                string.IsNullOrWhiteSpace(binding.GeometryId))
            {
                issues.Add("Every binding intent requires behaviour, slot, and geometry IDs.");
                continue;
            }
            var key = $"{binding.Identity.Key}:{binding.SlotId}";
            if (!bindingKeys.Add(key))
            {
                issues.Add($"Binding intent '{key}' is declared more than once.");
            }
            if (!behaviourIdentities.Contains(binding.Identity))
            {
                issues.Add($"Binding intent '{key}' references behaviour '{binding.Identity.Key}', which is not included in the bundle.");
            }
            if (!geometry.Any(item => string.Equals(item.AssetId, binding.GeometryId, StringComparison.Ordinal)))
            {
                issues.Add($"Binding intent '{key}' references geometry '{binding.GeometryId}', which is not included in the bundle.");
            }
        }

        return issues.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static AutonomyBundleAssetKind? ExpectedKind(string path)
        => path switch
        {
            var value when value.StartsWith("assets/geometry/", PathComparison) => AutonomyBundleAssetKind.Geometry,
            var value when value.StartsWith("assets/behaviours/", PathComparison) => AutonomyBundleAssetKind.BehaviourPackage,
            var value when value.StartsWith("assets/policies/", PathComparison) => AutonomyBundleAssetKind.PolicyProfile,
            var value when value.StartsWith("plans/missions/", PathComparison) => AutonomyBundleAssetKind.MissionTemplate,
            var value when value.StartsWith("plans/tasks/", PathComparison) => AutonomyBundleAssetKind.TaskTemplate,
            _ => null
        };

    private static void ValidateUniqueIds(
        IEnumerable<string> values,
        string label,
        ICollection<string> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Add($"Every {label} asset requires an ID.");
                continue;
            }
            if (!seen.Add(value.Trim()))
            {
                issues.Add($"{label} asset ID '{value.Trim()}' is declared more than once.");
            }
        }
    }

    private static void ValidateUniquePaths(
        IEnumerable<string> values,
        string label,
        ICollection<string> issues)
    {
        var seen = new HashSet<string>(PathComparer);
        foreach (var value in values)
        {
            var normalized = NormalizeOrIssue(value, issues);
            if (normalized is not null && !seen.Add(normalized))
            {
                issues.Add($"Duplicate {label} path '{normalized}'.");
            }
        }
    }

    private static string? NormalizeOrIssue(string value, ICollection<string> issues)
    {
        try
        {
            return NormalizeRelativePath(value);
        }
        catch (Exception ex)
        {
            issues.Add(ex.Message);
            return null;
        }
    }
}
