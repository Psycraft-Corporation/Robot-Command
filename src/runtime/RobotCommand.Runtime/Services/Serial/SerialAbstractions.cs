using System.IO.Ports;

namespace RobotCommand.Services.Serial;

public sealed record SerialDeviceDescriptor(
    string DeviceId,
    string PortName,
    string DisplayName,
    string? Manufacturer = null,
    string? VendorId = null,
    string? ProductId = null,
    string? SerialNumber = null,
    Guid? ContainerId = null,
    bool IsLikelySikRadio = false,
    bool IsPresent = true)
{
    public string HardwareSummary => string.Join(" · ", new[]
    {
        PortName,
        Manufacturer,
        VendorId is null || ProductId is null ? null : $"VID {VendorId} / PID {ProductId}",
        SerialNumber
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record SerialPortSettings(
    string PortName,
    int BaudRate = 57600,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None,
    bool DtrEnable = false,
    bool RtsEnable = false);

public sealed class SerialBytesReceived(ReadOnlyMemory<byte> payload, DateTimeOffset receivedAt) : EventArgs
{
    public ReadOnlyMemory<byte> Payload { get; } = payload;
    public DateTimeOffset ReceivedAt { get; } = receivedAt;
}

public sealed class SerialTransportFault(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public interface ISerialDeviceDiscovery
{
    Task<IReadOnlyList<SerialDeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<SerialDeviceDescriptor?> ResolveAsync(
        string? stableDeviceId,
        string? lastKnownPort,
        CancellationToken cancellationToken = default);
}

public interface ISerialPortLease : IAsyncDisposable
{
    string PortName { get; }
    string Owner { get; }
}

public interface ISerialPortLeaseManager
{
    ValueTask<ISerialPortLease> AcquireAsync(
        string portName,
        string owner,
        CancellationToken cancellationToken = default);
    bool IsLeased(string portName, out string? owner);
}

public interface ISerialByteTransport : IAsyncDisposable
{
    event EventHandler<SerialBytesReceived>? BytesReceived;
    event EventHandler<SerialTransportFault>? Faulted;
    bool IsOpen { get; }
    string? PortName { get; }
    long BytesReceivedCount { get; }
    long BytesSentCount { get; }
    DateTimeOffset? LastByteAt { get; }
    Task OpenAsync(SerialPortSettings settings, string owner, CancellationToken cancellationToken = default);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

public interface ISerialByteTransportFactory
{
    ISerialByteTransport Create();
}
