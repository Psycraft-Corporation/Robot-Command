namespace RobotCommand.Infrastructure;

/// <summary>
/// Coalesces a burst of store notifications into one logical change.  The
/// state remains readable immediately; only the notification is delayed so
/// consumers do not rebuild repeatedly while a publication batch is landing.
/// </summary>
internal sealed class CoalescedChangeNotifier : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private int _pending;
    private int _disposed;

    public CoalescedChangeNotifier(TimeSpan? delay = null, IUiDispatcher? dispatcher = null)
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(5);
        _dispatcher = dispatcher ?? new InlineUiDispatcher();
    }

    public event EventHandler? Changed;

    public void Request()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _pending, 1) != 0)
            return;

        _ = DispatchAsync();
    }

    private async Task DispatchAsync()
    {
        try
        {
            await Task.Delay(_delay, _shutdown.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _pending, 0);
            if (Volatile.Read(ref _disposed) == 0)
            {
                await _dispatcher.InvokeAsync(
                    () => Changed?.Invoke(this, EventArgs.Empty),
                    _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _pending, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
