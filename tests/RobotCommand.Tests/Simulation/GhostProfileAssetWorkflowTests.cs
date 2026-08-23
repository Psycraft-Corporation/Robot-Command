using RobotCommand.Core;
using RobotCommand.Services.Simulation;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GhostProfileAssetWorkflowTests
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task ImageImportPersistsMetadataAndCanBeReadAfterRestart()
    {
        var directory = CreateDirectory();
        try
        {
            var source = Path.Combine(directory, "icon.png");
            await File.WriteAllBytesAsync(source, Convert.FromBase64String(OnePixelPng));
            var profiles = new GhostProfileWorkflow(directory);
            var profile = await profiles.CreateAsync(new GhostProfileCreateRequest("Scout", GhostProfileDefaults.Dracula.Simulation));
            var assets = new GhostProfileAssetWorkflow(profiles, directory);

            var imported = await assets.ImportAsync(profile.Id, source);

            Assert.Equal(GhostProfileAssetKind.Image, imported.Kind);
            Assert.Equal(1, imported.Width);
            Assert.Equal(1, imported.Height);
            Assert.NotEmpty(imported.Sha256);
            var handle = await assets.OpenReadAsync(profile.Id);
            Assert.True(File.Exists(handle!.ManagedFilePath));

            var restartedProfiles = new GhostProfileWorkflow(directory);
            Assert.Equal(imported, restartedProfiles.Find(profile.Id)!.Asset);
            var restartedAssets = new GhostProfileAssetWorkflow(restartedProfiles, directory);
            var restartedHandle = await restartedAssets.OpenReadAsync(profile.Id);
            Assert.NotNull(restartedHandle);
            Assert.True(File.Exists(restartedHandle!.ManagedFilePath));
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public async Task MeshReplacementAndInvalidReplacementPreserveTheApprovedAsset()
    {
        var directory = CreateDirectory();
        try
        {
            var image = Path.Combine(directory, "icon.png");
            var obj = Path.Combine(directory, "model.obj");
            var invalid = Path.Combine(directory, "bad.obj");
            await File.WriteAllBytesAsync(image, Convert.FromBase64String(OnePixelPng));
            await File.WriteAllTextAsync(obj, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            await File.WriteAllTextAsync(invalid, "not a mesh");
            var profiles = new GhostProfileWorkflow(directory);
            var profile = await profiles.CreateAsync(new GhostProfileCreateRequest("Model Scout", GhostProfileDefaults.Dracula.Simulation));
            var assets = new GhostProfileAssetWorkflow(profiles, directory);
            var first = await assets.ImportAsync(profile.Id, image);

            var mesh = await assets.ImportAsync(profile.Id, obj);
            Assert.Equal(GhostProfileAssetKind.Mesh, mesh.Kind);
            Assert.Equal(3, mesh.VertexCount);
            Assert.NotEqual(first.Sha256, mesh.Sha256);
            await Assert.ThrowsAsync<InvalidDataException>(() => assets.ImportAsync(profile.Id, invalid));
            Assert.Equal(mesh, assets.Find(profile.Id));

            await assets.RemoveAsync(profile.Id);
            Assert.Null(assets.Find(profile.Id));
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public async Task DraculaIsFullyReadOnly()
    {
        var directory = CreateDirectory();
        try
        {
            var source = Path.Combine(directory, "icon.png");
            await File.WriteAllBytesAsync(source, Convert.FromBase64String(OnePixelPng));
            var profiles = new GhostProfileWorkflow(directory);
            var assets = new GhostProfileAssetWorkflow(profiles, directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => assets.ImportAsync("dracula", source));
            await Assert.ThrowsAsync<InvalidOperationException>(() => assets.RemoveAsync("dracula"));
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public async Task DeletingProfileCleansManagedAssetPackage()
    {
        var directory = CreateDirectory();
        try
        {
            var source = Path.Combine(directory, "icon.png");
            await File.WriteAllBytesAsync(source, Convert.FromBase64String(OnePixelPng));
            var profiles = new GhostProfileWorkflow(directory);
            var profile = await profiles.CreateAsync(new GhostProfileCreateRequest("Disposable", GhostProfileDefaults.Dracula.Simulation));
            var assets = new GhostProfileAssetWorkflow(profiles, directory);
            var imported = await assets.ImportAsync(profile.Id, source);
            var package = Path.GetDirectoryName((await assets.OpenReadAsync(profile.Id))!.ManagedFilePath)!;

            await profiles.DeleteAsync(profile.Id);

            Assert.False(Directory.Exists(package));
            Assert.Null(imported is null ? null : profiles.Find(profile.Id));
        }
        finally { TryDelete(directory); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "robot-command-ghost-assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
