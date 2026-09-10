using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MissionPreviewLayerItemViewModelTests
{
    [Fact]
    public void Visibility_IsIndependentPerMissionAndNotifiesOwner()
    {
        var changes = new List<bool>();
        var first = new MissionPreviewLayerItemViewModel("mission-one", "One", true, changes.Add);
        var second = new MissionPreviewLayerItemViewModel("mission-two", "Two", true, _ => throw new InvalidOperationException("wrong layer changed"));

        first.IsVisible = false;

        Assert.False(first.IsVisible);
        Assert.True(second.IsVisible);
        Assert.Equal([false], changes);
    }

    [Fact]
    public void NewLayer_IsVisibleAndCanBeToggled()
    {
        var changes = new List<bool>();
        var layer = new MissionPreviewLayerItemViewModel("mission", "Mission", true, changes.Add);

        layer.ToggleCommand.Execute(null);

        Assert.False(layer.IsVisible);
        Assert.Equal([false], changes);
    }
}
