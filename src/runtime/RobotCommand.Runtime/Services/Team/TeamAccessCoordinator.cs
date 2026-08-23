using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using RobotCommand.Models;

namespace RobotCommand.Services.Team;

public sealed record TeamAccessRegistration(
    TeamAccessRequestRecord Request,
    Task<TeamAccessDecision> Decision);

public sealed record TeamAccessDecision(
    TeamAccessRequestState State,
    string Message,
    DateTimeOffset ExpiresAt,
    string? SessionToken = null);

public sealed record TeamSessionLease(
    string SessionId,
    string Token,
    ChannelReader<TeamDisconnectNotice> DisconnectNotifications,
    CancellationToken CancellationToken);

public interface ITeamAccessCoordinator
{
    IReadOnlyList<TeamAccessRequestRecord> PendingRequests { get; }
    IReadOnlyList<TeamConnectedClientRecord> ConnectedClients { get; }
    event EventHandler? Changed;

    TeamAccessRegistration RequestAccess(
        string displayName,
        string applicationName,
        string applicationVersion,
        string sdkVersion,
        string apiVersion,
        string clientInstanceId,
        string requestNonce,
        string pairingId,
        string pairingCode,
        string pairingPassphrase,
        string remoteAddress);

    bool Approve(string requestId);
    bool Reject(string requestId, string message = "The Robot Command operator rejected this request.");
    TeamSessionLease BeginObserverSession(string token, string remoteAddress);
    bool ValidateConnectedToken(string token, string remoteAddress);
    void Touch(string token);
    void EndObserverSession(string token);
    bool Disconnect(string sessionId);
    bool Disconnect(string sessionId, string message);
    int DisconnectAll(TeamDisconnectReason reason, string message);
    void Clear();
}

public sealed class TeamAccessCoordinator : ITeamAccessCoordinator, IHostedService, IDisposable
{
    private sealed class PendingEntry(
        TeamAccessRequestRecord record,
        TaskCompletionSource<TeamAccessDecision> completion)
    {
        public TeamAccessRequestRecord Record { get; set; } = record;
        public TaskCompletionSource<TeamAccessDecision> Completion { get; } = completion;
    }

    private sealed class ApprovedEntry(
        string token,
        TeamAccessRequestRecord request,
        DateTimeOffset expiresAt)
    {
        public string Token { get; } = token;
        public TeamAccessRequestRecord Request { get; } = request;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }

    private sealed class ConnectedEntry(
        string token,
        TeamConnectedClientRecord record,
        CancellationTokenSource cancellation,
        Channel<TeamDisconnectNotice> notifications)
    {
        public string Token { get; } = token;
        public TeamConnectedClientRecord Record { get; set; } = record;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Channel<TeamDisconnectNotice> Notifications { get; } = notifications;
    }

    private readonly object _gate = new();
    private readonly ITeamServerSettingsService _settings;
    private readonly ITeamPairingService _pairing;
    private readonly ITeamPassphraseService _passphrase;
    private readonly Dictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApprovedEntry> _approved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectedEntry> _connected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _requestTimes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cleanupCancellation;
    private Task? _cleanupTask;
    private int _disposed;

    public TeamAccessCoordinator(ITeamServerSettingsService settings, ITeamPairingService pairing, ITeamPassphraseService? passphrase = null)
    {
        _settings = settings;
        _pairing = pairing;
        _passphrase = passphrase ?? new TeamPassphraseService();
    }

    public IReadOnlyList<TeamAccessRequestRecord> PendingRequests
    {
        get
        {
            lock (_gate)
                return _pending.Values.Select(item => item.Record)
                    .Where(item => item.State == TeamAccessRequestState.Pending)
                    .OrderBy(item => item.RequestedAt).ToArray();
        }
    }

    public IReadOnlyList<TeamConnectedClientRecord> ConnectedClients
    {
        get
        {
            lock (_gate)
                return _connected.Values.Select(item => item.Record)
                    .OrderBy(item => item.ConnectedAt).ToArray();
        }
    }

    public event EventHandler? Changed;

