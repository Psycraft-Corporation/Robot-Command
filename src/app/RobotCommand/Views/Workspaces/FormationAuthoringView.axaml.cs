using Avalonia.Controls;
using RobotCommand.Core;
using RobotCommand.Rendering.Avalonia;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public partial class FormationAuthoringView : UserControl
{
    public FormationAuthoringView()
    {
        InitializeComponent();
        Viewport.CameraChanged += OnCameraChanged;
        Viewport.FitRequested += OnFitRequested;
    }

    private void OnCameraChanged(object? sender, ThreeDCameraSnapshot camera)
    {
        if (DataContext is FormationAuthoringViewModel viewModel) _ = viewModel.SetCameraAsync(camera);
    }

    private void OnFitRequested(object? sender, EventArgs e)
    {
        if (DataContext is FormationAuthoringViewModel viewModel) viewModel.ResetCamera();
    }
}
