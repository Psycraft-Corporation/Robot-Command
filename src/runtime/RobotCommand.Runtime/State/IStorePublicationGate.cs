namespace RobotCommand.State;

/// <summary>
/// Serializes background projections that replace observable entity stores.
/// </summary>
public interface IStorePublicationGate : IDisposable
{
    IDisposable Enter(CancellationToken cancellationToken = default);

    Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default);
}

public sealed class StorePublicationGate : IStorePublicationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public IDisposable Enter(CancellationToken cancellationToken = default)
    {
        _gate.Wait(cancellationToken);
        return new Releaser(_gate);
    }

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