    // Retained for in-process callers compiled against the V1.2 coordinator contract.
    public TeamAccessRegistration RequestAccess(
        string displayName,
        string applicationName,
        string applicationVersion,
        string sdkVersion,
        string apiVersion,
        string clientInstanceId,
        string requestNonce,
        string pairingId,
        string pairingCode,
        string remoteAddress)
        => RequestAccess(displayName, applicationName, applicationVersion, sdkVersion, apiVersion, clientInstanceId,
            requestNonce, pairingId, pairingCode, string.Empty, remoteAddress);

    public TeamAccessRegistration RequestAccess(
        string displayName,
        string applicationName,
        string applicationVersion,
        string sdkVersion,
        string apiVersion,
        string clientInstanceId,
        string requestNonce,
        string pairingId,
        string pairingCode,
        string pairingPassphrase,
        string remoteAddress)
    {
        var now = DateTimeOffset.UtcNow;
        PendingEntry entry;
        lock (_gate)
        {
            var pairingRequested = !string.IsNullOrWhiteSpace(pairingId) || !string.IsNullOrWhiteSpace(pairingCode);
            var duplicate = _pending.Values.FirstOrDefault(item =>
                item.Record.State == TeamAccessRequestState.Pending &&
                string.Equals(item.Record.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Record.ClientInstanceId, clientInstanceId, StringComparison.Ordinal) &&
                string.Equals(item.Record.RequestNonce, requestNonce, StringComparison.Ordinal));
            if (duplicate is not null) return new TeamAccessRegistration(duplicate.Record, duplicate.Completion.Task);

            if (!AllowRequest(remoteAddress, now))
            {
                var denied = new TeamAccessDecision(
                    TeamAccessRequestState.Rejected,
                    "Too many access requests were received from this address. Try again later.",
                    now);
                return new TeamAccessRegistration(
                    CreateRecord(TeamAccessRequestState.Rejected, denied.Message, now),
                    Task.FromResult(denied));
            }

            var compatible = string.Equals(apiVersion, "v1", StringComparison.OrdinalIgnoreCase);
            var openAccess = _passphrase.IsOpenAccess;
            var passphraseRequired = !openAccess && _passphrase.IsConfigured;
            var passphraseVerified = passphraseRequired && _passphrase.Verify(pairingPassphrase);
            if (passphraseRequired && !passphraseVerified)
            {
                var denied = new TeamAccessDecision(
                    TeamAccessRequestState.Rejected,
                    "The Team server passphrase is incorrect. Check the host Robot Command and try again.",
                    now);
                return new TeamAccessRegistration(
                    CreateRecord(TeamAccessRequestState.Rejected, denied.Message, now, false, false),
                    Task.FromResult(denied));
            }
            var pairingVerified = openAccess || !pairingRequested || _pairing.IsValid(pairingId, pairingCode);
            // A V1.3 QR code carries the session passphrase as well as the legacy short code.
            // The session phrase is the human authentication factor, so an expired QR code
            // must not prevent an otherwise valid phrase from reaching host approval.
            if (pairingRequested && !pairingVerified && !passphraseVerified)
            {
                var denied = new TeamAccessDecision(
                    TeamAccessRequestState.Rejected,
                    "The QR pairing code is invalid or expired. Create a new pairing QR code on the host Robot Command.",
                    now);
                return new TeamAccessRegistration(
                    CreateRecord(TeamAccessRequestState.Rejected, denied.Message, now, false, passphraseVerified),
                    Task.FromResult(denied));
            }
            var record = new TeamAccessRequestRecord(
                $"request-{Guid.NewGuid():N}",
                Clean(displayName, "Observer"),
                Clean(applicationName, "Unknown application"),
                Clean(applicationVersion, "Unknown"),
                Clean(sdkVersion, "Unknown"),
                Clean(apiVersion, "Unknown"),
                Clean(clientInstanceId, "Unknown"),
                Clean(requestNonce, "Unknown"),
                pairingRequested ? Clean(pairingId, "") : string.Empty,
                pairingRequested ? Clean(pairingCode, "") : string.Empty,
                pairingVerified,
                passphraseVerified,
                remoteAddress,
                now,
                now.AddMinutes(2),
                compatible,
                compatible ? TeamAccessRequestState.Pending : TeamAccessRequestState.Incompatible,
                    openAccess
                        ? "Open Team server: access accepted. Starting read-only mirroring."
                        : compatible
                    ? passphraseVerified
                        ? "Session passphrase verified. Approve this observer to start read-only mirroring."
                        : pairingRequested
                            ? $"Pairing code {pairingCode} verified. Approve this observer."
                            : "Waiting for approval in Robot Command."
                    : "This client does not support Robot Command Team API v1.");
            var completion = new TaskCompletionSource<TeamAccessDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry = new PendingEntry(record, completion);
            if (compatible) _pending[record.Id] = entry;
            else completion.TrySetResult(new TeamAccessDecision(record.State, record.Message, record.ExpiresAt));

            TeamAccessRequestRecord CreateRecord(TeamAccessRequestState state, string message, DateTimeOffset requestedAt, bool pairingVerified = false, bool passphraseVerified = false)
                => new(
                    $"request-{Guid.NewGuid():N}", Clean(displayName, "Observer"), Clean(applicationName, "Unknown application"),
                    Clean(applicationVersion, "Unknown"), Clean(sdkVersion, "Unknown"), Clean(apiVersion, "Unknown"),
                    Clean(clientInstanceId, "Unknown"), Clean(requestNonce, "Unknown"),
                    pairingRequested ? Clean(pairingId, "") : string.Empty,
                    pairingRequested ? Clean(pairingCode, "") : string.Empty,
                    pairingVerified,
                    passphraseVerified,
                    remoteAddress,
                    requestedAt, requestedAt, false, state, message);
        }

        if (_passphrase.IsOpenAccess && entry.Record.Compatible)
            Approve(entry.Record.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        return new TeamAccessRegistration(entry.Record, entry.Completion.Task);
    }

    public bool Approve(string requestId)
    {
        TeamAccessDecision? decision = null;
        PendingEntry? entry = null;
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out entry) || entry.Record.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            if (!_passphrase.IsOpenAccess && !entry.Record.PassphraseVerified && !string.IsNullOrWhiteSpace(entry.Record.PairingId) &&
                !_pairing.Consume(entry.Record.PairingId, entry.Record.PairingShortCode))
            {
                decision = new TeamAccessDecision(
                    TeamAccessRequestState.Rejected,
                    "The QR pairing invitation expired before it was confirmed. Create a new pairing QR code and retry.",
                    DateTimeOffset.UtcNow);
            }
            else if (_connected.Count + _approved.Count >= _settings.Current.MaximumClients)
            {
                decision = new TeamAccessDecision(
                    TeamAccessRequestState.Rejected,
                    "The Robot Command observer client limit has been reached.",
                    DateTimeOffset.UtcNow);
            }
            else
            {
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
                _approved[token] = new ApprovedEntry(token, entry.Record, expiresAt);
                decision = new TeamAccessDecision(
                    TeamAccessRequestState.Approved,
                    "Access approved. Open the observer stream within one minute.",
                    expiresAt,
                    token);
            }
        }

