namespace RobotCommand.Infrastructure;

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}
