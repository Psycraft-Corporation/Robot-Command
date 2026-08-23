using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourPackageValidatorTests
{
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Validate_AcceptsWellFormedTreeWithUnknownNodesAndPorts()
    {
        var root = TemporaryDirectory();
        try
        {
            await WriteFilesAsync(
                root,
                """
                <root main_tree_to_execute="MainTree" BTCPP_format="4">
                  <BehaviorTree ID="MainTree">
                    <VendorSpecificNode arbitrary_port="opaque" another_port="{blackboard_key}" />
                  </BehaviorTree>
                </root>
                """,
                """
                { "schema_version": 99, "geometry_slots": [{ "future": true }] }
                """);
            var result = await new BehaviourPackageValidator().ValidateAsync(CreateContext(root));

            Assert.Equal(BehaviourPackageValidationState.Valid, result.State);
            Assert.True(result.Accepted);
            Assert.DoesNotContain(result.Findings, item => item.Severity == BehaviourPackageFindingSeverity.Error);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_RejectsMalformedXmlWithoutInterpretingTreeSemantics()
    {
        var root = TemporaryDirectory();
        try
        {
            await WriteFilesAsync(root, "<root><UnknownNode></root>", "{}");

            var result = await new BehaviourPackageValidator().ValidateAsync(CreateContext(root));

            Assert.Equal(BehaviourPackageValidationState.Invalid, result.State);
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_TREE_XML_INVALID");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_RejectsXmlDtd()
    {
        var root = TemporaryDirectory();
        try
        {
            await WriteFilesAsync(
                root,
                "<!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><root>&xxe;</root>",
                "{}");

            var result = await new BehaviourPackageValidator().ValidateAsync(CreateContext(root));

            Assert.Equal(BehaviourPackageValidationState.Invalid, result.State);
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_TREE_XML_INVALID");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_RejectsMalformedGeometryJsonWhenDeclared()
    {
        var root = TemporaryDirectory();
        try
        {
            await WriteFilesAsync(root, "<root />", "{ not-json }");

            var result = await new BehaviourPackageValidator().ValidateAsync(CreateContext(root));

            Assert.Equal(BehaviourPackageValidationState.Invalid, result.State);
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_GEOMETRY_JSON_INVALID");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_AllowsPackageWithoutGeometryFile()
    {
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.yaml"), "bt_id: test/arm_disarm\ntree_file: tree.xml\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tree.xml"), "<root />");
            var context = CreateContext(root) with
            {
                Layout = new BehaviourPackageLayout(root, "manifest.yaml", "tree.xml", null),
                Manifest = CreateContext(root).Manifest with { GeometryFile = null }
            };

            var result = await new BehaviourPackageValidator().ValidateAsync(context);

            Assert.True(result.Accepted);
            Assert.DoesNotContain(result.Findings, item => item.Code.StartsWith("BEHAVIOUR_PACKAGE_GEOMETRY_", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_AllowsLogosToDecideMissingSchemaAndVersionSupport()
    {
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.yaml"), "bt_id: test/arm_disarm\ntree_file: tree.xml\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tree.xml"), "<root />");
            var context = new BehaviourPackageValidationContext(
                new BehaviourPackageIdentity("test/arm_disarm"),
                new BehaviourPackageLayout(root, "manifest.yaml", "tree.xml", null),
                new BehaviourPackageManifestSummary(
                    0,
                    "test/arm_disarm",
                    "arm_disarm",
                    string.Empty,
                    null,
                    "tree.xml",
                    null),
                Sha256);

            var result = await new BehaviourPackageValidator().ValidateAsync(context);

            Assert.Equal(BehaviourPackageValidationState.Warning, result.State);
            Assert.True(result.Accepted);
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_SCHEMA_VERSION_UNSPECIFIED");
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_VERSION_UNSPECIFIED");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_BlocksPackageWithoutDeclaredTree()
    {
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.yaml"), "bt_id: test/arm_disarm\n");
            var context = CreateContext(root) with
            {
                Layout = new BehaviourPackageLayout(root, "manifest.yaml", null, null),
                Manifest = CreateContext(root).Manifest with { TreeFile = null, GeometryFile = null }
            };

            var result = await new BehaviourPackageValidator().ValidateAsync(context);

            Assert.Equal(BehaviourPackageValidationState.Invalid, result.State);
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_TREE_NOT_DECLARED");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Validate_BlocksIdentityMismatch()
    {
        var root = TemporaryDirectory();
        try
        {
            await WriteFilesAsync(root, "<root />", "{}");
            var context = CreateContext(root) with
            {
                Identity = new BehaviourPackageIdentity("test/different", "1.0.0")
            };

            var result = await new BehaviourPackageValidator().ValidateAsync(context);

            Assert.Equal(BehaviourPackageValidationState.Invalid, result.State);
            Assert.Contains(result.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_IDENTITY_MISMATCH");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static BehaviourPackageValidationContext CreateContext(string root)
        => new(
            new BehaviourPackageIdentity("test/arm_disarm", "1.0.0"),
            new BehaviourPackageLayout(root, "manifest.yaml", "tree.xml", "geometry.json"),
            new BehaviourPackageManifestSummary(
                1,
                "test/arm_disarm",
                "arm_disarm",
                string.Empty,
                "1.0.0",
                "tree.xml",
                "geometry.json"),
            Sha256);

    private static async Task WriteFilesAsync(string root, string tree, string geometry)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "manifest.yaml"),
            "schema_version: 1\nbt_id: test/arm_disarm\nversion: 1.0.0\ntree_file: tree.xml\ngeometry_file: geometry.json\n");
        await File.WriteAllTextAsync(Path.Combine(root, "tree.xml"), tree);
        await File.WriteAllTextAsync(Path.Combine(root, "geometry.json"), geometry);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "logos-behaviour-validator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
