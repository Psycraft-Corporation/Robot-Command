using RobotCommand.Models;
using RobotCommand.Services.Connections;

namespace RobotCommand.Services.Media;

/// <summary>Adapts an RTSP media endpoint to the shared managed-connection lifecycle.</summary>
public sealed class RtspConnectionProvider(IEnumerable<ICameraMediaSourceProvider> providers) : IConnectionProvider
{
    private readonly IReadOnlyDictionary<string, ICameraMediaSourceProvider> _providers = providers.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
    public bool Supports(ConnectionMode mode) => mode == ConnectionMode.Media;

    public IManagedConnection Create(ConnectionDefinition definition) => new RtspManagedConnection(definition, ProviderFor(definition));

    private ICameraMediaSourceProvider ProviderFor(ConnectionDefinition definition)
    {
        if (!_providers.TryGetValue("rtsp", out var provider)) throw new InvalidOperationException("The RTSP media provider is unavailable.");
        provider.Validate(definition.Target);
        return provider;
    }

    private sealed class RtspManagedConnection(ConnectionDefinition definition, ICameraMediaSourceProvider provider) : IManagedConnection
    {
        private AvailabilityState _state = AvailabilityState.Offline;
        private DateTimeOffset? _lastAttempt;
        private DateTimeOffset? _connectedAt;
        private DateTimeOffset? _lastConnectedAt;
        private DateTimeOffset? _lastSeen;
        private string? _lastError;
        private CameraStreamRecord? _stream;

        public event EventHandler? Changed;
        public ConnectionDefinition Definition { get; } = definition;
        public AvailabilityState State => _state;
        public bool IsTransportOpen => _state == AvailabilityState.Online;
        public bool HasConnectBeenRequested { get; private set; }
        public bool HasActiveStreams => _stream is not null;
        public DateTimeOffset? LastAttempt => _lastAttempt;
        public DateTimeOffset? ConnectedAt => _connectedAt;
        public DateTimeOffset? LastConnectedAt => _lastConnectedAt;
        public DateTimeOffset? LastSeen => _lastSeen;
        public DateTimeOffset? LastSnapshotRefresh => _lastSeen;
        public string? LastError => _lastError;
        public LogosConnectionObservation? LastObservation => null;
        public IReadOnlyList<LogosConnectionObservation> Observations => [];
        private string SourceId => Definition.Id.StartsWith("media:", StringComparison.Ordinal) ? Definition.Id[6..] : Definition.Id;
        public LogosConnectionLiveSnapshot LiveSnapshot => new([], [], [], [], [], [],
            [provider.CreateSource(SourceId, Definition.Name, Definition.Target, _state == AvailabilityState.Online,
                _lastError ?? "RTSP endpoint not probed")], _stream is null ? [] : [_stream], []);

        public async Task<LogosConnectionObservation> ConnectAsync(ConnectionCredentials credentials, bool reconnecting, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HasConnectBeenRequested = true;
            _lastAttempt = DateTimeOffset.UtcNow;
            var result = await provider.ProbeAsync(Definition.Target, cancellationToken);
            SetProbe(result);
            return new LogosConnectionObservation(
                new RuntimeObservation("media", Definition.Name, "media", "stream", "camera", "rtsp", "", "Ready", "Ready", "RTSP", result.Message, []),
                null, null, _state, result.CheckedAt);
        }

        public async Task<LogosConnectionObservation> RefreshAsync(CancellationToken cancellationToken = default)
            => await ConnectAsync(ConnectionCredentials.Empty, true, cancellationToken);

        public async Task<CameraStreamRecord> OpenCameraStreamAsync(CameraStreamOpenRequest request, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(request.CameraSourceId, SourceId, StringComparison.Ordinal))
                throw new ArgumentException("The camera source does not belong to this RTSP connection.", nameof(request));
            _stream = await provider.OpenAsync(SourceId, Definition.Name, Definition.Target, cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);
            return _stream;
        }

        public async Task CloseCameraStreamAsync(string streamId, CancellationToken cancellationToken = default)
        {
            if (_stream is { } opened && (opened.Id == streamId || opened.StreamId == streamId))
            {
                await provider.CloseAsync(opened, cancellationToken);
                _stream = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(OperatorPolicyRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(OperatorPolicyEvaluation.Unavailable("RTSP media connections do not provide vehicle command authority."));

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_stream is not null) await CloseCameraStreamAsync(_stream.Id, cancellationToken);
            HasConnectBeenRequested = false;
            _state = AvailabilityState.Offline;
            _connectedAt = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void EvaluateFreshness(DateTimeOffset now, TimeSpan staleAfter, TimeSpan offlineAfter)
        {
            if (_lastSeen is { } seen && now - seen > offlineAfter) _state = AvailabilityState.Offline;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void SetProbe(CameraMediaSourceProbeResult result)
        {
            _lastSeen = result.CheckedAt;
            _lastError = result.Succeeded ? null : result.Message;
            _state = result.Succeeded ? AvailabilityState.Online : AvailabilityState.Faulted;
            if (result.Succeeded)
            {
                _connectedAt ??= result.CheckedAt;
                _lastConnectedAt = result.CheckedAt;
            }
            else _connectedAt = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
