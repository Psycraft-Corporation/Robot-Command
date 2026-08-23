using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class OperationalInspectorView : UserControl
{
    private bool _initialized;

    public OperationalInspectorView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_initialized || DataContext is not OperationalInspectorViewModel viewModel)
        {
            return;
        }

        _initialized = true;
        await viewModel.InitializeAsync();
    }
}
