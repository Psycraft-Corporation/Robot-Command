using System.Text.Json;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests.Reconciliation;

public sealed class UnitAssociationWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"robot-command-unit-library-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavesOneVehicleAndCameraSourceWithStableIdAndAuthorityDefaults()
    {
        var service = new UnitDefinitionService(_directory);
        var saved = await service.SaveAsync(null, new UnitDefinitionRequest(
            "Dracula",
            [new("px4", "system-1")],
            [new("direct", "front")]), TestContext.Current.CancellationToken);

        Assert.Equal("Dracula", saved.DisplayName);
        Assert.Single(saved.VehicleSources);
        Assert.Single(saved.CameraSources);
        Assert.Equal("px4", saved.CommandAuthorityConnectionId);
        Assert.Equal(saved.Id, service.Units.Single().Id);
    }

    [Fact]
    public async Task RejectsDuplicateSourceOwnershipAndAllowsOfflineConnectionAssociation()
    {
        var service = new UnitDefinitionService(_directory);
        await service.SaveAsync(null, new UnitDefinitionRequest("One", [new("px4", "system-1")]), TestContext.Current.CancellationToken);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
            null, new UnitDefinitionRequest("Two", [new("px4", "system-1")]), TestContext.Current.CancellationToken));
        Assert.Contains("already associated", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var offline = await service.SaveAsync(
            null, new UnitDefinitionRequest("Offline", [], ConnectionIds: ["camera-link"]), TestContext.Current.CancellationToken);
        Assert.Empty(offline.VehicleSources);
        Assert.Equal(["camera-link"], offline.ConnectionIds);
    }

    [Fact]
    public async Task OfflineConnectionAssociationReconcilesWhenItsVehicleAppears()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var service = new UnitDefinitionService(_directory, vehicles);
        var saved = await service.SaveAsync(null, new UnitDefinitionRequest(
            "Dracula", [], ConnectionIds: ["px4"]), TestContext.Current.CancellationToken);

        Assert.Empty(saved.VehicleSources);

        var vehicle = new VehicleRecord(
            "px4-system-1", "Dracula", ["px4"], null, null, "Multicopter", "Air", "px4",
            AvailabilityState.Online);
        vehicles.Upsert(vehicle);

        var projected = service.ProjectVehicles([vehicle]);
        var associated = Assert.Single(projected);
        Assert.Equal("Dracula", associated.Name);
        Assert.Equal(["px4"], associated.ConnectionIds);
        Assert.Single(service.Units.Single().VehicleSources);
    }

    [Fact]
    public async Task ReadsLegacyArrayAndWritesVersionedDocument()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "data"));
        var legacy = new[]
        {
            new ManualUnitDefinition("unit-1", "Legacy", [new("px4", "system-1")], "px4", "px4", "px4", DateTimeOffset.UtcNow)
        };
        await File.WriteAllTextAsync(Path.Combine(_directory, "data", "units.json"), JsonSerializer.Serialize(legacy));

        var service = new UnitDefinitionService(_directory);
        Assert.Equal("unit-1", Assert.Single(service.Units).Id);

        await service.SaveAsync("unit-1", new UnitDefinitionRequest("Migrated", [new("px4", "system-1")]), TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(Path.Combine(_directory, "data", "units.json"));
        Assert.Contains("fieldconsole.units.v1", text, StringComparison.Ordinal);
        Assert.Contains("Migrated", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenamePreservesIdAndDeleteDoesNotTouchConnections()
    {
        var service = new UnitDefinitionService(_directory);
        var created = await service.SaveAsync(null, new UnitDefinitionRequest("Before", [new("px4", "system-1")]), TestContext.Current.CancellationToken);
        var renamed = await service.SaveAsync(created.Id, new UnitDefinitionRequest("After", [new("px4", "system-1")]), TestContext.Current.CancellationToken);

        Assert.Equal(created.Id, renamed.Id);
        await service.DeleteAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.Empty(service.Units);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
