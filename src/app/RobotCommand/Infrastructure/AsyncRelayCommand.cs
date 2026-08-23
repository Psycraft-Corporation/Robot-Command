using System.Windows.Input;

namespace RobotCommand.Infrastructure;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public event EventHandler<Exception>? ExecutionFailed;

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected command outcome and should never
            // escape an async-void ICommand invocation.
        }
        catch (Exception exception)
        {
            // ICommand.Execute is necessarily async void. Contain failures at
            // this boundary so a transport timeout cannot terminate Avalonia;
            // interested view models can surface the structured failure.
            try
            {
                ExecutionFailed?.Invoke(this, exception);
            }
            catch
            {
                // Error observers must not be able to turn a contained command
                // failure back into an unhandled UI exception.
            }
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
