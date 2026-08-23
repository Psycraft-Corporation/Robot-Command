using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourPackageProtoMapperTests
{
    [Fact]
    public async Task ToProto_PreservesOpaqueManifestTreeAndGeometryPayloads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"logos-behaviour-proto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string manifest = "schema_version: 1\nbt_id: custom-workflow\nversion: 4.2.0\n";
            const string tree = "<root BTCPP_format=\"4\"><BehaviorTree ID=\"Main\"><VendorNode custom_port=\"{opaque_value}\" /></BehaviorTree></root>";
            const string geometry = "{\"slots\":[],\"vendor_extension\":{\"preserve\":true}}";
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.yaml"), manifest);
            await File.WriteAllTextAsync(Path.Combine(root, "tree.xml"), tree);
            await File.WriteAllTextAsync(Path.Combine(root, "geometry.json"), geometry);
            var package = Package(root);
            var mapper = new BehaviourPackageProtoMapper();

            var proto = await mapper.ToProtoAsync(package);

            Assert.Equal("custom-workflow", proto.BehaviourId);
            Assert.Equal("4.2.0", proto.Package.Version);
            Assert.Equal("Custom workflow", proto.Package.DisplayName);
            Assert.Equal(new string('a', 64), proto.PackageSha256);
            Assert.Equal(manifest, proto.ManifestYaml);
            Assert.Equal(tree, proto.TreeXml);
            Assert.Equal(geometry, proto.GeometryJson);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }


    [Fact]
    public void ToValidationResult_KeepsLogosWarningsSubmittable()
    {
        var validation = new global::Logos.Api.V1.ValidationResult
        {
            Status = global::Logos.Api.V1.ValidationStatus.Warning
        };
        validation.Issues.Add(new global::Logos.Api.V1.Issue
        {
            Code = "LOGOS_PACKAGE_WARNING",
            Severity = global::Logos.Api.V1.Severity.Warning,
            Message = "Logos accepted the package with a warning."
        });
        var result = new BehaviourPackageProtoMapper().ToValidationResult(
            validation,
            new global::Logos.Api.V1.DomainStatus
            {
                Ok = true,
                Code = global::Logos.Api.V1.DomainCode.Ok
            },
            new global::Logos.Api.V1.AuthorizationDecision { Allowed = true });

        Assert.True(result.Accepted);
        Assert.Equal(BehaviourPackageValidationState.Warning, result.State);
        Assert.Contains(result.Findings, item => item.Code == "LOGOS_PACKAGE_WARNING");
    }

    [Fact]
    public void ToDeleteResult_MapsActivePackagePreconditionToConflict()
    {
        var response = new global::Logos.Api.V1.DeleteBehaviourPackageResponse
        {
            Authorization = new global::Logos.Api.V1.AuthorizationDecision { Allowed = true },
            Result = new global::Logos.Api.V1.CommandResult
            {
                CommandStatus = global::Logos.Api.V1.CommandStatus.Rejected,
                Status = new global::Logos.Api.V1.DomainStatus
                {
                    Ok = false,
                    Code = global::Logos.Api.V1.DomainCode.FailedPrecondition,
                    Message = "The behaviour package is active."
                }
            }
        };

        var result = new BehaviourPackageProtoMapper().ToDeleteResult(response);

        Assert.False(result.Accepted);
        Assert.Equal(BehaviourPackageCommandState.Conflict, result.State);
        Assert.Contains("active", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalBehaviourPackageRecord Package(string root)
    {
        var identity = new BehaviourPackageIdentity("custom-workflow", "4.2.0");
        return new LocalBehaviourPackageRecord(
            identity,
            new BehaviourPackageLayout(root, "manifest.yaml", "tree.xml", "geometry.json"),
            new BehaviourPackageManifestSummary(
                1,
                identity.BehaviourId,
                "Custom workflow",
                string.Empty,
                identity.Version,
                "tree.xml",
                "geometry.json"),
            "Custom workflow",
            string.Empty,
            new string('a', 64),
            BehaviourPackageLocalState.Imported,
            new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.RobotCommandIntegrity,
                BehaviourPackageValidationState.Valid,
                "Structurally valid.",
                [],
                DateTimeOffset.UtcNow),
            BehaviourPackageValidationResult.NotValidated(BehaviourPackageValidationAuthority.Logos),
            DateTimeOffset.UtcNow,
            ImportedAt: DateTimeOffset.UtcNow);
    }
}
