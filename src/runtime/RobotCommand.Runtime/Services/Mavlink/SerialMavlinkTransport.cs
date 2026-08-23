using RobotCommand.Services.Serial;

namespace RobotCommand.Services.Mavlink;

public sealed class SerialMavlinkTransport : IMavlinkTransport
{
    private readonly ISerialDeviceDiscovery _discovery;
    private readonly ISerialByteTransportFactory _factory;
    private readonly string? _deviceId;
    private readonly string? _lastKnownPort;
    private readonly int _baudRate;
    private readonly string _owner;
    private readonly MavlinkStreamFramer _framer = new();
    private ISerialByteTransport? _serial;
    private long _chunks;
    private long _decodeErrors;

    public SerialMavlinkTransport(
        ISerialDeviceDiscovery discovery,
        ISerialByteTransportFactory factory,
        string? deviceId,
        string? lastKnownPort,
        int baudRate,
        string owner)
    {
        _discovery = discovery;
        _factory = factory;
        _deviceId = deviceId;
        _lastKnownPort = lastKnownPort;
        _baudRate = baudRate;
        _owner = owner;
    }

    public event EventHandler<MavlinkTransportChunk>? ChunkReceived;
    public event EventHandler<MavlinkTransportFault>? Faulted;
    public bool IsOpen => _serial?.IsOpen == true;
    public bool KeepOpenWithoutHeartbeat => true;
    public string TransportName => "Serial / SiK";
    public string? LocalEndpoint => _serial?.PortName;
    public MavlinkTransportStatistics Statistics => new(
        _serial?.BytesReceivedCount ?? 0,
        _serial?.BytesSentCount ?? 0,
        Interlocked.Read(ref _chunks),
        _framer.FramesProduced,
        _framer.FramingErrors + Interlocked.Read(ref _decodeErrors),
        _serial?.LastByteAt,
        IsOpen
            ? $"{LocalEndpoint} open at {_baudRate:N0} baud"
            : $"Serial device {(_deviceId ?? _lastKnownPort ?? "not selected")} closed");

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        var descriptor = await _discovery.ResolveAsync(_deviceId, _lastKnownPort, cancellationToken)
            ?? throw new IOException($"Serial device '{_deviceId ?? _lastKnownPort}' is not present.");
        var serial = _factory.Create();
        serial.BytesReceived += OnBytesReceived;
        serial.Faulted += OnSerialFaulted;
        try
        {
            await serial.OpenAsync(
                new SerialPortSettings(descriptor.PortName, _baudRate),
                _owner,
                cancellationToken);
            _framer.Reset();
            _serial = serial;
        }
        catch
        {
            serial.BytesReceived -= OnBytesReceived;
            serial.Faulted -= OnSerialFaulted;
            await serial.DisposeAsync();
            throw;
        }
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> payload,
        MavlinkTransportRoute route,
        CancellationToken cancellationToken = default)
        => (_serial ?? throw new InvalidOperationException("The MAVLink serial transport is not open."))
            .WriteAsync(payload, cancellationToken);

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var serial = Interlocked.Exchange(ref _serial, null);
        if (serial is null) return;
        serial.BytesReceived -= OnBytesReceived;
        serial.Faulted -= OnSerialFaulted;
        await serial.CloseAsync(cancellationToken);
        await serial.DisposeAsync();
        _framer.Reset();
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    public void RecordDecodeError() => Interlocked.Increment(ref _decodeErrors);

    private void OnBytesReceived(object? sender, SerialBytesReceived e)
    {
        Interlocked.Increment(ref _chunks);
        foreach (var frame in _framer.Push(e.Payload.Span))
        {
            ChunkReceived?.Invoke(this, new MavlinkTransportChunk(frame, MavlinkTransportRoute.Serial, e.ReceivedAt));
        }
    }

    private void OnSerialFaulted(object? sender, SerialTransportFault e)
        => Faulted?.Invoke(this, new MavlinkTransportFault(e.Message, e.Exception));
}
