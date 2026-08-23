using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperationalSupervisionService : IAsyncDisposable
{
    event EventHandler? Changed;

    OperationalExecutionSnapshot Snapshot { get; }

    Task BeginAsync(
        OperationalExecutionTarget target,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class NullOperationalSupervisionService : IOperationalSupervisionService
{
    public static NullOperationalSupervisionService Instance { get; } = new();

    private NullOperationalSupervisionService()
    {
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public OperationalExecutionSnapshot Snapshot => OperationalExecutionSnapshot.Empty;

    public Task BeginAsync(
        OperationalExecutionTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
