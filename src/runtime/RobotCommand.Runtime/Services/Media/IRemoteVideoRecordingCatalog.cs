using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface IRemoteVideoRecordingCatalog : IAsyncDisposable, IDisposable
{
    event EventHandler? Changed;

    RemoteVideoRecordingStatus Status { get; }

    IReadOnlyList<RemoteVideoRecordingSpan> Spans { get; }

    Task BeginSessionAsync(
        CameraStreamRecord stream,
        ConnectionDefinition? connection,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    void ProtectPlayback(string? spanId);

    Task<RemoteVideoRecordingSpan> EnsureCachedAsync(
        string spanId,
        CancellationToken cancellationToken = default);

    Task<RemoteVideoRecordingSpan> RetainAsync(
        string spanId,
        CancellationToken cancellationToken = default);
}
