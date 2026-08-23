using RobotCommand.Models;

namespace RobotCommand.Services.Team;

public sealed class TeamServerWorkflow : ITeamServerWorkflow
{
    private readonly ILanTeamServer _server;
    private readonly ITeamAccessCoordinator _access;
    private readonly ITeamPassphraseService _passphrase;
    private readonly ITeamServerSettingsService _settings;

    public TeamServerWorkflow(
        ILanTeamServer server,
        ITeamAccessCoordinator access,
        ITeamPassphraseService passphrase,
        ITeamServerSettingsService settings)
    {
        _server = server;
        _access = access;
        _passphrase = passphrase;
        _settings = settings;
        _server.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _access.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _passphrase.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public TeamServerWorkflowStatus Status => new(
        _server.IsRunning,
        _server.Status,
        _server.Endpoint,
        _server.CertificateFingerprint,
        _server.LastError,
        _settings.Current,
        _passphrase.IsConfigured && !_passphrase.IsOpenAccess);

    public IReadOnlyList<TeamAccessRequestRecord> PendingRequests => _access.PendingRequests;
    public IReadOnlyList<TeamConnectedClientRecord> ConnectedClients => _access.ConnectedClients;
    public event EventHandler? Changed;

    public async Task<TeamServerWorkflowStatus> StartAsync(TeamServerStartRequest request, CancellationToken cancellationToken = default)
    {
        var settings = new TeamServerSettings(request.DisplayName, request.Port, request.MaximumClients, !request.OpenAccess).Normalize();
        if (request.OpenAccess) _passphrase.ConfigureOpenAccess();
        else _passphrase.Configure(string.IsNullOrWhiteSpace(request.Passphrase)
            ? TeamPassphraseService.CreateSuggestedPassphrase()
            : request.Passphrase);
        await _settings.SaveAsync(settings, cancellationToken);
        await _server.StartAsync(cancellationToken);
        return Status;
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => _server.StopAsync(cancellationToken);

    public async Task SetAuthenticationAsync(bool enabled, bool requireReauthentication, string? passphrase = null, CancellationToken cancellationToken = default)
    {
        if (enabled)
        {
            _passphrase.Configure(string.IsNullOrWhiteSpace(passphrase)
                ? TeamPassphraseService.CreateSuggestedPassphrase()
                : passphrase);
            await _settings.SaveAsync(_settings.Current with { RequirePassphrase = true }, cancellationToken);
            if (requireReauthentication)
                _access.DisconnectAll(TeamDisconnectReason.AuthenticationRequired,
                    "The Robot Command operator enabled authentication and requires all observers to authenticate again.");
        }
        else
        {
            _passphrase.ConfigureOpenAccess();
            await _settings.SaveAsync(_settings.Current with { RequirePassphrase = false }, cancellationToken);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Approve(string requestId) => _access.Approve(requestId);
    public bool Reject(string requestId, string? message = null) => _access.Reject(requestId, message ?? "The Robot Command operator rejected this request.");
    public bool Disconnect(string sessionId, string? message = null)
        => string.IsNullOrWhiteSpace(message) ? _access.Disconnect(sessionId) : _access.Disconnect(sessionId, message);
}
