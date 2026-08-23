namespace RobotCommand.Services.Serial;

public sealed class SerialPortLeaseManager : ISerialPortLeaseManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<ISerialPortLease> AcquireAsync(
        string portName,
        string owner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(portName)) throw new ArgumentException("Serial port is required.", nameof(portName));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Lease owner is required.", nameof(owner));
        var normalized = portName.Trim().ToUpperInvariant();
        lock (_gate)
        {
            if (_owners.TryGetValue(normalized, out var existing))
            {
                throw new InvalidOperationException($"{normalized} is already in use by {existing}.");
            }
            _owners[normalized] = owner;
        }
        return ValueTask.FromResult<ISerialPortLease>(new Lease(this, normalized, owner));
    }

    public bool IsLeased(string portName, out string? owner)
    {
        lock (_gate) return _owners.TryGetValue(portName.Trim().ToUpperInvariant(), out owner);
    }

    private void Release(string portName, string owner)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue(portName, out var current) && current == owner) _owners.Remove(portName);
        }
    }

    private sealed class Lease(SerialPortLeaseManager manager, string portName, string owner) : ISerialPortLease
    {
        private int _released;
        public string PortName { get; } = portName;
        public string Owner { get; } = owner;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) manager.Release(PortName, Owner);
            return ValueTask.CompletedTask;
        }
    }
}
