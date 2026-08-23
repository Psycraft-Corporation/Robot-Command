using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryGroupStoreTests
{
    [Fact]
    public void GeometrySelectionWorkflow_EmptySelectionClearsTheCurrentGeometrySelection()
    {
        var selection = new RobotCommand.Services.Workflows.GeometrySelectionWorkflow();
        selection.Set(["zone-alpha"], "zone-alpha");

        selection.Clear();

        Assert.Empty(selection.Current.GeometryIds);
        Assert.Null(selection.Current.AnchorGeometryId);
    }

    [Fact]
    public async Task Groups_AreFlatAndMayContainTheSameGeometryMoreThanOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robotcommand-groups-{Guid.NewGuid():N}");
        try
        {
            var store = new GeometryDocumentStore(root);
            var document = await store.UpsertAsync(Route("route-one"));
            var groups = new GeometryGroupStore(store, new GeometryDocumentCodec());

            await groups.CreateAsync("Survey");
            await groups.CreateAsync("Day one");
            await groups.AssignAsync("Survey", [document.GeometryId]);
            await groups.AssignAsync("Day one", [document.GeometryId]);

            Assert.Equal(["Day one", "Survey"], groups.GetGroups(document.GeometryId));
            Assert.Equal(1, groups.Groups["Survey"].Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectionWorkflow_TracksOnlySessionIdsAndAnchor()
    {
        var selection = new RobotCommand.Services.Workflows.GeometrySelectionWorkflow();

        selection.Set(["zone-a", "route-b", "zone-a"], "route-b");

        Assert.Equal(["zone-a", "route-b"], selection.Current.GeometryIds);
        Assert.Equal("route-b", selection.Current.AnchorGeometryId);
        selection.Clear();
        Assert.False(selection.Current.HasSelection);
    }

    [Fact]
    public async Task DeleteGroup_RejectsNonEmptyGroups()
    {
        var root = Path.Combine(Path.GetTempPath(), $"robotcommand-groups-{Guid.NewGuid():N}");
        try
        {
            var store = new GeometryDocumentStore(root);
            var document = await store.UpsertAsync(Route("route-one"));
            var groups = new GeometryGroupStore(store, new GeometryDocumentCodec());
            await groups.CreateAsync("Survey");
            await groups.AssignAsync("Survey", [document.GeometryId]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => groups.DeleteAsync("Survey"));

            Assert.Contains("not empty", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Survey", groups.Groups.Keys);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GeometrySet_ExportsIndependentDocumentsAndFlatGroups()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"robotcommand-groups-source-{Guid.NewGuid():N}");
        var targetRoot = Path.Combine(Path.GetTempPath(), $"robotcommand-groups-target-{Guid.NewGuid():N}");
        var exportPath = Path.Combine(Path.GetTempPath(), $"robotcommand-geometry-set-{Guid.NewGuid():N}.json");
        try
        {
            var sourceDocuments = new GeometryDocumentStore(sourceRoot);
            var first = await sourceDocuments.UpsertAsync(Route("route-one"));
            var second = await sourceDocuments.UpsertAsync(Route("route-two"));
            var sourceGroups = new GeometryGroupStore(sourceDocuments, new GeometryDocumentCodec());
            await sourceGroups.CreateAsync("Survey");
            await sourceGroups.AssignAsync("Survey", [first.GeometryId, second.GeometryId]);

            await sourceGroups.ExportSetAsync(exportPath, "North survey", [first.GeometryId, second.GeometryId]);

            var targetDocuments = new GeometryDocumentStore(targetRoot);
            var targetGroups = new GeometryGroupStore(targetDocuments, new GeometryDocumentCodec());
            await targetGroups.ImportSetAsync(exportPath, replace: false);

            Assert.Equal(2, targetDocuments.Documents.Count);
            Assert.Equal(["route-one", "route-two"], targetGroups.Groups["Survey"]);
        }
        finally
        {
            if (File.Exists(exportPath)) File.Delete(exportPath);
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, recursive: true);
        }
    }

    private static GeometryDocument Route(string id) => GeometryDocument.Create(id, id, GeometryDocumentKind.WaypointSequence) with
    {
        Points =
        [
            GeometryDocumentPoint.GlobalWgs84(-79.42, 43.73, 20),
            GeometryDocumentPoint.GlobalWgs84(-79.41, 43.74, 20)
        ]
    };
}
