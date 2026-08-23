using RobotCommand.Core;
using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests.Fences;

public sealed class SharedFenceWorkflowTests
{
    [Fact]
    public async Task FenceLibrary_RoundTripsTargetNeutralDocumentAndHash()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var document = new FenceDocument(
                FenceDocument.CurrentSchemaVersion,
                "campus",
                "Campus inclusion",
                FenceKind.Inclusion,
                [
                    new(43.7000, -79.4000),
                    new(43.7000, -79.3900),
                    new(43.7100, -79.3900),
                    new(43.7100, -79.4000)
                ],
                "zone-1", "Campus", "zone-hash", null, null, now, now);

            var store = new FenceLibraryStore(root);
            var saved = await store.SaveAsync(document, replace: false);
            var reloaded = new FenceLibraryStore(root);

            Assert.Equal(FenceDocument.CurrentSchemaVersion, saved.SchemaVersion);
            Assert.False(string.IsNullOrWhiteSpace(saved.ContentSha256));
            Assert.True(reloaded.TryGet("campus", out var loaded));
            Assert.Equal(saved.FenceId, loaded!.FenceId);
            Assert.Equal(saved.DisplayName, loaded.DisplayName);
            Assert.Equal(saved.Kind, loaded.Kind);
            Assert.Equal(saved.Coordinates, loaded.Coordinates);
            Assert.Equal(saved.ContentSha256, loaded.ContentSha256);
            Assert.Equal("zone-hash", loaded.SourceGeometryHash);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task FenceLibrary_RejectsSelfIntersectingAndInvalidAltitudePolygons()
    {
        var now = DateTimeOffset.UtcNow;
        var bowTie = new FenceDocument(
            FenceDocument.CurrentSchemaVersion, "bad", "Bad", FenceKind.Exclusion,
            [new(43.7, -79.4), new(43.71, -79.39), new(43.71, -79.4), new(43.7, -79.39)],
            null, null, null, 20, 10, now, now);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new FenceLibraryStore(TemporaryDirectory()).SaveAsync(bowTie));
        Assert.Contains("altitude", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FenceLibrary_RejectsDuplicateVertices()
    {
        var now = DateTimeOffset.UtcNow;
        var duplicate = new FenceDocument(
            FenceDocument.CurrentSchemaVersion, "duplicate", "Duplicate", FenceKind.Inclusion,
            [new(43.7, -79.4), new(43.7, -79.4), new(43.71, -79.39)],
            null, null, null, null, null, now, now);

        Assert.Throws<InvalidDataException>(() => FenceLibraryStore.Validate(duplicate));
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "robotcommand-fences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
