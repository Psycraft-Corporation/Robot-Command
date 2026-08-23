using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public partial class ThreeDWorkspaceView : UserControl
{
    public ThreeDWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
        Viewport.FitRequested += OnFitRequested;
    }

    private async void OnFitRequested(object? sender, EventArgs e)
    {
        if (DataContext is ThreeDWorkspaceViewModel viewModel)
            await viewModel.FitSceneAsync();
    }
}