        entry.Completion.TrySetResult(decision);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Reject(string requestId, string message = "The Robot Command operator rejected this request.")
    {
        PendingEntry? entry;
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out entry)) return false;
        }
        entry.Completion.TrySetResult(new TeamAccessDecision(
            TeamAccessRequestState.Rejected, message, DateTimeOffset.UtcNow));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public TeamSessionLease BeginObserverSession(string token, string remoteAddress)
    {
        TeamSessionLease lease;
        lock (_gate)
        {
            if (!_approved.Remove(token, out var approved) || approved.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("The observer token is invalid or expired.");
            if (!string.Equals(approved.Request.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The observer token was issued to a different address.");
            if (_connected.Count >= _settings.Current.MaximumClients)
                throw new InvalidOperationException("The observer client limit has been reached.");

            var sessionId = $"session-{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            var record = new TeamConnectedClientRecord(
                sessionId,
                approved.Request.Id,
                approved.Request.DisplayName,
                approved.Request.ApplicationName,
                approved.Request.ApplicationVersion,
                remoteAddress,
                now,
                now);
            var notifications = Channel.CreateBounded<TeamDisconnectNotice>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            var cancellation = new CancellationTokenSource();
            _connected[token] = new ConnectedEntry(token, record, cancellation, notifications);
            lease = new TeamSessionLease(sessionId, token, notifications.Reader, cancellation.Token);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return lease;
    }

    public bool ValidateConnectedToken(string token, string remoteAddress)
    {
        lock (_gate)
            return _connected.TryGetValue(token, out var entry) &&
                   string.Equals(entry.Record.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase);
    }

    public void Touch(string token)
    {
        lock (_gate)
        {
            if (_connected.TryGetValue(token, out var entry))
                entry.Record = entry.Record with { LastActivityAt = DateTimeOffset.UtcNow };
        }
    }

    public void EndObserverSession(string token)
    {
        ConnectedEntry? entry;
        lock (_gate) _connected.Remove(token, out entry);
        entry?.Notifications.Writer.TryComplete();
        entry?.Cancellation.Cancel();
        entry?.Cancellation.Dispose();
        if (entry is not null) Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Disconnect(string sessionId)
        => Disconnect(sessionId, TeamDisconnectReason.OperatorDisconnected,
            "The Robot Command operator disconnected this observer.");

    public bool Disconnect(string sessionId, string message)
        => Disconnect(sessionId, TeamDisconnectReason.OperatorDisconnected,
            string.IsNullOrWhiteSpace(message) ? "The Robot Command operator disconnected this observer." : message);

    public int DisconnectAll(TeamDisconnectReason reason, string message)
    {
        ConnectedEntry[] entries;
        lock (_gate)
        {
            entries = _connected.Values.ToArray();
            _connected.Clear();
        }

        foreach (var entry in entries)
            NotifyDisconnect(entry, reason, message);
        if (entries.Length > 0) Changed?.Invoke(this, EventArgs.Empty);
        return entries.Length;
    }

    private bool Disconnect(string sessionId, TeamDisconnectReason reason, string message)
    {
        ConnectedEntry? entry;
        lock (_gate)
        {
            var pair = _connected.FirstOrDefault(item => item.Value.Record.SessionId == sessionId);
            if (pair.Value is null) return false;
            _connected.Remove(pair.Key, out entry);
        }
        NotifyDisconnect(entry!, reason, message);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static void NotifyDisconnect(ConnectedEntry entry, TeamDisconnectReason reason, string message)
    {
        entry.Notifications.Writer.TryWrite(new TeamDisconnectNotice(reason, message));
        entry.Notifications.Writer.TryComplete();
        entry.Cancellation.Cancel();
        entry.Cancellation.Dispose();
    }

    public void Clear()
    {
        PendingEntry[] pending;
        ConnectedEntry[] connected;
        lock (_gate)
        {
            pending = _pending.Values.ToArray();
            connected = _connected.Values.ToArray();
            _pending.Clear();
            _approved.Clear();
            _connected.Clear();
        }
        foreach (var entry in pending)
            entry.Completion.TrySetResult(new TeamAccessDecision(
                TeamAccessRequestState.Expired, "The Robot Command team server stopped.", DateTimeOffset.UtcNow));
        foreach (var entry in connected)
            NotifyDisconnect(entry, TeamDisconnectReason.ServerStopped, "The Robot Command team server was stopped.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cleanupTask = CleanupLoopAsync(_cleanupCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupCancellation?.Cancel();
        if (_cleanupTask is not null)
        {
            try { await _cleanupTask.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        }
        Clear();
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            List<PendingEntry> expiredPending = [];
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in _pending.Where(item => item.Value.Record.ExpiresAt <= now).ToArray())
                {
                    _pending.Remove(pair.Key);
                    expiredPending.Add(pair.Value);
                }
                foreach (var token in _approved.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
                    _approved.Remove(token);
            }
            foreach (var entry in expiredPending)
                entry.Completion.TrySetResult(new TeamAccessDecision(
                    TeamAccessRequestState.Expired, "The access request expired.", DateTimeOffset.UtcNow));
            if (expiredPending.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool AllowRequest(string address, DateTimeOffset now)
    {
        if (!_requestTimes.TryGetValue(address, out var times)) _requestTimes[address] = times = new Queue<DateTimeOffset>();
        while (times.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(2)) times.Dequeue();
        if (times.Count >= 6) return false;
        times.Enqueue(now);
        return true;
    }

    private static string Clean(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, 160)];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cleanupCancellation?.Cancel();
        _cleanupCancellation?.Dispose();
        Clear();
    }
}
