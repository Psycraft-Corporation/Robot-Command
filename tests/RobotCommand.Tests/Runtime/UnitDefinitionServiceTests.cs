using RobotCommand.Models;
using RobotCommand.Services.Reconciliation;
using Xunit;

namespace RobotCommand.Tests.Reconciliation;

public sealed class UnitDefinitionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"robot-command-unit-definition-{Guid.NewGuid():N}");

    [Fact]
    public async Task AssociatesConnectionsWithoutDestroyingBackendObservations()
    {
        var service = new UnitDefinitionService(_directory);

        var definition = await service.SaveAsync(
            null,
            "Dracula",
            [new("logos", "logos-dracula"), new("px4", "px4-system-1")],
            "logos",
            "px4",
            "px4",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, definition.Connections.Count);
        Assert.Equal("logos-dracula", service.ResolveCommandSource("px4-system-1"));
        Assert.Equal("px4-system-1", service.ResolveTelemetrySource("logos-dracula"));
        Assert.Equal("px4-system-1", service.ResolveDiagnosticsSource("logos-dracula"));
        Assert.Equal("Dracula", service.DisplayNameFor("logos-dracula", "fallback"));
    }

    [Fact]
    public async Task ProjectionShowsOneUnitWithAllAssociatedConnections()
    {
        var service = new UnitDefinitionService(_directory);
        await service.SaveAsync(
            null,
            "Dracula",
            [new("logos", "logos-dracula"), new("px4", "px4-system-1")],
            "logos",
            "px4",
            "px4",
            TestContext.Current.CancellationToken);
        var observations = new[]
        {
            Vehicle("logos-dracula", "Logos Dracula", "logos"),
            Vehicle("px4-system-1", "PX4 System 1", "px4"),
            Vehicle("wingman", "Wingman", "wingman-link")
        };

        var projected = service.ProjectVehicles(observations);

        Assert.Equal(2, projected.Count);
        var dracula = Assert.Single(projected, item => item.Name == "Dracula");
        Assert.Equal("logos-dracula", dracula.Id);
        Assert.Equal(["logos", "px4"], dracula.ConnectionIds);
        Assert.Contains(projected, item => item.Id == "wingman");
    }

    [Fact]
    public async Task DefinitionPersistsAndRemovingItRestoresIndependentObservations()
    {
        var service = new UnitDefinitionService(_directory);
        var definition = await service.SaveAsync(
            null,
            "Dracula",
            [new("logos", "logos-dracula"), new("px4", "px4-system-1")],
            "logos",
            "px4",
            "px4",
            TestContext.Current.CancellationToken);

        var reloaded = new UnitDefinitionService(_directory);
        Assert.Single(reloaded.Definitions);
        Assert.True(File.Exists(Path.Combine(_directory, "data", "units.json")));

        await reloaded.RemoveAsync(definition.Id, TestContext.Current.CancellationToken);
        var projected = reloaded.ProjectVehicles([
            Vehicle("logos-dracula", "Logos Dracula", "logos"),
            Vehicle("px4-system-1", "PX4 System 1", "px4")]);

        Assert.Equal(2, projected.Count);
        Assert.Empty(new UnitDefinitionService(_directory).Definitions);
    }

    [Fact]
    public async Task RejectsAuthorityFromAnUnassociatedConnection()
    {
        var service = new UnitDefinitionService(_directory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
            null,
            "Dracula",
            [new("logos", "logos-dracula"), new("px4", "px4-system-1")],
            "other",
            "px4",
            "px4",
            TestContext.Current.CancellationToken));

        Assert.Contains("command authority", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneMavlinkConnectionCanBindDifferentSystemObservationsToDifferentUnits()
    {
        var service = new UnitDefinitionService(_directory);
        await service.SaveAsync(
            null,
            "Dracula",
            [new("mavlink", "system-1"), new("logos-1", "logos-dracula")],
            "logos-1",
            "mavlink",
            "mavlink",
            TestContext.Current.CancellationToken);
        await service.SaveAsync(
            null,
            "Wingman",
            [new("mavlink", "system-2"), new("logos-2", "logos-wingman")],
            "logos-2",
            "mavlink",
            "mavlink",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, service.Definitions.Count);
        Assert.Equal("Dracula", service.FindByBinding("mavlink", "system-1")?.DisplayName);
        Assert.Equal("Wingman", service.FindByBinding("mavlink", "system-2")?.DisplayName);
    }

    private static VehicleRecord Vehicle(string id, string name, string connectionId)
        => new(id, name, [connectionId], null, null, "Multicopter", "Air", "test",
            AvailabilityState.Online, "Ready", "Landed", "Disarmed", "Healthy");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
