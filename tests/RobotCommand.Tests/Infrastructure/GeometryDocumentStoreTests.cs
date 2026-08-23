using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryDocumentStoreTests
{
    [Fact]
    public async Task UpsertAndReload_PersistsCanonicalDocument()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var saved = await store.UpsertAsync(Route());
            var reloaded = new GeometryDocumentStore(root);

            Assert.False(saved.IsDirty);
            Assert.Matches("^[a-f0-9]{64}$", saved.ContentSha256);
            var document = Assert.Single(reloaded.Documents);
            Assert.Equal("route-alpha", document.GeometryId);
            Assert.Equal(saved.ContentSha256, document.ContentSha256);
            Assert.False(document.IsDirty);
            Assert.Empty(reloaded.Issues);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RepeatedUpsert_ReplacesAtomicallyAndLeavesNoStagingFiles()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            await store.UpsertAsync(Route());
            var replacement = await store.UpsertAsync(Route() with
            {
                DisplayName = "Replacement route",
                Points =
                [
                    GeometryDocumentPoint.GlobalWgs84(-79.40, 43.60, 40),
                    GeometryDocumentPoint.GlobalWgs84(-79.41, 43.61, 40)
                ]
            });

            var loaded = new GeometryDocumentStore(root);
            Assert.Equal("Replacement route", Assert.Single(loaded.Documents).DisplayName);
            Assert.Equal(replacement.ContentSha256, loaded.Documents[0].ContentSha256);
            Assert.Empty(Directory.EnumerateFiles(store.StagingPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Refresh_IsolatesMalformedFileAndKeepsValidDocuments()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            await store.UpsertAsync(Route());
            await File.WriteAllTextAsync(
                Path.Combine(store.DocumentsPath, "geometry--broken.geometry.json"),
                "{not-json}");

            await store.RefreshAsync();

            Assert.Single(store.Documents);
            Assert.Contains(store.Issues, item =>
                item.Code == "GEOMETRY_LIBRARY_LOAD_FAILED" &&
                item.Path.EndsWith("geometry--broken.geometry.json", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Refresh_KeepsStructurallyInvalidDraftAndSurfacesValidationIssue()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            await store.UpsertAsync(GeometryDocument.Create(
                "poi-empty",
                "Empty POI draft",
                GeometryDocumentKind.PointOfInterest));

            var reloaded = new GeometryDocumentStore(root);

            Assert.Equal("poi-empty", Assert.Single(reloaded.Documents).GeometryId);
            Assert.Contains(reloaded.Issues, item =>
                item.GeometryId == "poi-empty" && item.Code == "GEOMETRY_POI_POINT_COUNT");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Reload_RemovesAbandonedEmptyLegacyLocalDrafts()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var legacy = GeometryDocument.Create(
                "legacy-empty-poi",
                "Abandoned legacy draft",
                GeometryDocumentKind.PointOfInterest) with
            {
                SchemaVersion = GeometryDocument.LegacyLogosSchemaVersion,
                Origin = GeometryDocumentOrigin.LocalDraft
            };
            var path = GeometryLibraryPaths.DocumentPath(store.DocumentsPath, legacy.GeometryId);
            File.WriteAllText(path, new GeometryDocumentCodec().Serialize(legacy));

            var reloaded = new GeometryDocumentStore(root);

            Assert.Empty(reloaded.Documents);
            Assert.False(File.Exists(path));
            Assert.Empty(reloaded.Issues);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Reload_PreservesLegacyDraftsThatContainGeometry()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var legacy = GeometryDocument.Create(
                "legacy-populated-poi",
                "Legacy point of interest",
                GeometryDocumentKind.PointOfInterest) with
            {
                SchemaVersion = GeometryDocument.LegacyLogosSchemaVersion,
                Origin = GeometryDocumentOrigin.LocalDraft,
                Points = [GeometryDocumentPoint.GlobalWgs84(-79.42, 43.73, 20)]
            };
            var path = GeometryLibraryPaths.DocumentPath(store.DocumentsPath, legacy.GeometryId);
            File.WriteAllText(path, new GeometryDocumentCodec().Serialize(legacy));

            var reloaded = new GeometryDocumentStore(root);

            Assert.Equal("legacy-populated-poi", Assert.Single(reloaded.Documents).GeometryId);
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Import_DoesNotOverwriteUnlessExplicitlyAllowed()
    {
        var root = TemporaryDirectory();
        var importRoot = TemporaryDirectory();
        try
        {
            var codec = new GeometryDocumentCodec();
            var importPath = Path.Combine(importRoot, "route.geometry.json");
            await File.WriteAllTextAsync(
                importPath,
                codec.Serialize(codec.PrepareForSave(Route())));
            var store = new GeometryDocumentStore(root);
            await store.ImportAsync(importPath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ImportAsync(importPath));
            Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);

            var replacement = Route() with { DisplayName = "Imported replacement" };
            await File.WriteAllTextAsync(
                importPath,
                codec.Serialize(codec.PrepareForSave(replacement)));
            var imported = await store.ImportAsync(importPath, allowReplace: true);
            Assert.Equal("Imported replacement", imported.DisplayName);
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(importRoot);
        }
    }

    [Fact]
    public async Task Export_WritesPortableDocumentWithoutMutatingLibrary()
    {
        var root = TemporaryDirectory();
        var exportRoot = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var saved = await store.UpsertAsync(Route());
            var path = Path.Combine(exportRoot, "route-export.json");

            await store.ExportAsync(saved.GeometryId, path);

            Assert.True(File.Exists(path));
            var exported = new GeometryDocumentCodec().Deserialize(await File.ReadAllTextAsync(path));
            Assert.Equal(saved.ContentSha256, exported.ContentSha256);
            Assert.Single(store.Documents);
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(exportRoot);
        }
    }

    [Fact]
    public async Task Remove_DeletesOnlyTheLocalDocument()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            await store.UpsertAsync(Route());
            var path = GeometryLibraryPaths.DocumentPath(store.DocumentsPath, "route-alpha");

            await store.RemoveAsync("route-alpha");

            Assert.Empty(store.Documents);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Upsert_RejectsTraversalAndUsesReservedNameSafeFilePrefix()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var traversal = Route() with { GeometryId = "../escape" };

            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(traversal));
            Assert.False(File.Exists(Path.Combine(root, "escape.geometry.json")));

            var reserved = await store.UpsertAsync(Route() with
            {
                GeometryId = "CON",
                DisplayName = "Reserved filename test"
            });
            var expected = Path.Combine(
                store.DocumentsPath,
                "geometry--CON.geometry.json");
            Assert.Equal("CON", reserved.GeometryId);
            Assert.True(File.Exists(expected));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Import_RejectsSymbolicLinkWhenPlatformSupportsIt()
    {
        var root = TemporaryDirectory();
        var sourceRoot = TemporaryDirectory();
        try
        {
            var codec = new GeometryDocumentCodec();
            var realPath = Path.Combine(sourceRoot, "real.json");
            var linkPath = Path.Combine(sourceRoot, "linked.json");
            await File.WriteAllTextAsync(realPath, codec.Serialize(codec.PrepareForSave(Route())));
            try
            {
                File.CreateSymbolicLink(linkPath, realPath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            var store = new GeometryDocumentStore(root);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ImportAsync(linkPath));
            Assert.Contains("symbolic", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root);
            DeleteDirectory(sourceRoot);
        }
    }

    [Fact]
    public void DefaultRoot_UsesAutonomyGeometryHierarchy()
    {
        var path = GeometryLibraryPaths.DefaultRootPath();

        Assert.True(path.EndsWith(
            Path.Combine("Psycraft", "Robot Command", "Autonomy", "Geometry"),
            StringComparison.OrdinalIgnoreCase));
    }

    private static GeometryDocument Route()
        => GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence,
            DateTimeOffset.Parse("2026-07-27T12:00:00Z")) with
        {
            Description = "Local library test route.",
            Points =
            [
                GeometryDocumentPoint.GlobalWgs84(-79.3832, 43.6532, 50),
                GeometryDocumentPoint.GlobalWgs84(-79.3820, 43.6540, 50)
            ]
        };

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"robot-command-geometry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
