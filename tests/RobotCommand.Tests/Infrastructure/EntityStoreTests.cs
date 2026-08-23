using RobotCommand.Models;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class EntityStoreTests
{
    [Fact]
    public void Upsert_AddsThenReplacesEntityWithSameKey()
    {
        var store = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);

        store.Upsert(CreateVehicle("vehicle-1", AvailabilityState.Online));
        store.Upsert(CreateVehicle("vehicle-1", AvailabilityState.Degraded));

        Assert.Single(store.Items);
        Assert.Equal(AvailabilityState.Degraded, store.Items[0].State);
    }

    [Fact]
    public void ReplaceAll_RemovesMissingAndKeepsNewEntities()
    {
        var store = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        store.Upsert(CreateVehicle("vehicle-1", AvailabilityState.Online));
        store.Upsert(CreateVehicle("vehicle-2", AvailabilityState.Online));

        store.ReplaceAll([
            CreateVehicle("vehicle-2", AvailabilityState.Degraded),
            CreateVehicle("vehicle-3", AvailabilityState.Online)
        ]);

        Assert.Equal(2, store.Items.Count);
        Assert.DoesNotContain(store.Items, item => item.Id == "vehicle-1");
        Assert.Contains(store.Items, item => item.Id == "vehicle-2" && item.State == AvailabilityState.Degraded);
        Assert.Contains(store.Items, item => item.Id == "vehicle-3");
    }

    [Fact]
    public void Remove_ReturnsFalseForUnknownKey()
    {
        var store = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);

        Assert.False(store.Remove("missing"));
    }

    private static readonly string[] expected = new[] { "two", "one" };

    [Fact]
    public void ReplaceAll_ReordersExistingEntitiesToMatchIncomingOrder()
    {
        var store = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        store.ReplaceAll([
            new ConnectionRecord("one", "One", "http://one", ConnectionMode.Direct, AvailabilityState.Online, true),
            new ConnectionRecord("two", "Two", "http://two", ConnectionMode.Direct, AvailabilityState.Online, true)
        ]);

        store.ReplaceAll([
            new ConnectionRecord("two", "Two", "http://two", ConnectionMode.Direct, AvailabilityState.Online, true),
            new ConnectionRecord("one", "One", "http://one", ConnectionMode.Direct, AvailabilityState.Online, true)
        ]);

        Assert.Equal(expected, store.Items.Select(item => item.Id));
    }

    private static VehicleRecord CreateVehicle(string id, AvailabilityState state)
        => new(
            id,
            "Dracula",
            ["connection-1"],
            "logos-1",
            null,
            "Multicopter",
            "Air",
            "dracula",
            state);
}
