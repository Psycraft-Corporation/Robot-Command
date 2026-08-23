using System.Net;
using System.Net.Sockets;

namespace RobotCommand.Services.Mavlink;

public sealed class UdpMavlinkTransport : IMavlinkTransport
{
    private readonly IPEndPoint _listenEndPoint;
    private readonly object _gate = new();
    // PX4 may coalesce several MAVLink frames into one UDP datagram. Keep the
    // transport contract frame-oriented just as the serial transport does.
    private readonly MavlinkStreamFramer _framer = new();
    private UdpClient? _client;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private long _bytesReceived;
    private long _bytesSent;
    private long _chunksReceived;
    private DateTimeOffset? _lastByteAt;

    public UdpMavlinkTransport(IPEndPoint listenEndPoint) => _listenEndPoint = listenEndPoint;

    public event EventHandler<MavlinkTransportChunk>? ChunkReceived;
    public event EventHandler<MavlinkTransportFault>? Faulted;

    public bool KeepOpenWithoutHeartbeat => false;
    public string TransportName => "UDP";

    public bool IsOpen
    {
        get { lock (_gate) return _client is not null; }
    }

    public string? LocalEndpoint
    {
        get { lock (_gate) return _client?.Client.LocalEndPoint?.ToString(); }
    }

    public MavlinkTransportStatistics Statistics => new(
        Interlocked.Read(ref _bytesReceived),
        Interlocked.Read(ref _bytesSent),
        Interlocked.Read(ref _chunksReceived),
        LastByteAt: _lastByteAt,
        Detail: LocalEndpoint is null ? "UDP listener closed" : $"Listening on {LocalEndpoint}");

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_client is not null) return Task.CompletedTask;
            _client = new UdpClient(_listenEndPoint);
            _receiveCancellation = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_client, _receiveCancellation.Token);
        }
        return Task.CompletedTask;
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> payload,
        MavlinkTransportRoute route,
        CancellationToken cancellationToken = default)
    {
        UdpClient client;
        lock (_gate) client = _client ?? throw new InvalidOperationException("The MAVLink UDP transport is not open.");
        if (route.NativeRoute is not IPEndPoint endpoint)
        {
            throw new InvalidOperationException("The MAVLink UDP route has no IP endpoint.");
        }
        await client.SendAsync(payload, endpoint, cancellationToken);
        Interlocked.Add(ref _bytesSent, payload.Length);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        UdpClient? client;
        CancellationTokenSource? receiveCancellation;
        Task? receiveTask;
        lock (_gate)
        {
            client = _client;
            receiveCancellation = _receiveCancellation;
            receiveTask = _receiveTask;
            _client = null;
            _receiveCancellation = null;
            _receiveTask = null;
        }
        if (client is null) return;
        receiveCancellation?.Cancel();
        client.Dispose();
        if (receiveTask is not null)
        {
            try { await receiveTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (receiveCancellation?.IsCancellationRequested == true) { }
            catch (ObjectDisposedException) { }
        }
        receiveCancellation?.Dispose();
        _framer.Reset();
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(cancellationToken);
                var now = DateTimeOffset.UtcNow;
                Interlocked.Add(ref _bytesReceived, received.Buffer.Length);
                _lastByteAt = now;
                var route = new MavlinkTransportRoute(received.RemoteEndPoint.ToString(), received.RemoteEndPoint);
                foreach (var frame in _framer.Push(received.Buffer))
                {
                    Interlocked.Increment(ref _chunksReceived);
                    ChunkReceived?.Invoke(this, new MavlinkTransportChunk(frame, route, now));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, new MavlinkTransportFault($"UDP receive failed: {ex.Message}", ex));
        }
    }
}
