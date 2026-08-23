using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Behaviours;
using Xunit;

namespace RobotCommand.Tests;

public sealed class BehaviourPackageStoreTests
{
    [Fact]
    public async Task ImportDirectory_PreservesOpaquePackageAndUsesManifestIdentity()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<UnknownNode custom_port=\"opaque\" />");
            var store = CreateStore(libraryRoot);

            var result = await store.ImportDirectoryAsync(source);

            Assert.False(result.Replaced);
            Assert.Equal("test/arm_disarm", result.Package.Identity.BehaviourId);
            Assert.Null(result.Package.Identity.Version);
            Assert.Equal("arm_disarm", result.Package.DisplayName);
            Assert.Equal(BehaviourPackageLocalState.Imported, result.Package.State);
            Assert.True(result.Package.Integrity.Accepted);
            Assert.True(result.Package.CanSubmitToLogos);
            Assert.Equal(BehaviourPackageValidationState.NotValidated, result.Package.LogosValidation.State);
            Assert.Matches("^[a-f0-9]{64}$", result.Package.ContentSha256!);
            Assert.NotEqual(Path.GetFullPath(source), result.Package.Layout.PackageDirectory);
            Assert.Contains(
                "UnknownNode",
                await File.ReadAllTextAsync(Path.Combine(result.Package.Layout.PackageDirectory, "tree.xml")));
            Assert.Single(store.Packages);
            Assert.Empty(store.Issues);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task ImportDirectory_RequiresExplicitReplacementAndLeavesNoStagingFiles()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var first = await store.ImportDirectoryAsync(source);

            await WritePackageAsync(source, treeBody: "<AlwaysFailure />");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportDirectoryAsync(source));
            var replacement = await store.ImportDirectoryAsync(source, allowReplace: true);

            Assert.True(replacement.Replaced);
            Assert.NotEqual(first.Package.ContentSha256, replacement.Package.ContentSha256);
            Assert.Empty(Directory.EnumerateFileSystemEntries(BehaviourPackageLibraryPaths.StagingPath(libraryRoot)));
            Assert.Contains(
                "AlwaysFailure",
                await File.ReadAllTextAsync(Path.Combine(replacement.Package.Layout.PackageDirectory, "tree.xml")));
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task Refresh_DetectsFilesChangedOutsideTheStore()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);
            await File.AppendAllTextAsync(
                Path.Combine(imported.Package.Layout.PackageDirectory, "tree.xml"),
                Environment.NewLine + "<!-- changed -->");

            await store.RefreshAsync();

            var package = Assert.Single(store.Packages);
            Assert.Equal(BehaviourPackageLocalState.Modified, package.State);
            Assert.Equal(BehaviourPackageValidationState.Warning, package.Integrity.State);
            Assert.False(package.CanSubmitToLogos);
            Assert.Contains(package.Integrity.Findings, item => item.Code == "BEHAVIOUR_LIBRARY_CONTENT_MODIFIED");
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task ImportDirectory_RejectsManifestPathTraversal()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "manifest.yaml"),
                """
                schema_version: 1
                bt_id: test/escape
                name: escape
                tree_file: ../tree.xml
                """);
            var store = CreateStore(libraryRoot);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportDirectoryAsync(source));

            Assert.Contains("escapes", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(store.Packages);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task ImportDirectory_RejectsSymbolicLinkWhenPlatformSupportsIt()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        var outside = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var outsideTree = Path.Combine(outside, "outside.xml");
            await File.WriteAllTextAsync(outsideTree, "<Outside />");
            var linkedTree = Path.Combine(source, "linked.xml");
            try
            {
                File.CreateSymbolicLink(linkedTree, outsideTree);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            var store = CreateStore(libraryRoot);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportDirectoryAsync(source));
            Assert.Contains("symbolic", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
            DeleteDirectory(outside);
        }
    }

    [Fact]
    public async Task ExportAndRemove_OperateOnLibraryCopyOnly()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        var export = Path.Combine(TemporaryDirectory(), "arm-disarm");
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);

            await store.ExportDirectoryAsync(imported.Package.Identity, export);
            await store.RemoveAsync(imported.Package.Identity);

            Assert.True(File.Exists(Path.Combine(source, "manifest.yaml")));
            Assert.True(File.Exists(Path.Combine(export, "manifest.yaml")));
            Assert.True(File.Exists(Path.Combine(export, "tree.xml")));
            Assert.Empty(store.Packages);
            Assert.False(Directory.Exists(imported.Package.Layout.PackageDirectory));
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
            DeleteDirectory(Path.GetDirectoryName(export)!);
        }
    }

    [Fact]
    public async Task Reload_KeepsPackageVisibleWhenMetadataIsMalformed()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);
            var metadataPath = BehaviourPackageLibraryPaths.MetadataFilePath(
                libraryRoot,
                imported.Package.Identity);
            await File.WriteAllTextAsync(metadataPath, "{not-json}");

            var reloaded = CreateStore(libraryRoot);

            var package = Assert.Single(reloaded.Packages);
            Assert.Equal(imported.Package.Identity, package.Identity);
            Assert.Equal(BehaviourPackageLocalState.Discovered, package.State);
            Assert.Equal(BehaviourPackageValidationState.Warning, package.Integrity.State);
            Assert.Contains(
                reloaded.Issues,
                item => item.Code == "BEHAVIOUR_LIBRARY_METADATA_LOAD_FAILED");
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task ImportDirectory_RejectsManagedLibraryOverlap()
    {
        var libraryRoot = TemporaryDirectory();
        try
        {
            var source = Path.Combine(libraryRoot, "incoming");
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ImportDirectoryAsync(source));

            Assert.Contains("overlap", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
        }
    }

    [Fact]
    public async Task SetRemoteBaseline_PersistsAcrossStoreReload()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);

            await store.SetRemoteBaselineAsync(imported.Package.Identity, "ABC123");
            var reloaded = CreateStore(libraryRoot);

            Assert.Equal("abc123", Assert.Single(reloaded.Packages).RemoteBaselineSha256);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task ImportDirectory_KeepsStructurallyInvalidPackageButBlocksLogosSubmission()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<UnknownNode>");
            var store = CreateStore(libraryRoot);

            var imported = await store.ImportDirectoryAsync(source);

            Assert.Equal(BehaviourPackageLocalState.Invalid, imported.Package.State);
            Assert.Equal(BehaviourPackageValidationState.Invalid, imported.Package.Integrity.State);
            Assert.False(imported.Package.CanSubmitToLogos);
            Assert.Contains("blocking issues", imported.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                imported.Package.Integrity.Findings,
                item => item.Code == "BEHAVIOUR_PACKAGE_TREE_XML_INVALID");
            Assert.True(File.Exists(Path.Combine(imported.Package.Layout.PackageDirectory, "tree.xml")));
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task RefreshAsync_RechecksManagedPackageAndReturnsCurrentFindings()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);
            await File.WriteAllTextAsync(
                Path.Combine(imported.Package.Layout.PackageDirectory, "geometry.json"),
                "{ invalid }");

            await store.RefreshAsync();

            var refreshed = Assert.Single(store.Packages);
            Assert.Equal(BehaviourPackageValidationState.Invalid, refreshed.Integrity.State);
            Assert.Contains(refreshed.Integrity.Findings, item => item.Code == "BEHAVIOUR_PACKAGE_GEOMETRY_JSON_INVALID");
            Assert.Equal(BehaviourPackageLocalState.Invalid, refreshed.State);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    [Fact]
    public async Task SetLogosValidation_PersistsAcrossStoreReload()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);
            var validation = new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.Logos,
                BehaviourPackageValidationState.Warning,
                "Logos accepted the package with warnings.",
                [new BehaviourPackageValidationFinding(
                    "LOGOS_WARNING",
                    BehaviourPackageFindingSeverity.Warning,
                    "Review the package warning.")],
                DateTimeOffset.UtcNow);

            await store.SetLogosValidationAsync(imported.Package.Identity, validation);
            var reloaded = CreateStore(libraryRoot);

            var package = Assert.Single(reloaded.Packages);
            Assert.Equal(BehaviourPackageValidationState.Warning, package.LogosValidation.State);
            Assert.Equal(BehaviourPackageValidationAuthority.Logos, package.LogosValidation.Authority);
            Assert.Contains(package.LogosValidation.Findings, item => item.Code == "LOGOS_WARNING");
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }


    [Fact]
    public async Task Refresh_ClearsDisplayedLogosValidationWhenPackageContentChanges()
    {
        var libraryRoot = TemporaryDirectory();
        var source = TemporaryDirectory();
        try
        {
            await WritePackageAsync(source, treeBody: "<AlwaysSuccess />");
            var store = CreateStore(libraryRoot);
            var imported = await store.ImportDirectoryAsync(source);
            await store.SetLogosValidationAsync(
                imported.Package.Identity,
                new BehaviourPackageValidationResult(
                    BehaviourPackageValidationAuthority.Logos,
                    BehaviourPackageValidationState.Valid,
                    "Logos accepted the package.",
                    [],
                    DateTimeOffset.UtcNow));
            await File.AppendAllTextAsync(imported.Package.Layout.TreeFile is null
                ? throw new InvalidOperationException("Imported package did not retain its tree path.")
                : BehaviourPackageLibraryPaths.ResolvePackageFile(
                    imported.Package.Layout.PackageDirectory,
                    imported.Package.Layout.TreeFile,
                     "tree"), "\n<!-- changed -->");

            await store.RefreshAsync();

            var package = Assert.Single(store.Packages);
            Assert.Equal(BehaviourPackageLocalState.Modified, package.State);
            Assert.Equal(BehaviourPackageValidationState.NotValidated, package.LogosValidation.State);
            Assert.Contains("changed", package.LogosValidation.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(libraryRoot);
            DeleteDirectory(source);
        }
    }

    private static BehaviourPackageStore CreateStore(string root)
        => new(
            new AppConfiguration
            {
                BehaviourLibraryPath = root,
                MaximumBehaviourPackageBytes = 1024 * 1024,
                MaximumBehaviourPackageFiles = 50
            },
            NullLogger<BehaviourPackageStore>.Instance);

    private static async Task WritePackageAsync(string directory, string treeBody)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.yaml"),
            """
            schema_version: 1
            bt_id: test/arm_disarm
            name: arm_disarm
            description: ''
            tree_file: tree.xml
            geometry_file: geometry.json
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "tree.xml"),
            $"<root main_tree_to_execute=\"MainTree\"><BehaviorTree ID=\"MainTree\">{treeBody}</BehaviorTree></root>");
        await File.WriteAllTextAsync(Path.Combine(directory, "geometry.json"),
            """
            {
              "schema_version": 1,
              "geometry_slots": []
            }
            """);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "logos-behaviour-library-tests", Guid.NewGuid().ToString("N"));
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
