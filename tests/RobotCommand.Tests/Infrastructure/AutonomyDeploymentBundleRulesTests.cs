using RobotCommand.Models;
using RobotCommand.Services.Autonomy;
using Xunit;

namespace RobotCommand.Tests;

public sealed class AutonomyDeploymentBundleRulesTests
{
    [Theory]
    [InlineData("../manifest.json")]
    [InlineData("assets/../../escape.json")]
    [InlineData("/absolute/file.json")]
    [InlineData("C:/absolute/file.json")]
    public void NormalizeRelativePath_RejectsUnsafePaths(string path)
        => Assert.Throws<InvalidDataException>(() =>
            AutonomyDeploymentBundleRules.NormalizeRelativePath(path));

    [Fact]
    public void BuildPlan_OrdersDependenciesAndDefersConnectionScopedMutations()
    {
        var manifest = Manifest();

        var plan = AutonomyDeploymentBundleRules.BuildPlan(manifest);

        Assert.Equal(
            new[]
            {
                AutonomyBundleAssetKind.Geometry,
                AutonomyBundleAssetKind.BehaviourPackage,
                AutonomyBundleAssetKind.BehaviourBinding,
                AutonomyBundleAssetKind.PolicyProfile,
                AutonomyBundleAssetKind.MissionTemplate,
                AutonomyBundleAssetKind.TaskTemplate
            },
            plan.Select(item => item.Kind).ToArray());
        Assert.Equal(AutonomyDeploymentStepState.Deferred, plan[2].State);
        Assert.Equal(AutonomyDeploymentStepState.Deferred, plan[3].State);
        for (var index = 0; index < plan.Count; index++)
        {
            Assert.Equal(index + 1, plan[index].Sequence);
        }
    }

    [Fact]
    public void ValidateManifest_RejectsBindingDependenciesThatAreNotBundled()
    {
        var manifest = Manifest() with
        {
            Behaviours = [],
            Geometry = [],
            Bindings =
            [
                new AutonomyBundleBindingIntent(
                    "survey/search",
                    "2.1.0",
                    "search-area",
                    "zone-alpha",
                    true)
            ]
        };

        var issues = AutonomyDeploymentBundleRules.ValidateManifest(manifest);

        Assert.Contains(issues, item => item.Contains("not included in the bundle", StringComparison.Ordinal));
        Assert.Contains(issues, item => item.Contains("zone-alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateManifest_RequiresDeclaredHashedFilesAndUniquePaths()
    {
        var manifest = Manifest() with
        {
            Files =
            [
                new AutonomyBundleFileEntry(
                    "assets/geometry/zone.json",
                    AutonomyBundleAssetKind.Geometry,
                    10,
                    new string('a', 64)),
                new AutonomyBundleFileEntry(
                    "assets/geometry/zone.json",
                    AutonomyBundleAssetKind.Geometry,
                    10,
                    "not-a-hash")
            ]
        };

        var issues = AutonomyDeploymentBundleRules.ValidateManifest(manifest);

        Assert.Contains(issues, item => item.Contains("Duplicate file path", StringComparison.Ordinal));
        Assert.Contains(issues, item => item.Contains("valid SHA-256", StringComparison.Ordinal));
        Assert.Contains(issues, item => item.Contains("undeclared file", StringComparison.Ordinal));
        Assert.Contains(issues, item => item.Contains("has no declared package files", StringComparison.Ordinal));
    }

    private static AutonomyDeploymentBundleManifest Manifest() => new(
        AutonomyDeploymentBundleManifest.CurrentSchemaVersion,
        "field-demo",
        "Field demo",
        "Dependency-order test",
        DateTimeOffset.UtcNow,
        [new AutonomyBundleBehaviourAsset("survey/search", "2.1.0", "assets/behaviours/search", new string('b', 64))],
        [new AutonomyBundleDocumentAsset("zone-alpha", "assets/geometry/zone.json")],
        [new AutonomyBundleDocumentAsset("field-policy", "assets/policies/policy.json")],
        [new AutonomyBundleDocumentAsset("mission-alpha", "plans/missions/mission.json")],
        [new AutonomyBundleDocumentAsset("task-alpha", "plans/tasks/task.json")],
        [new AutonomyBundleBindingIntent("survey/search", "2.1.0", "search-area", "zone-alpha", true)],
        [
            File("assets/geometry/zone.json", AutonomyBundleAssetKind.Geometry),
            File("assets/behaviours/search/manifest.yaml", AutonomyBundleAssetKind.BehaviourPackage),
            File("assets/behaviours/search/tree.xml", AutonomyBundleAssetKind.BehaviourPackage),
            File("assets/policies/policy.json", AutonomyBundleAssetKind.PolicyProfile),
            File("plans/missions/mission.json", AutonomyBundleAssetKind.MissionTemplate),
            File("plans/tasks/task.json", AutonomyBundleAssetKind.TaskTemplate)
        ]);

    private static AutonomyBundleFileEntry File(string path, AutonomyBundleAssetKind kind)
        => new(path, kind, 1, new string('a', 64));
}
