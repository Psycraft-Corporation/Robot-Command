using RobotCommand.Infrastructure;
using Xunit;

namespace RobotCommand.Tests.Infrastructure;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteContainsFailureAndRaisesFailureNotification()
    {
        var command = new AsyncRelayCommand(_ => Task.FromException(new TimeoutException("transport timeout")));
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        command.ExecutionFailed += (_, exception) => failure.TrySetResult(exception);

        command.Execute(null);

        var exception = await failure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsType<TimeoutException>(exception);
        Assert.True(command.CanExecute(null));
    }
}
