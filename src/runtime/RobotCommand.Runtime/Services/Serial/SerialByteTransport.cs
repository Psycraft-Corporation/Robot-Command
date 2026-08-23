using System.IO.Ports;

namespace RobotCommand.Services.Serial;

public sealed class SerialByteTransportFactory(ISerialPortLeaseManager leases) : ISerialByteTransportFactory
{
    public ISerialByteTransport Create() => new SerialByteTransport(leases);
}

public sealed class SerialByteTransport(ISerialPortLeaseManager leases) : ISerialByteTransport
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialPort? _serialPort;
    private ISerialPortLease? _lease;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private long _bytesReceived;
    private long _bytesSent;

    public event EventHandler<SerialBytesReceived>? BytesReceived;
    public event EventHandler<SerialTransportFault>? Faulted;
    public bool IsOpen { get { lock (_gate) return _serialPort?.IsOpen == true; } }
    public string? PortName { get { lock (_gate) return _serialPort?.PortName; } }
    public long BytesReceivedCount => Interlocked.Read(ref _bytesReceived);
    public long BytesSentCount => Interlocked.Read(ref _bytesSent);
    public DateTimeOffset? LastByteAt { get; private set; }

    public async Task OpenAsync(SerialPortSettings settings, string owner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_serialPort?.IsOpen == true) return;
        }

        var lease = await leases.AcquireAsync(settings.PortName, owner, cancellationToken);
        var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.Handshake,
            ReadTimeout = 500,
            WriteTimeout = 1000,
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable
        };
        try
        {
            port.Open();
            var readCancellation = new CancellationTokenSource();
            lock (_gate)
            {
                _serialPort = port;
                _lease = lease;
                _readCancellation = readCancellation;
                _readTask = ReadLoopAsync(port, readCancellation.Token);
            }
        }
        catch
        {
            port.Dispose();
            await lease.DisposeAsync();
            throw;
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        SerialPort port;
        lock (_gate) port = _serialPort is { IsOpen: true } current
            ? current
            : throw new InvalidOperationException("The serial transport is not open.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await port.BaseStream.WriteAsync(bytes, cancellationToken);
            await port.BaseStream.FlushAsync(cancellationToken);
            Interlocked.Add(ref _bytesSent, bytes.Length);
        }
        finally { _writeGate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        SerialPort? port;
        CancellationTokenSource? readCancellation;
        Task? readTask;
        ISerialPortLease? lease;
        lock (_gate)
        {
            port = _serialPort;
            readCancellation = _readCancellation;
            readTask = _readTask;
            lease = _lease;
            _serialPort = null;
            _readCancellation = null;
            _readTask = null;
            _lease = null;
        }
        if (port is null) return;
        readCancellation?.Cancel();
        try { port.Close(); } catch { }
        port.Dispose();
        if (readTask is not null)
        {
            try { await readTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (readCancellation?.IsCancellationRequested == true) { }
            catch (Exception) { }
        }
        readCancellation?.Dispose();
        if (lease is not null) await lease.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _writeGate.Dispose();
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await port.BaseStream.ReadAsync(buffer, cancellationToken);
                if (count == 0) continue;
                var copy = buffer.AsMemory(0, count).ToArray();
                var now = DateTimeOffset.UtcNow;
                LastByteAt = now;
                Interlocked.Add(ref _bytesReceived, count);
                BytesReceived?.Invoke(this, new SerialBytesReceived(copy, now));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, new SerialTransportFault($"Serial port {port.PortName} failed: {ex.Message}", ex));
        }
    }
}
