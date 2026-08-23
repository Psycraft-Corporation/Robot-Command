using Avalonia.Controls;
using RobotCommand.Core;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class OperateView : UserControl
{
    public OperateView()
    {
        InitializeComponent();
        WorldViewport.CameraChanged += OnWorldCameraChanged;
        WorldViewport.FitRequested += OnWorldFitRequested;
    }

    private void OnWorldCameraChanged(object? sender, ThreeDCameraSnapshot camera)
    {
        if (DataContext is OperateViewModel viewModel && viewModel.Map.IsThreeD)
            _ = viewModel.Map.SetWorldCameraAsync(camera);
    }

    private void OnWorldFitRequested(object? sender, EventArgs e)
    {
        if (DataContext is OperateViewModel viewModel && viewModel.Map.IsThreeD)
            _ = viewModel.Map.FitWorldSceneAsync();
    }
}
