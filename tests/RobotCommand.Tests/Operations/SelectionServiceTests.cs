using RobotCommand.Models;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SelectionServiceTests
{
    [Fact]
    public void Select_UpdatesSharedContextAndRaisesChanged()
    {
        var service = new SelectionService();
        var changed = 0;
        service.Changed += (_, _) => changed++;

        service.Select(new OperationalSelection(
            SelectionKind.Vehicle,
            "vehicle-1",
            "Dracula",
            "Multicopter",
            [new SelectionField("State", "Online")]));

        Assert.Equal(SelectionKind.Vehicle, service.Current.Kind);
        Assert.Equal("vehicle-1", service.Current.Id);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Clear_RestoresEmptySelection()
    {
        var service = new SelectionService();
        service.Select(new OperationalSelection(
            SelectionKind.Team,
            "team-1",
            "Test team",
            "2 members",
            Array.Empty<SelectionField>()));

        service.Clear();

        Assert.Equal(OperationalSelection.None, service.Current);
    }

    [Fact]
    public void SetUnitSelectionTracksIdsAndAnchor()
    {
        var service = new SelectionService();
        var first = new OperationalSelection(SelectionKind.Vehicle, "vehicle-1", "One", "", []);
        var second = new OperationalSelection(SelectionKind.Vehicle, "vehicle-2", "Two", "", []);

        service.SetUnitSelection([first, second], "vehicle-1");

        Assert.Equal(["vehicle-1", "vehicle-2"], service.SelectedUnitIds);
        Assert.Equal("vehicle-1", service.UnitSelectionAnchorId);
        Assert.Equal("vehicle-1", service.Current.Id);
    }
}
