using RobotCommand.Models;

namespace RobotCommand.Services.Team;

public sealed record TeamServerStartRequest(
    string DisplayName,
    int Port = 7443,
    int MaximumClients = 8,
    bool OpenAccess = false,
    string? Passphrase = null);

public sealed record TeamServerWorkflowStatus(
    bool IsRunning,
    string Status,
    string Endpoint,
    string CertificateFingerprint,
    string? Error,
    TeamServerSettings Settings,
    bool AuthenticationEnabled);

public interface ITeamServerWorkflow
{
    TeamServerWorkflowStatus Status { get; }
    IReadOnlyList<TeamAccessRequestRecord> PendingRequests { get; }
    IReadOnlyList<TeamConnectedClientRecord> ConnectedClients { get; }
    event EventHandler? Changed;

    Task<TeamServerWorkflowStatus> StartAsync(TeamServerStartRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SetAuthenticationAsync(bool enabled, bool requireReauthentication, string? passphrase = null, CancellationToken cancellationToken = default);
    bool Approve(string requestId);
    bool Reject(string requestId, string? message = null);
    bool Disconnect(string sessionId, string? message = null);
}
