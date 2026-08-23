using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Shell;

public sealed partial class EmbeddedTerminalView : UserControl
{
    private EmbeddedTerminalViewModel? _terminal;

    public EmbeddedTerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_terminal is not null)
        {
            _terminal.PropertyChanged -= OnTerminalPropertyChanged;
        }

        _terminal = DataContext as EmbeddedTerminalViewModel;
        if (_terminal is null)
            return;

        _terminal.PropertyChanged += OnTerminalPropertyChanged;
        FocusInput();
    }

    private void OnTerminalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EmbeddedTerminalViewModel.OutputText) || OutputBox is null)
            return;

        OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not EmbeddedTerminalViewModel terminal)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                terminal.ExecuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                terminal.RecallPrevious();
                e.Handled = true;
                break;
            case Key.Down:
                terminal.RecallNext();
                e.Handled = true;
                break;
        }
    }

    public void FocusInput() => InputBox?.Focus();
}
