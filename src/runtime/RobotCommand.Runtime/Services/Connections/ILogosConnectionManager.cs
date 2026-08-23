using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public interface ILogosConnectionManager : IAsyncDisposable
{
    IReadOnlyList<ConnectionDefinition> Definitions { get; }

    bool TryGetDefinition(string connectionId, out ConnectionDefinition? definition);

    Task RegisterAsync(ConnectionDefinition definition, CancellationToken cancellationToken = default);

    Task UpdateAsync(ConnectionDefinition definition, CancellationToken cancellationToken = default);

    Task RemoveAsync(string connectionId, CancellationToken cancellationToken = default);

    Task ConnectAsync(
        string connectionId,
        ConnectionCredentials credentials,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default);

    Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default);

    Task<CameraStreamRecord> OpenCameraStreamAsync(
        string connectionId,
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default);

    Task CloseCameraStreamAsync(
        string connectionId,
        string streamId,
        CancellationToken cancellationToken = default);

    Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        string connectionId,
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default);

    Task ConnectAllAsync(CancellationToken cancellationToken = default);

    Task StartAutoConnectionsAsync(CancellationToken cancellationToken = default);

    Task SuperviseAsync(CancellationToken cancellationToken = default);
}
