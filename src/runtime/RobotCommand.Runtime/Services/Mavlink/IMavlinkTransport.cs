namespace RobotCommand.Services.Mavlink;

public sealed record MavlinkTransportRoute(string DisplayName, object? NativeRoute = null)
{
    public static MavlinkTransportRoute Serial { get; } = new("serial");
}

public sealed class MavlinkTransportChunk(
    ReadOnlyMemory<byte> payload,
    MavlinkTransportRoute route,
    DateTimeOffset receivedAt) : EventArgs
{
    public ReadOnlyMemory<byte> Payload { get; } = payload;
    public MavlinkTransportRoute Route { get; } = route;
    public DateTimeOffset ReceivedAt { get; } = receivedAt;
}

public sealed class MavlinkTransportFault(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public sealed record MavlinkTransportStatistics(
    long BytesReceived = 0,
    long BytesSent = 0,
    long ChunksReceived = 0,
    long FramesReceived = 0,
    long FramingErrors = 0,
    DateTimeOffset? LastByteAt = null,
    string? Detail = null);

public interface IMavlinkTransport : IAsyncDisposable
{
    event EventHandler<MavlinkTransportChunk>? ChunkReceived;
    event EventHandler<MavlinkTransportFault>? Faulted;

    bool IsOpen { get; }
    bool KeepOpenWithoutHeartbeat { get; }
    string TransportName { get; }
    string? LocalEndpoint { get; }
    MavlinkTransportStatistics Statistics { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task SendAsync(
        ReadOnlyMemory<byte> payload,
        MavlinkTransportRoute route,
        CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}
