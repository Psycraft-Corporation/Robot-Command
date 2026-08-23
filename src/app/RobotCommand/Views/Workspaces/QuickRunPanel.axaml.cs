using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class QuickRunPanel : UserControl
{
    public QuickRunPanel()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    private OperatorControlsViewModel? _viewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.QueuedCommandsChanged -= OnQueuedCommandsChanged;
        }

        _viewModel = DataContext as OperatorControlsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.QueuedCommandsChanged += OnQueuedCommandsChanged;
        }
    }

    private void OnQueuedCommandsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ScrollToCommandReview, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(ScrollToCommandReview, DispatcherPriority.Render);
    }

    private void ScrollToCommandReview()
    {
        PendingCommandPanel?.BringIntoView();
        OperationsScrollViewer?.ScrollToEnd();
    }
}
