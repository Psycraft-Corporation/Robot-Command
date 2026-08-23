using RobotCommand.Core;
using RobotCommand.Services.Simulation;
using RobotCommand.Simulation;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GhostProfileWorkflowTests
{
    [Fact]
    public async Task CreateUpdateDeleteAndRestart_PersistUserProfiles()
    {
        var directory = CreateDirectory();
        try
        {
            var changed = 0;
            var first = new GhostProfileWorkflow(directory);
            first.Changed += (_, _) => changed++;
            var scout = await first.CreateAsync(new GhostProfileCreateRequest(
                "Scout",
                GhostProfileDefaults.Dracula.Simulation with { MaximumHorizontalSpeedMetresPerSecond = 15 }));

            Assert.Equal("scout", scout.Id);
            Assert.Equal(15, scout.Simulation.MaximumHorizontalSpeedMetresPerSecond);
            Assert.True(changed > 0);

            var updated = await first.UpdateAsync(scout.Id, new GhostProfileUpdateRequest(
                "Scout Fast",
                scout.Simulation with { MaximumClimbRateMetresPerSecond = 4 }));
            Assert.Equal(scout.Id, updated.Id);
            Assert.Equal(4, updated.Simulation.MaximumClimbRateMetresPerSecond);

            var restarted = new GhostProfileWorkflow(directory);
            var loaded = Assert.Single(restarted.Profiles.Where(profile => profile.Id == scout.Id));
            Assert.Equal("Scout Fast", loaded.Name);
            Assert.Equal(4, loaded.Simulation.MaximumClimbRateMetresPerSecond);

            await restarted.DeleteAsync(scout.Id);
            Assert.DoesNotContain(restarted.Profiles, profile => profile.Id == scout.Id);
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public async Task Dracula_IsImmutable_AndInvalidProfilesAreRejected()
    {
        var directory = CreateDirectory();
        try
        {
            var profiles = new GhostProfileWorkflow(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.UpdateAsync(
                "dracula",
                new GhostProfileUpdateRequest("Changed", GhostProfileDefaults.Dracula.Simulation)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.DeleteAsync("dracula"));
            await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(new GhostProfileCreateRequest(
                "Too fast",
                GhostProfileDefaults.Dracula.Simulation with { MaximumHorizontalSpeedMetresPerSecond = 101 })));
            await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.CreateAsync(new GhostProfileCreateRequest(
                "Dracula",
                GhostProfileDefaults.Dracula.Simulation)));
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public async Task MalformedLibrary_IsIgnoredWithoutBlockingStartup()
    {
        var directory = CreateDirectory();
        try
        {
            var data = Path.Combine(directory, "data");
            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(Path.Combine(data, "ghost-profiles.json"), "not json");

            var profiles = new GhostProfileWorkflow(directory);

            Assert.Contains(profiles.Profiles, profile => profile.Id == "dracula");
            Assert.NotEmpty(profiles.LibraryIssues);
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public async Task SimulationEngine_UsesSuppliedCustomProfile()
    {
        var directory = CreateDirectory();
        try
        {
            var profiles = new GhostProfileWorkflow(directory);
            var custom = await profiles.CreateAsync(new GhostProfileCreateRequest(
                "Slow Scout",
                GhostProfileDefaults.Dracula.Simulation with
                {
                    MaximumHorizontalSpeedMetresPerSecond = 3,
                    MaximumClimbRateMetresPerSecond = 1
                }));
            var engine = new GhostSimulationEngine("profile-test");

            var result = engine.Apply(new SimulationCommand(
                "create",
                SimulationCommandKind.Create,
                ProfileId: custom.Id,
                Profile: custom));

            Assert.True(result.Accepted);
            var snapshot = engine.Snapshot();
            var ghost = Assert.Single(snapshot.Ghosts);
            Assert.Equal(custom.Id, ghost.ProfileId);
        }
        finally { TryDelete(directory); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"robotcommand-ghost-profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
