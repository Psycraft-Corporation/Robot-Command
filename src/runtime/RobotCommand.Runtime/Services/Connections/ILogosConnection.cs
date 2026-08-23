using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public interface ILogosConnection : IAsyncDisposable
{
    event EventHandler? Changed;

    ConnectionDefinition Definition { get; }

    AvailabilityState State { get; }

    bool IsTransportOpen { get; }

    bool HasConnectBeenRequested { get; }

    bool HasActiveStreams { get; }

    DateTimeOffset? LastAttempt { get; }

    DateTimeOffset? NextReconnectAt => null;

    DateTimeOffset? ConnectedAt { get; }

    DateTimeOffset? LastConnectedAt { get; }

    DateTimeOffset? LastSeen { get; }

    DateTimeOffset? LastSnapshotRefresh { get; }

    string? LastError { get; }

    LogosConnectionObservation? LastObservation { get; }

    IReadOnlyList<LogosConnectionObservation> Observations
        => LastObservation is { } observation ? [observation] : [];

    LogosConnectionLiveSnapshot LiveSnapshot { get; }

    Task<LogosConnectionObservation> ConnectAsync(
        ConnectionCredentials credentials,
        bool reconnecting,
        CancellationToken cancellationToken = default);

    Task<LogosConnectionObservation> RefreshAsync(CancellationToken cancellationToken = default);

    Task<CameraStreamRecord> OpenCameraStreamAsync(
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default);

    Task CloseCameraStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default);

    Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    void EvaluateFreshness(DateTimeOffset now, TimeSpan staleAfter, TimeSpan offlineAfter);
}
