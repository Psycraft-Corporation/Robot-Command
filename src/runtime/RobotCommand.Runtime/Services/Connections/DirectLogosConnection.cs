using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Logos.Api.Sdk;
using Logos.Api.V1;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Connections;

public sealed class DirectLogosConnection : IManagedConnection
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly AppConfiguration _configuration;
    private readonly ILogger<DirectLogosConnection> _logger;
    private readonly Dictionary<string, VehicleTelemetryRecord> _telemetry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkRecord> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConsoleEventRecord> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeometryOverlayRecord> _geometries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PerceptionTrackRecord> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CameraSourceRecord> _cameraSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CameraStreamRecord> _cameraStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<LiveStreamKind, LiveStreamRecord> _streams = [];
    private LogosClient? _client;
    private ConnectionCredentials _credentials = ConnectionCredentials.Empty;
    private AvailabilityState _state = AvailabilityState.Offline;
    private bool _hasConnectBeenRequested;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _connectedAt;
    private DateTimeOffset? _lastConnectedAt;
    private DateTimeOffset? _lastSeen;
    private DateTimeOffset? _lastSnapshotRefresh;
    private string? _lastError;
    private LogosConnectionObservation? _lastObservation;
    private CancellationTokenSource? _streamCancellation;
    private CancellationTokenSource? _connectCancellation;
    private Task[] _streamTasks = [];
    private int _streamGeneration;

    public DirectLogosConnection(
        ConnectionDefinition definition,
        AppConfiguration configuration,
        ILogger<DirectLogosConnection> logger)
    {
        Definition = definition;
        _configuration = configuration;
        _logger = logger;

        foreach (var kind in System.Enum.GetValues<LiveStreamKind>())
        {
            _streams[kind] = CreateStreamRecord(kind, LiveStreamState.Stopped);
        }
    }

    public event EventHandler? Changed;

    public ConnectionDefinition Definition { get; }

    public AvailabilityState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public bool IsTransportOpen
    {
        get
        {
            lock (_stateGate)
            {
                return _client is not null;
            }
        }
    }

    public bool HasConnectBeenRequested
    {
        get
        {
            lock (_stateGate)
            {
                return _hasConnectBeenRequested;
            }
        }
    }

    public bool HasActiveStreams
    {
        get
        {
            lock (_stateGate)
            {
                return _streams.Values.Any(item => item.State == LiveStreamState.Live);
            }
        }
    }

    public DateTimeOffset? LastAttempt
    {
        get
        {
            lock (_stateGate)
            {
                return _lastAttempt;
            }
        }
    }

    public DateTimeOffset? ConnectedAt
    {
        get
        {
            lock (_stateGate)
            {
                return _connectedAt;
            }
        }
    }

    public DateTimeOffset? LastConnectedAt
    {
        get
        {
            lock (_stateGate)
            {
                return _lastConnectedAt;
            }
        }
    }

    public DateTimeOffset? LastSeen
    {
        get
        {
            lock (_stateGate)
            {
                return _lastSeen;
            }
        }
    }

    public DateTimeOffset? LastSnapshotRefresh
    {
        get
        {
            lock (_stateGate)
            {
                return _lastSnapshotRefresh;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastError;
            }
        }
    }

    public LogosConnectionObservation? LastObservation
    {
        get
        {
            lock (_stateGate)
            {
                return _lastObservation;
            }
        }
    }

    public LogosConnectionLiveSnapshot LiveSnapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new LogosConnectionLiveSnapshot(
                    _telemetry.Values.OrderBy(item => item.VehicleId, StringComparer.Ordinal).ToArray(),
                    _links.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    _events.Values.OrderByDescending(item => item.Timestamp).ToArray(),
                    _streams.Values.OrderBy(item => item.Kind).ToArray(),
                    _geometries.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    _tracks.Values.OrderByDescending(item => item.Confidence).ToArray(),
                    _cameraSources.Values.OrderByDescending(item => item.Active).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    _cameraStreams.Values.OrderByDescending(item => item.ObservedAt).ToArray());
            }
        }
    }

    public async Task<LogosConnectionObservation> ConnectAsync(
        ConnectionCredentials credentials,
        bool reconnecting,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateGate)
        {
            _connectCancellation = attemptCancellation;
        }
        try
        {
            await StopLiveStreamsAsync();
            SetConnecting(credentials.Normalize(), reconnecting);

            LogosClient? oldClient;
            lock (_stateGate)
            {
                oldClient = _client;
                _client = CreateClient(_credentials);
            }

            oldClient?.Dispose();
            var observation = await DiscoverAndApplyAsync(attemptCancellation.Token);
            StartLiveStreams();
            RaiseChanged();
            return observation;
        }
        catch (Exception ex)
        {
            ApplyFailure(ex, disposeClient: true);
            RaiseChanged();
            throw;
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_connectCancellation, attemptCancellation))
                {
                    _connectCancellation = null;
                }
            }
            _operationGate.Release();
        }
    }

    public async Task<LogosConnectionObservation> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                if (_client is null)
                {
                    throw new InvalidOperationException($"Connection '{Definition.Name}' is not open.");
                }

                _lastAttempt = DateTimeOffset.UtcNow;
            }

            var observation = await DiscoverAndApplyAsync(cancellationToken);
            if (_configuration.LiveStreamsEnabled && _streamTasks.Length == 0)
            {
                StartLiveStreams();
            }

            RaiseChanged();
            return observation;
        }
        catch (Exception ex)
        {
            ApplyFailure(ex, disposeClient: false);
            RaiseChanged();
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<CameraStreamRecord> OpenCameraStreamAsync(
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CameraSourceId))
        {
            throw new ArgumentException("A camera source ID is required.", nameof(request));
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            LogosClient client;
            string? logosInstanceId;
            lock (_stateGate)
            {
                client = _client ?? throw new InvalidOperationException($"Connection '{Definition.Name}' is not open.");
                logosInstanceId = _lastObservation?.Runtime.LogosInstanceId;
            }

            var apiRequest = new OpenCameraStreamRequest
            {
                Command = MetadataHelpers.Command(),
                CameraSourceId = request.CameraSourceId,
                Options = new VideoStreamOptions
                {
                    PreferredProtocol = MapProtocol(request.Protocol),
                    Width = request.Width,
                    Height = request.Height,
                    FrameRateHz = request.FrameRateHz,
                    BitrateKbps = request.BitrateKbps,
                    Codec = request.Codec ?? string.Empty,
                    NegotiationPayload = request.NegotiationPayload ?? string.Empty
                }
            };
            var response = await client.Sensors.Raw.OpenCameraStreamAsync(
                apiRequest,
                StreamCallOptions(client, cancellationToken));
            StatusHelpers.EnsureAuthorized(response.Authorization);
            StatusHelpers.EnsureCommandOk(response.Result, "OpenCameraStream");

            var record = LogosLiveRecordMapper.ToCameraStreamRecord(
                Definition.Id,
                logosInstanceId,
                response.Session)
                ?? throw new LogosDomainException("OpenCameraStream returned no stream session.", "STREAM_SESSION_MISSING");
            lock (_stateGate)
            {
                _cameraStreams[record.Id] = record;
            }

            RaiseChanged();
            return record;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task CloseCameraStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamId))
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            LogosClient client;
            string? logosInstanceId;
            lock (_stateGate)
            {
                client = _client ?? throw new InvalidOperationException($"Connection '{Definition.Name}' is not open.");
                logosInstanceId = _lastObservation?.Runtime.LogosInstanceId;
            }

            var response = await client.Sensors.Raw.CloseCameraStreamAsync(
                new CloseCameraStreamRequest
                {
                    Command = MetadataHelpers.Command(),
                    StreamId = streamId,
                    Reason = "Closed by Robot Command"
                },
                StreamCallOptions(client, cancellationToken));
            StatusHelpers.EnsureAuthorized(response.Authorization);
            StatusHelpers.EnsureCommandOk(response.Result, "CloseCameraStream");

            var record = LogosLiveRecordMapper.ToCameraStreamRecord(
                Definition.Id,
                logosInstanceId,
                response.Session);
            lock (_stateGate)
            {
                if (record is null)
                {
                    _cameraStreams.Remove($"{Definition.Id}:{streamId}");
                }
                else
                {
                    _cameraStreams[record.Id] = record;
                }
            }

            RaiseChanged();
        }
        finally
        {
            _operationGate.Release();
        }
    }


    public async Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            LogosClient client;
            lock (_stateGate)
            {
                client = _client ?? throw new InvalidOperationException($"Connection '{Definition.Name}' is not open.");
            }

            var response = await client.Policy.EvaluateAsync(
                request.Action,
                PolicyEvaluationKind.Action,
                request.ContextJson,
                dryRun: true,
                correlationId: request.CorrelationId,
                cancellationToken: cancellationToken);
            StatusHelpers.EnsureAuthorized(response.Authorization);
            StatusHelpers.EnsureDomainOk(response.Status, "EvaluatePolicy");

            var decision = response.Decision;
            if (decision is null)
            {
                return OperatorPolicyEvaluation.Unavailable("Logos PolicyService returned no decision.");
            }

            var decisionName = decision.Decision.ToString();
            var allowed = string.Equals(decisionName, "Allow", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(decisionName, "Warn", StringComparison.OrdinalIgnoreCase);
            var findings = decision.Findings
                .Select(item => new OperatorPolicyFinding(
                    item.Code,
                    item.Severity.ToString(),
                    item.Message,
                    item.RecommendedAction))
                .ToArray();

            return new OperatorPolicyEvaluation(
                true,
                allowed,
                decisionName,
                decision.Summary,
                findings);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? connectCancellation;
        lock (_stateGate)
        {
            connectCancellation = _connectCancellation;
            _hasConnectBeenRequested = false;
        }
        connectCancellation?.Cancel();

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await StopLiveStreamsAsync();

            LogosClient? client;
            lock (_stateGate)
            {
                client = _client;
                _client = null;
                _state = AvailabilityState.Offline;
                _hasConnectBeenRequested = false;
                _connectedAt = null;
                _lastError = null;
                foreach (var key in _cameraSources.Keys.ToArray())
                {
                    _cameraSources[key] = _cameraSources[key] with
                    {
                        State = AvailabilityState.Offline,
                        Fresh = false,
                        Active = false
                    };
                }

                foreach (var key in _cameraStreams.Keys.ToArray())
                {
                    _cameraStreams[key] = _cameraStreams[key] with
                    {
                        State = "Closed",
                        Message = "Connection closed",
                        ObservedAt = DateTimeOffset.UtcNow
                    };
                }

                SetAllStreamsStoppedLocked();
            }

            client?.Dispose();
            RaiseChanged();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void EvaluateFreshness(DateTimeOffset now, TimeSpan staleAfter, TimeSpan offlineAfter)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (_client is null || _lastSeen is null)
            {
                return;
            }

            var age = now - _lastSeen.Value;
            var next = _state;
            if (age >= offlineAfter)
            {
                next = AvailabilityState.Offline;
            }
            else if (age >= staleAfter && _state != AvailabilityState.Faulted)
            {
                next = AvailabilityState.Stale;
            }

            if (next != _state)
            {
                _state = next;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _operationGate.Dispose();
    }

    private LogosClient CreateClient(ConnectionCredentials credentials)
        => new(new LogosClientOptions
        {
            Target = Definition.Target,
            ApiKey = credentials.ApiKey,
            BearerToken = credentials.BearerToken,
            Timeout = TimeSpan.FromSeconds(8),
            ThrowOnError = false,
            Metadata = new Dictionary<string, string>
            {
                ["x-logos-client"] = "robot-command",
                ["x-logos-client-version"] = ThisAssembly.Version,
                ["x-logos-connection-id"] = Definition.Id
            }
        });

    private async Task<LogosConnectionObservation> DiscoverAndApplyAsync(CancellationToken cancellationToken)
    {
        LogosClient client;
        lock (_stateGate)
        {
            client = _client ?? throw new InvalidOperationException("The Logos client is not available.");
            _lastAttempt = DateTimeOffset.UtcNow;
        }

        var identityTask = client.System.GetIdentityAsync(cancellationToken: cancellationToken);
        var healthTask = client.System.GetHealthAsync(cancellationToken: cancellationToken);
        await Task.WhenAll(identityTask, healthTask);

        var identityResponse = await identityTask;
        var healthResponse = await healthTask;
        EnsureSuccessful(identityResponse.Status?.Ok, identityResponse.Status?.Message, "GetSystemIdentity");
        EnsureSuccessful(healthResponse.Status?.Ok, healthResponse.Status?.Message, "GetHealth");

        var identity = identityResponse.Identity
            ?? throw new LogosDomainException("GetSystemIdentity returned no identity.", "IDENTITY_MISSING");
        var systemHealth = healthResponse.Health;

        var systemCapabilities = await ReadSystemCapabilitiesAsync(client, cancellationToken);
        var vehicleObservation = await ReadVehicleAsync(
            client,
            identity.LogosInstanceId,
            identity.VehicleId,
            identity.TeamId,
            cancellationToken);

        var runtimeObservation = new RuntimeObservation(
            identity.LogosInstanceId,
            EmptyToFallback(identity.DisplayName, identity.LogosInstanceId),
            identity.SystemRole.ToString(),
            identity.RuntimeMode.ToString(),
            EmptyToFallback(identity.PlatformKind, "Unknown"),
            EmptyToFallback(identity.PlatformProfile, "Unknown"),
            EmptyToFallback(identity.SoftwareVersion?.LogosVersion, "Unknown"),
            systemHealth?.Health.ToString() ?? "Unknown",
            systemHealth?.Readiness.ToString() ?? "Unknown",
            EmptyToFallback(systemHealth?.Code, "Unknown"),
            systemHealth?.Message ?? string.Empty,
            systemCapabilities);

        TeamObservation? teamObservation = null;
        if (!string.IsNullOrWhiteSpace(identity.TeamId))
        {
            teamObservation = new TeamObservation(
                identity.TeamId,
                identity.TeamId,
                identity.SystemRole.ToString().Contains("TeamManager", StringComparison.OrdinalIgnoreCase)
                    ? identity.LogosInstanceId
                    : null,
                EmptyToNull(identity.VehicleId));
        }

        var observedAt = DateTimeOffset.UtcNow;
        var availability = MapRuntimeAvailability(systemHealth?.Health.ToString(), systemHealth?.State.ToString());
        var observation = new LogosConnectionObservation(
            runtimeObservation,
            teamObservation,
            vehicleObservation,
            availability,
            observedAt);

        lock (_stateGate)
        {
            _lastObservation = observation;
            _lastSeen = observedAt;
            _lastSnapshotRefresh = observedAt;
            _lastError = null;
            _state = availability;
            _connectedAt ??= observedAt;
            _lastConnectedAt = observedAt;
        }

        await ReadFallbackLiveDataAsync(client, observation, cancellationToken);

        _logger.LogInformation(
            "Discovered Logos runtime {LogosInstanceId} on connection {ConnectionId}",
            runtimeObservation.LogosInstanceId,
            Definition.Id);

        return observation;
    }

    private async Task ReadFallbackLiveDataAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        var telemetryTask = ReadTelemetrySnapshotAsync(client, observation, cancellationToken);
        var linksTask = ReadLinksSnapshotAsync(client, observation, cancellationToken);
        var eventsTask = ReadEventsSnapshotAsync(client, observation, cancellationToken);
        var geometryTask = ReadGeometrySnapshotAsync(client, observation, cancellationToken);
        var tracksTask = ReadTracksSnapshotAsync(client, observation, cancellationToken);
        var camerasTask = ReadCameraSnapshotAsync(client, observation, cancellationToken);
        await Task.WhenAll(telemetryTask, linksTask, eventsTask, geometryTask, tracksTask, camerasTask);
    }

    private async Task ReadTelemetrySnapshotAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new GetVehicleTelemetryRequest
            {
                RequestId = MetadataHelpers.RequestId(),
                VehicleId = observation.Vehicle?.VehicleId ?? string.Empty,
                LogosInstanceId = observation.Runtime.LogosInstanceId,
                IncludeOdometry = true,
                IncludeGlobalPosition = true,
                IncludeHome = true,
                IncludeFreshness = true,
                IncludeDetails = true
            };
            var response = await client.Sensors.Raw.GetVehicleTelemetryAsync(
                request,
                StreamCallOptions(client, cancellationToken));

            if (response.Status is { Ok: false })
            {
                return;
            }

            var record = LogosLiveRecordMapper.ToTelemetryRecord(
                Definition.Id,
                observation.Runtime.LogosInstanceId,
                observation.Vehicle?.VehicleId,
                response.Telemetry);
            if (record is not null)
            {
                lock (_stateGate)
                {
                    _telemetry[record.Id] = record;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Telemetry snapshot failed for {ConnectionId}", Definition.Id);
        }
    }

    private async Task ReadLinksSnapshotAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Links.ListLinksAsync(
                includeStatus: true,
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: false })
            {
                return;
            }

            var statuses = response.LinkStatuses
                .Where(item => !string.IsNullOrWhiteSpace(item.LinkId))
                .ToDictionary(item => item.LinkId, StringComparer.Ordinal);
            var records = response.Links
                .Select(item => LogosLiveRecordMapper.ToLinkRecord(
                    Definition.Id,
                    observation.Runtime.LogosInstanceId,
                    item,
                    statuses.GetValueOrDefault(item.LinkId)))
                .Where(item => item is not null)
                .Cast<LinkRecord>()
                .ToArray();

            lock (_stateGate)
            {
                _links.Clear();
                foreach (var record in records)
                {
                    _links[record.Id] = record;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Link snapshot failed for {ConnectionId}", Definition.Id);
        }
    }

    private async Task ReadEventsSnapshotAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Events.ListEventsAsync(
                pageSize: Math.Min(_configuration.MaxEvents, 250),
                includePayloads: false,
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: false })
            {
                return;
            }

            foreach (var item in response.Events)
            {
                var record = LogosLiveRecordMapper.ToEventRecord(
                    Definition.Id,
                    observation.Runtime.LogosInstanceId,
                    item);
                if (record is not null)
                {
                    AddEvent(record);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Event snapshot failed for {ConnectionId}", Definition.Id);
        }
    }

    private async Task ReadGeometrySnapshotAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Geometry.ListObjectsAsync(
                includeObjects: true,
                pageSize: _configuration.MaxGeometryObjects,
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: false })
            {
                return;
            }

            var records = response.Objects
                .Select(item => LogosLiveRecordMapper.ToGeometryOverlayRecord(
                    Definition.Id,
                    observation.Runtime.LogosInstanceId,
                    item))
                .Where(item => item is not null)
                .Cast<GeometryOverlayRecord>()
                .ToArray();
            lock (_stateGate)
            {
                _geometries.Clear();
                foreach (var record in records)
                {
                    _geometries[record.Id] = record;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Geometry snapshot failed for {ConnectionId}", Definition.Id);
        }
    }

    private async Task ReadTracksSnapshotAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Perception.GetTracksAsync(
                minConfidence: _configuration.PerceptionMinConfidence,
                limit: (uint)_configuration.MaxPerceptionTracks,
                includePayloads: false,
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: false })
            {
                return;
            }

            ReplaceTracks(response.Tracks, response.ObservedAt, observation.Runtime.LogosInstanceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Perception track snapshot failed for {ConnectionId}", Definition.Id);
        }
    }

    private async Task ReadCameraSnapshotAsync(
        LogosClient client,
        LogosConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Sensors.Raw.ListCameraSourcesAsync(
                new ListCameraSourcesRequest
                {
                    RequestId = MetadataHelpers.RequestId(),
                    Page = new PageRequest { PageSize = _configuration.MaxCameraSources },
                    IncludeStatus = true,
                    IncludeIntrinsics = true
                },
                StreamCallOptions(client, cancellationToken));
            if (response.Status is { Ok: false })
            {
                return;
            }

            var muxStatuses = response.MuxStatus is null
                ? Enumerable.Empty<CameraSourceStatus>()
                : response.MuxStatus.Sources;
            var statuses = response.SourceStatuses
                .Concat(muxStatuses)
                .Where(item => !string.IsNullOrWhiteSpace(item.CameraSourceId))
                .GroupBy(item => item.CameraSourceId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.Ordinal);
            var records = response.Sources
                .Select(item => LogosLiveRecordMapper.ToCameraSourceRecord(
                    Definition.Id,
                    observation.Runtime.LogosInstanceId,
                    item,
                    statuses.GetValueOrDefault(item.CameraSourceId),
                    response.MuxStatus))
                .Where(item => item is not null)
                .Cast<CameraSourceRecord>()
                .ToList();

            foreach (var status in statuses.Values)
            {
                if (records.Any(item => item.CameraSourceId == status.CameraSourceId))
                {
                    continue;
                }

                var record = LogosLiveRecordMapper.ToCameraSourceRecord(
                    Definition.Id,
                    observation.Runtime.LogosInstanceId,
                    null,
                    status,
                    response.MuxStatus);
                if (record is not null)
                {
                    records.Add(record);
                }
            }

            lock (_stateGate)
            {
                _cameraSources.Clear();
                foreach (var record in records)
                {
                    _cameraSources[record.Id] = record;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Camera source snapshot failed for {ConnectionId}", Definition.Id);
        }
    }

    private void StartLiveStreams()
    {
        if (!_configuration.LiveStreamsEnabled)
        {
            return;
        }

        LogosClient client;
        int generation;
        CancellationToken token;
        lock (_stateGate)
        {
            client = _client ?? throw new InvalidOperationException("The Logos client is not available.");
            _streamCancellation = new CancellationTokenSource();
            token = _streamCancellation.Token;
            generation = ++_streamGeneration;
        }

        _streamTasks =
        [
            RunStreamLoopAsync(LiveStreamKind.Health, client, generation, ConsumeHealthStreamAsync, token),
            RunStreamLoopAsync(LiveStreamKind.VehicleState, client, generation, ConsumeVehicleStateStreamAsync, token),
            RunStreamLoopAsync(LiveStreamKind.VehicleTelemetry, client, generation, ConsumeTelemetryStreamAsync, token),
            RunStreamLoopAsync(LiveStreamKind.Links, client, generation, ConsumeLinksStreamAsync, token),
            RunStreamLoopAsync(LiveStreamKind.Events, client, generation, ConsumeEventsStreamAsync, token),
            RunStreamLoopAsync(LiveStreamKind.CameraStatus, client, generation, ConsumeCameraStatusStreamAsync, token),
            RunStreamLoopAsync(LiveStreamKind.PerceptionTracks, client, generation, ConsumeTracksStreamAsync, token)
        ];
    }

    private async Task StopLiveStreamsAsync()
    {
        CancellationTokenSource? cancellation;
        Task[] tasks;
        lock (_stateGate)
        {
            cancellation = _streamCancellation;
            _streamCancellation = null;
            tasks = _streamTasks;
            _streamTasks = [];
            _streamGeneration++;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A live stream stopped with an error for {ConnectionId}", Definition.Id);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunStreamLoopAsync(
        LiveStreamKind kind,
        LogosClient client,
        int generation,
        Func<LogosClient, int, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        var restartCount = 0;
        while (!cancellationToken.IsCancellationRequested && IsCurrentStreamGeneration(generation))
        {
            SetStreamState(kind, LiveStreamState.Starting, restartCount, null, started: true);
            try
            {
                await consume(client, generation, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException($"The {kind} stream ended unexpectedly.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                restartCount++;
                SetStreamState(kind, LiveStreamState.BackingOff, restartCount, ex.Message);
                _logger.LogDebug(
                    ex,
                    "{StreamKind} stream failed for {ConnectionId}; retry {RestartCount}",
                    kind,
                    Definition.Id,
                    restartCount);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_configuration.StreamRetrySeconds),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        SetStreamState(kind, LiveStreamState.Stopped, restartCount, null);
    }

    private async Task ConsumeHealthStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var request = new WatchHealthRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds)),
            IncludeDetails = true
        };
        using var call = client.System.Raw.WatchHealth(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(LiveStreamKind.Health, LiveStreamState.Live, CurrentRestartCount(LiveStreamKind.Health), null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            if (response.Health is not null)
            {
                ApplyHealth(response.Health);
            }

            MarkStreamMessage(LiveStreamKind.Health);
        }
    }

    private async Task ConsumeVehicleStateStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var observation = LastObservation;
        var request = new WatchVehicleStateRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            VehicleId = observation?.Vehicle?.VehicleId ?? string.Empty,
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds)),
            IncludeDetails = true
        };
        using var call = client.Vehicle.Raw.WatchVehicleState(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(LiveStreamKind.VehicleState, LiveStreamState.Live, CurrentRestartCount(LiveStreamKind.VehicleState), null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            if (response.State is not null)
            {
                ApplyVehicleState(response.State);
            }

            MarkStreamMessage(LiveStreamKind.VehicleState);
        }
    }

    private async Task ConsumeTelemetryStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var observation = LastObservation;
        var request = new WatchVehicleTelemetryRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            VehicleId = observation?.Vehicle?.VehicleId ?? string.Empty,
            LogosInstanceId = observation?.Runtime.LogosInstanceId ?? string.Empty,
            IncludeOdometry = true,
            IncludeGlobalPosition = true,
            IncludeHome = true,
            IncludeFreshness = true,
            IncludeDetails = true,
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds))
        };
        using var call = client.Sensors.Raw.WatchVehicleTelemetry(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(LiveStreamKind.VehicleTelemetry, LiveStreamState.Live, CurrentRestartCount(LiveStreamKind.VehicleTelemetry), null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            var currentObservation = LastObservation;
            var record = LogosLiveRecordMapper.ToTelemetryRecord(
                Definition.Id,
                currentObservation?.Runtime.LogosInstanceId,
                currentObservation?.Vehicle?.VehicleId,
                response.Telemetry,
                response.Odometry);
            if (record is not null)
            {
                lock (_stateGate)
                {
                    _telemetry[record.Id] = record;
                }
            }

            MarkStreamMessage(LiveStreamKind.VehicleTelemetry);
        }
    }

    private async Task ConsumeLinksStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var request = new WatchLinksRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            IncludeQuality = true,
            IncludePeer = true,
            IncludeDetails = true,
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds))
        };
        using var call = client.Links.Raw.WatchLinks(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(LiveStreamKind.Links, LiveStreamState.Live, CurrentRestartCount(LiveStreamKind.Links), null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            var record = LogosLiveRecordMapper.ToLinkRecord(
                Definition.Id,
                LastObservation?.Runtime.LogosInstanceId,
                response.Link,
                response.LinkStatus);
            if (record is not null)
            {
                lock (_stateGate)
                {
                    _links[record.Id] = record;
                }
            }

            MarkStreamMessage(LiveStreamKind.Links);
        }
    }

    private async Task ConsumeEventsStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var request = new WatchEventsRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            IncludePayloads = false,
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds))
        };
        using var call = client.Events.Raw.WatchEvents(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(LiveStreamKind.Events, LiveStreamState.Live, CurrentRestartCount(LiveStreamKind.Events), null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            var record = LogosLiveRecordMapper.ToEventRecord(
                Definition.Id,
                LastObservation?.Runtime.LogosInstanceId,
                response.Event);
            if (record is not null)
            {
                AddEvent(record);
            }

            MarkStreamMessage(LiveStreamKind.Events);
        }
    }

    private async Task ConsumeCameraStatusStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var request = new WatchCameraStatusRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            IncludeMuxStatus = true,
            IncludeStreams = true,
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds))
        };
        using var call = client.Sensors.Raw.WatchCameraStatus(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(
            LiveStreamKind.CameraStatus,
            LiveStreamState.Live,
            CurrentRestartCount(LiveStreamKind.CameraStatus),
            null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            IEnumerable<CameraSourceStatus> statuses = response.MuxStatus is null
                ? Enumerable.Empty<CameraSourceStatus>()
                : response.MuxStatus.Sources;
            if (response.SourceStatus is not null)
            {
                statuses = statuses.Append(response.SourceStatus);
            }

            foreach (var status in statuses.Where(item => !string.IsNullOrWhiteSpace(item.CameraSourceId)))
            {
                ApplyCameraSourceStatus(status, response.MuxStatus);
            }

            if (response.MuxStatus is not null &&
                !string.IsNullOrWhiteSpace(response.MuxStatus.ActiveCameraSourceId))
            {
                lock (_stateGate)
                {
                    foreach (var key in _cameraSources.Keys.ToArray())
                    {
                        var source = _cameraSources[key];
                        _cameraSources[key] = source with
                        {
                            Active = source.CameraSourceId == response.MuxStatus.ActiveCameraSourceId
                        };
                    }
                }
            }

            var stream = LogosLiveRecordMapper.ToCameraStreamRecord(
                Definition.Id,
                LastObservation?.Runtime.LogosInstanceId,
                response.Stream);
            if (stream is not null)
            {
                lock (_stateGate)
                {
                    _cameraStreams[stream.Id] = stream;
                }
            }

            MarkStreamMessage(LiveStreamKind.CameraStatus);
        }
    }

    private void ApplyCameraSourceStatus(
        CameraSourceStatus status,
        CameraMuxStatus? muxStatus)
    {
        var mapped = LogosLiveRecordMapper.ToCameraSourceRecord(
            Definition.Id,
            LastObservation?.Runtime.LogosInstanceId,
            null,
            status,
            muxStatus);
        if (mapped is null)
        {
            return;
        }

        lock (_stateGate)
        {
            if (_cameraSources.TryGetValue(mapped.Id, out var existing))
            {
                mapped = mapped with
                {
                    Name = existing.Name,
                    Kind = existing.Kind,
                    Width = mapped.Width == 0 ? existing.Width : mapped.Width,
                    Height = mapped.Height == 0 ? existing.Height : mapped.Height
                };
            }

            _cameraSources[mapped.Id] = mapped;
        }
    }

    private async Task ConsumeTracksStreamAsync(
        LogosClient client,
        int generation,
        CancellationToken cancellationToken)
    {
        var request = new WatchTracksRequest
        {
            RequestId = MetadataHelpers.RequestId(),
            MinConfidence = _configuration.PerceptionMinConfidence,
            IncludePayloads = false,
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(_configuration.StreamHeartbeatSeconds))
        };
        using var call = client.Perception.Raw.WatchTracks(
            request,
            StreamCallOptions(client, cancellationToken));
        SetStreamState(LiveStreamKind.PerceptionTracks, LiveStreamState.Live, CurrentRestartCount(LiveStreamKind.PerceptionTracks), null);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (!IsCurrentStreamGeneration(generation))
            {
                return;
            }

            var response = call.ResponseStream.Current;
            if (response.EventType != TrackEventType.Heartbeat)
            {
                ReplaceTracks(response.Tracks, response.ObservedAt, LastObservation?.Runtime.LogosInstanceId);
            }

            MarkStreamMessage(LiveStreamKind.PerceptionTracks);
        }
    }

    private void ReplaceTracks(
        IEnumerable<Track2D> tracks,
        Timestamp? observedAt,
        string? logosInstanceId)
    {
        var records = tracks
            .Select(item => LogosLiveRecordMapper.ToPerceptionTrackRecord(
                Definition.Id,
                logosInstanceId,
                item,
                observedAt))
            .Where(item => item is not null)
            .Cast<PerceptionTrackRecord>()
            .OrderByDescending(item => item.Confidence)
            .Take(_configuration.MaxPerceptionTracks)
            .ToArray();
        lock (_stateGate)
        {
            _tracks.Clear();
            foreach (var record in records)
            {
                _tracks[record.Id] = record;
            }
        }
    }

    private void ApplyHealth(SystemHealth health)
    {
        lock (_stateGate)
        {
            if (_lastObservation is null)
            {
                return;
            }

            var runtime = _lastObservation.Runtime with
            {
                Health = health.Health.ToString(),
                Readiness = health.Readiness.ToString(),
                HealthCode = EmptyToFallback(health.Code, "Unknown"),
                HealthMessage = health.Message ?? string.Empty
            };
            var availability = MapRuntimeAvailability(health.Health.ToString(), health.State.ToString());
            var observedAt = DateTimeOffset.UtcNow;
            _lastObservation = _lastObservation with
            {
                Runtime = runtime,
                Availability = availability,
                ObservedAt = observedAt
            };
            _state = availability;
            _lastSeen = observedAt;
            _lastError = null;
        }
    }

    private void ApplyVehicleState(VehicleState state)
    {
        lock (_stateGate)
        {
            if (_lastObservation?.Vehicle is null)
            {
                return;
            }

            var readiness = !string.IsNullOrWhiteSpace(state.Readiness?.Code)
                ? state.Readiness.Code
                : state.Readiness?.Readiness.ToString() ?? "Unknown";
            var vehicle = _lastObservation.Vehicle with
            {
                VehicleId = EmptyToFallback(state.VehicleId, _lastObservation.Vehicle.VehicleId),
                DisplayName = EmptyToFallback(state.VehicleId, _lastObservation.Vehicle.DisplayName),
                ProfileKey = EmptyToFallback(state.ProfileKey, _lastObservation.Vehicle.ProfileKey),
                Readiness = readiness,
                Lifecycle = state.LifecycleState.ToString(),
                ArmState = state.ArmState.ToString(),
                Health = state.Health.ToString()
            };
            var observedAt = DateTimeOffset.UtcNow;
            _lastObservation = _lastObservation with
            {
                Vehicle = vehicle,
                ObservedAt = observedAt
            };
            _lastSeen = observedAt;
            _lastError = null;
        }
    }

    private async Task<IReadOnlyList<string>> ReadSystemCapabilitiesAsync(
        LogosClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.System.GetCapabilitiesAsync(
                includeUnavailable: false,
                cancellationToken: cancellationToken);

            if (response.Status is { Ok: false })
            {
                return [];
            }

            return response.Capabilities
                .Where(item => item.Available && !string.IsNullOrWhiteSpace(item.CapabilityKey))
                .Select(item => item.CapabilityKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Capability discovery failed for {ConnectionId}", Definition.Id);
            return [];
        }
    }

    private async Task<VehicleObservation?> ReadVehicleAsync(
        LogosClient client,
        string logosInstanceId,
        string vehicleId,
        string teamId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            return null;
        }

        try
        {
            var stateResponse = await client.Vehicle.GetStateAsync(
                vehicleId: vehicleId,
                includeDetails: true,
                cancellationToken: cancellationToken);

            if (stateResponse.Status is { Ok: false } || stateResponse.State is null)
            {
                return new VehicleObservation(
                    vehicleId,
                    vehicleId,
                    logosInstanceId,
                    EmptyToNull(teamId),
                    "Unknown",
                    "Unknown",
                    string.Empty,
                    "Unknown",
                    "Unknown",
                    "Unknown",
                    "Unknown",
                    []);
            }

            var state = stateResponse.State;
            var profile = await ReadVehicleProfileAsync(client, state.ProfileKey, cancellationToken);
            var displayName = ReadVehicleDisplayName(state.Attributes, state.VehicleId, vehicleId);
            var readiness = !string.IsNullOrWhiteSpace(state.Readiness?.Code)
                ? state.Readiness.Code
                : state.Readiness?.Readiness.ToString() ?? "Unknown";

            var observedVehicleId = EmptyToFallback(state.VehicleId, vehicleId);
            return new VehicleObservation(
                observedVehicleId,
                displayName,
                logosInstanceId,
                EmptyToNull(teamId),
                profile.VehicleClass,
                profile.Domain,
                state.ProfileKey,
                readiness,
                state.LifecycleState.ToString(),
                state.ArmState.ToString(),
                state.Health.ToString(),
                profile.CapabilityKeys);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vehicle discovery failed for {VehicleId}", vehicleId);
            return new VehicleObservation(
                vehicleId,
                vehicleId,
                logosInstanceId,
                EmptyToNull(teamId),
                "Unknown",
                "Unknown",
                string.Empty,
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                []);
        }
    }

    public IReadOnlyList<LogosConnectionObservation> Observations
        => LastObservation is { } observation ? [observation] : [];

    private static string ReadVehicleDisplayName(
        IDictionary<string, string> attributes,
        string observedVehicleId,
        string fallbackVehicleId)
    {
        foreach (var key in new[] { "display_name", "vehicle_name", "name", "callsign" })
        {
            if (attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return EmptyToFallback(observedVehicleId, fallbackVehicleId);
    }

    private async Task<(string VehicleClass, string Domain, IReadOnlyList<string> CapabilityKeys)> ReadVehicleProfileAsync(
        LogosClient client,
        string profileKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            return ("Unknown", "Unknown", []);
        }

        try
        {
            var response = await client.Vehicle.GetProfileAsync(
                profileKey: profileKey,
                cancellationToken: cancellationToken);

            if (response.Status is { Ok: false } || response.Profile is null)
            {
                return ("Unknown", "Unknown", []);
            }

            var profile = response.Profile;
            var capabilityKeys = profile.Capabilities
                .Where(item => item.Available && !string.IsNullOrWhiteSpace(item.CapabilityKey))
                .Select(item => item.CapabilityKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();

            return (
                profile.Taxonomy?.VehicleClass.ToString() ?? "Unknown",
                profile.Taxonomy?.Domain.ToString() ?? "Unknown",
                capabilityKeys);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vehicle profile discovery failed for {ProfileKey}", profileKey);
            return ("Unknown", "Unknown", []);
        }
    }

    private void SetConnecting(ConnectionCredentials credentials, bool reconnecting)
    {
        lock (_stateGate)
        {
            _credentials = credentials;
            _hasConnectBeenRequested = true;
            _lastAttempt = DateTimeOffset.UtcNow;
            _lastError = null;
            _state = reconnecting
                ? AvailabilityState.Reconnecting
                : AvailabilityState.Connecting;
        }
    }

    private void ApplyFailure(Exception exception, bool disposeClient)
    {
        LogosClient? client = null;
        lock (_stateGate)
        {
            if (disposeClient)
            {
                client = _client;
                _client = null;
                SetAllStreamsStoppedLocked();
            }

            _lastError = exception.Message;
            _state = _lastObservation is null
                ? AvailabilityState.Faulted
                : AvailabilityState.Degraded;
        }

        client?.Dispose();
        _logger.LogWarning(
            exception,
            "Logos connection operation failed for {ConnectionId}",
            Definition.Id);
    }

    private void AddEvent(ConsoleEventRecord record)
    {
        lock (_stateGate)
        {
            _events[record.Id] = record;
            if (_events.Count > _configuration.MaxEvents)
            {
                foreach (var key in _events.Values
                             .OrderByDescending(item => item.Timestamp)
                             .Skip(_configuration.MaxEvents)
                             .Select(item => item.Id)
                             .ToArray())
                {
                    _events.Remove(key);
                }
            }
        }
    }

    private void MarkStreamMessage(LiveStreamKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_stateGate)
        {
            var current = _streams[kind];
            _streams[kind] = current with
            {
                State = LiveStreamState.Live,
                LastMessageAt = now,
                LastError = null
            };
            _lastSeen = now;
            if (_lastObservation is not null && _state is AvailabilityState.Stale or AvailabilityState.Offline)
            {
                _state = _lastObservation.Availability;
            }
        }

        RaiseChanged();
    }

    private void SetStreamState(
        LiveStreamKind kind,
        LiveStreamState state,
        int restartCount,
        string? lastError,
        bool started = false)
    {
        lock (_stateGate)
        {
            var current = _streams[kind];
            _streams[kind] = current with
            {
                State = state,
                LastStartedAt = started ? DateTimeOffset.UtcNow : current.LastStartedAt,
                RestartCount = restartCount,
                LastError = lastError
            };
        }

        RaiseChanged();
    }

    private int CurrentRestartCount(LiveStreamKind kind)
    {
        lock (_stateGate)
        {
            return _streams[kind].RestartCount;
        }
    }

    private bool IsCurrentStreamGeneration(int generation)
    {
        lock (_stateGate)
        {
            return generation == _streamGeneration && _client is not null;
        }
    }

    private void SetAllStreamsStoppedLocked()
    {
        foreach (var kind in _streams.Keys.ToArray())
        {
            var current = _streams[kind];
            _streams[kind] = current with { State = LiveStreamState.Stopped, LastError = null };
        }
    }

    private LiveStreamRecord CreateStreamRecord(LiveStreamKind kind, LiveStreamState state)
        => new($"{Definition.Id}:{kind}", Definition.Id, kind, state);

    private static VideoStreamProtocol MapProtocol(VideoProtocolPreference preference)
        => preference switch
        {
            VideoProtocolPreference.WebRtc => VideoStreamProtocol.Webrtc,
            VideoProtocolPreference.Rtsp => VideoStreamProtocol.Rtsp,
            VideoProtocolPreference.Mjpeg => VideoStreamProtocol.Mjpeg,
            VideoProtocolPreference.WebSocket => VideoStreamProtocol.Websocket,
            VideoProtocolPreference.Hls => VideoStreamProtocol.Hls,
            _ => VideoStreamProtocol.Unspecified
        };

    private static CallOptions StreamCallOptions(LogosClient client, CancellationToken cancellationToken)
        => new(
            headers: MetadataHelpers.BuildGrpcMetadata(client.Options),
            cancellationToken: cancellationToken);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A connection change subscriber failed for {ConnectionId}", Definition.Id);
        }
    }

    private static void EnsureSuccessful(bool? ok, string? message, string operation)
    {
        if (ok is false)
        {
            throw new LogosDomainException(
                string.IsNullOrWhiteSpace(message)
                    ? $"{operation} failed."
                    : $"{operation} failed: {message}");
        }
    }

    private static AvailabilityState MapRuntimeAvailability(string? health, string? state)
    {
        var combined = $"{health} {state}";
        if (combined.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return AvailabilityState.Degraded;
        }

        if (combined.Contains("Degraded", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return AvailabilityState.Degraded;
        }

        return AvailabilityState.Online;
    }

    private static string EmptyToFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
