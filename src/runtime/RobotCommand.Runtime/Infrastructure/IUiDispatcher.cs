namespace RobotCommand.Infrastructure;

public interface IUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
