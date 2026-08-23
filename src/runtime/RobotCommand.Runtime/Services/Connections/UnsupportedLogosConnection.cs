using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public class UnsupportedLogosConnection : IManagedConnection
{
    private readonly string _reason;

    public UnsupportedLogosConnection(ConnectionDefinition definition, string reason)
    {
        Definition = definition;
        _reason = reason;
    }

    public event EventHandler? Changed;

    public ConnectionDefinition Definition { get; }

    public AvailabilityState State { get; private set; } = AvailabilityState.Offline;

    public bool IsTransportOpen => false;

    public bool HasConnectBeenRequested { get; private set; }

    public bool HasActiveStreams => false;

    public DateTimeOffset? LastAttempt { get; private set; }

    public DateTimeOffset? ConnectedAt => null;

    public DateTimeOffset? LastConnectedAt => null;

    public DateTimeOffset? LastSeen => null;

    public DateTimeOffset? LastSnapshotRefresh => null;

    public string? LastError { get; private set; }

    public LogosConnectionObservation? LastObservation => null;

    public IReadOnlyList<LogosConnectionObservation> Observations => [];

    public LogosConnectionLiveSnapshot LiveSnapshot => LogosConnectionLiveSnapshot.Empty;

    public Task<LogosConnectionObservation> ConnectAsync(
        ConnectionCredentials credentials,
        bool reconnecting,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HasConnectBeenRequested = true;
        LastAttempt = DateTimeOffset.UtcNow;
        LastError = _reason;
        State = AvailabilityState.Faulted;
        Changed?.Invoke(this, EventArgs.Empty);
        throw new NotSupportedException(_reason);
    }

    public Task<LogosConnectionObservation> RefreshAsync(CancellationToken cancellationToken = default)
        => ConnectAsync(ConnectionCredentials.Empty, false, cancellationToken);


    public Task<CameraStreamRecord> OpenCameraStreamAsync(
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(_reason);
    }

    public Task CloseCameraStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(_reason);
    }

    public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OperatorPolicyEvaluation.Unavailable(_reason));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = AvailabilityState.Offline;
        HasConnectBeenRequested = false;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void EvaluateFreshness(DateTimeOffset now, TimeSpan staleAfter, TimeSpan offlineAfter)
    {
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
