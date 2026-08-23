using Avalonia.Controls;
using RobotCommand.Services.Team;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class MyTeamView : UserControl
{
    public MyTeamView() => InitializeComponent();

    private void OnDisconnectRemote(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { Tag: RobotCommandObserverRecord record } && DataContext is MyTeamViewModel viewModel)
        {
            viewModel.SelectRemoteObserver(record);
            viewModel.DisconnectRemoteCommand.Execute(null);
        }
    }

    private void OnUseNearbyServer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { Tag: DiscoveredRobotCommandServer server } && DataContext is MyTeamViewModel viewModel)
            viewModel.UseNearbyServer(server);
    }
}
