using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using RobotCommand.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.State;

namespace RobotCommand.Services.Mavlink;

/// <summary>Latest position/velocity target for a PX4 Offboard formation member.</summary>
public sealed record Px4FormationSetpoint(
    float NorthMetres,
    float EastMetres,
    float DownMetres,
    float VelocityNorthMetresPerSecond,
    float VelocityEastMetresPerSecond,
    float VelocityDownMetresPerSecond);

/// <summary>Global relative-altitude target for ArduPilot Guided formation control.</summary>
public sealed record ArduPilotFormationSetpoint(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeRelativeMetres,
    float VelocityNorthMetresPerSecond,
    float VelocityEastMetresPerSecond,
    float VelocityDownMetresPerSecond);

public sealed record Px4FormationControlResult(
    bool Accepted,
    string State,
    string Message,
    DateTimeOffset? LastSetpointAt = null)
{
    public static Px4FormationControlResult Rejected(string message) => new(false, "Failed", message);
    public static Px4FormationControlResult Active(string message, DateTimeOffset at) => new(true, "Offboard active", message, at);
}

public sealed record ArduPilotFormationControlResult(
    bool Accepted,
    string State,
    string Message,
    DateTimeOffset? LastSetpointAt = null)
{
    public static ArduPilotFormationControlResult Rejected(string message) => new(false, "Failed", message);
    public static ArduPilotFormationControlResult Active(string message, DateTimeOffset at) => new(true, "Guided active", message, at);
}

public sealed record MavlinkCameraCapabilitySnapshot(
    bool HasCamera,
    bool HasGimbal,
    bool? SupportsPhoto,
    bool? SupportsVideo,
    bool? SupportsZoom,
    bool? SupportsRoll,
    string? CameraSourceId,
    string? CameraName,
    DateTimeOffset? ObservedAt);

public sealed record MavlinkManualControlStatus(
    string Mode,
    ManualControlModeClass ModeClass,
    bool ParameterAdmissionVerified,
    double StreamRateHertz,
    DateTimeOffset? LastInputSentAt,
    bool InputEchoAvailable,
    DateTimeOffset? LastInputEchoAt,
    string? SafeReleaseMode,
    bool? SafeReleaseConfirmed,
    string? Failure);

public sealed class MavlinkConnection : IManagedConnection, IMavlinkParameterClient, IMavlinkMissionClient
{
    private const ushort GimbalDeviceFlagsYawLock = 16;
    private const ushort GimbalDeviceFlagsYawInVehicleFrame = 32;
    private const ushort GimbalDeviceFlagsYawInEarthFrame = 64;
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CommandAckTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DelayedAckQuarantine = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SystemStaleAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SystemOfflineAfter = TimeSpan.FromSeconds(10);
    // Position control remains authoritative. Feed-forward is deliberately
    // damped for live autopilots because a full tangential/radial velocity
    // command makes small telemetry and timing errors show up as bounce,
    // especially while translation and rotation/resize are composed.
    private const float FormationVelocityFeedForwardGain = 0.35f;
    private readonly object _gate = new();
    private readonly IMavlinkTransport _transport;
    private readonly IMavlinkCodec _codec;
    private readonly IReadOnlyList<IMavlinkAutopilotAdapter> _adapters;
    private readonly Dictionary<string, IVehicleDiagnosticsProvider> _diagnosticsProviders;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly IUiDispatcher _dispatcher;
    private readonly MavlinkConnectionRegistry _registry;
    private readonly ILogger<MavlinkConnection> _logger;
    private readonly IEntityStore<string, MavlinkCameraDefinitionRecord>? _cameraDefinitions;
    private readonly IMavlinkCameraDefinitionLoader _cameraDefinitionLoader;
    private readonly MavlinkTransportRoute? _bootstrapRoute;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<byte, SystemState> _systems = [];
    private readonly Dictionary<(byte SystemId, byte ComponentId), MavlinkCameraState> _cameras = [];
    private readonly Dictionary<(byte SystemId, byte ComponentId), MavlinkGimbalState> _gimbals = [];
    private readonly HashSet<(byte SystemId, byte ComponentId)> _cameraInformationRequested = [];
    private readonly HashSet<(byte SystemId, byte ComponentId, uint MessageId)> _gimbalInformationRequested = [];
    private readonly ConcurrentDictionary<CommandKey, TaskCompletionSource<MavlinkCommandAck>> _pendingAcks = new();
    // MAVLink COMMAND_ACK has no request identifier. After a mutating command
    // times out, quarantine that command key briefly so a delayed ACK cannot
    // satisfy a newer operation for the same vehicle/command. Automatic
    // retries remain limited to idempotent telemetry housekeeping.
    private readonly ConcurrentDictionary<CommandKey, DateTimeOffset> _ackQuarantine = new();
    private readonly ConcurrentDictionary<MissionTransferKey, MissionTransferSession> _missionTransfers = new();
    private readonly ConcurrentDictionary<byte, Px4FormationSession> _formationSessions = new();
    private readonly ConcurrentDictionary<byte, ArduPilotFormationSession> _arduPilotFormationSessions = new();
    private readonly ConcurrentDictionary<byte, ArduPilotManualSession> _arduPilotManualSessions = new();
    private readonly Queue<ConsoleEventRecord> _events = new();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _heartbeatTask;
    private TaskCompletionSource<LogosConnectionObservation>? _firstHeartbeat;
    private uint _pingSequence;
    private int _reconnectAttempt;
    private long _mavlinkOneFrames;
    private long _mavlinkTwoFrames;
    private long _decodeErrors;
    private bool _disposed;

    private static class Log
    {
        private static readonly Action<ILogger, uint, byte, Exception?> _missionWithoutTransfer =
            LoggerMessage.Define<uint, byte>(LogLevel.Debug, new EventId(1, nameof(MissionWithoutTransfer)),
                "Ignoring MAVLink mission message {MessageId} from system {SystemId}; no transfer is active.");
        private static readonly Action<ILogger, uint, byte, byte, Exception?> _missionTypeMismatch =
            LoggerMessage.Define<uint, byte, byte>(LogLevel.Debug, new EventId(2, nameof(MissionTypeMismatch)),
                "Ignoring MAVLink mission message {MessageId} with mission type {MissionType}; transfer expects {ExpectedMissionType}.");
        private static readonly Action<ILogger, uint, byte, byte, Exception?> _missionReceived =
            LoggerMessage.Define<uint, byte, byte>(LogLevel.Information, new EventId(3, nameof(MissionReceived)),
                "Received MAVLink mission message {MessageId} from system {SystemId}, component {ComponentId}.");
        private static readonly Action<ILogger, ushort, int, Exception?> _invalidUploadRequest =
            LoggerMessage.Define<ushort, int>(LogLevel.Warning, new EventId(4, nameof(InvalidUploadRequest)),
                "Ignoring MAVLink mission request for out-of-range item {Sequence}; transfer contains {Count} items.");
        private static readonly Action<ILogger, ushort, string, Exception?> _itemRequested =
            LoggerMessage.Define<ushort, string>(LogLevel.Information, new EventId(5, nameof(ItemRequested)),
                "MAVLink mission transfer requested item {Sequence} ({Format}).");
        private static readonly Action<ILogger, byte, Exception?> _missionAcknowledged =
            LoggerMessage.Define<byte>(LogLevel.Information, new EventId(6, nameof(MissionAcknowledged)),
                "MAVLink mission transfer received MAV_MISSION_RESULT {Result}.");
        private static readonly Action<ILogger, ushort, int, Exception?> _invalidReceivedItem =
            LoggerMessage.Define<ushort, int>(LogLevel.Warning, new EventId(7, nameof(InvalidReceivedItem)),
                "Ignoring MAVLink mission item {Sequence}; expected count is {Count}.");
        private static readonly Action<ILogger, ushort, byte, Exception?> _commandRetry =
            LoggerMessage.Define<ushort, byte>(LogLevel.Debug, new EventId(8, nameof(CommandRetry)),
                "Retrying MAVLink command {Command} for system {SystemId}");
        private static readonly Action<ILogger, ushort, byte, int, Exception?> _commandAcknowledgementMissing =
            LoggerMessage.Define<ushort, byte, int>(LogLevel.Warning, new EventId(9, nameof(CommandAcknowledgementMissing)),
                "MAVLink command {Command} for system {SystemId} received no acknowledgement after {Attempts} attempt(s)");
        private static readonly Action<ILogger, string, byte, Exception?> _manualStreamFailed =
            LoggerMessage.Define<string, byte>(LogLevel.Warning, new EventId(10, nameof(ManualStreamFailed)),
                "ArduPilot manual-input stream failed for {ConnectionId}/{SystemId}");
        private static readonly Action<ILogger, string, byte, Exception?> _holdFailed =
            LoggerMessage.Define<string, byte>(LogLevel.Warning, new EventId(11, nameof(HoldFailed)),
                "Could not place {Autopilot} system {SystemId} into Hold");
        private static readonly Action<ILogger, string, Exception?> _auditError =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(12, nameof(AuditError)), "{MavlinkAuditMessage}");
        private static readonly Action<ILogger, string, Exception?> _auditWarning =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(13, nameof(AuditWarning)), "{MavlinkAuditMessage}");
        private static readonly Action<ILogger, string, Exception?> _auditInformation =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(14, nameof(AuditInformation)), "{MavlinkAuditMessage}");

        public static void MissionWithoutTransfer(ILogger logger, uint messageId, byte systemId) => _missionWithoutTransfer(logger, messageId, systemId, null);
        public static void MissionTypeMismatch(ILogger logger, uint messageId, byte missionType, byte expectedType) => _missionTypeMismatch(logger, messageId, missionType, expectedType, null);
        public static void MissionReceived(ILogger logger, uint messageId, byte systemId, byte componentId) => _missionReceived(logger, messageId, systemId, componentId, null);
        public static void InvalidUploadRequest(ILogger logger, ushort sequence, int count) => _invalidUploadRequest(logger, sequence, count, null);
        public static void ItemRequested(ILogger logger, ushort sequence, string format) => _itemRequested(logger, sequence, format, null);
        public static void MissionAcknowledged(ILogger logger, byte result) => _missionAcknowledged(logger, result, null);
        public static void InvalidReceivedItem(ILogger logger, ushort sequence, int count) => _invalidReceivedItem(logger, sequence, count, null);
        public static void CommandRetry(ILogger logger, ushort command, byte systemId) => _commandRetry(logger, command, systemId, null);
        public static void CommandAcknowledgementMissing(ILogger logger, ushort command, byte systemId, int attempts) => _commandAcknowledgementMissing(logger, command, systemId, attempts, null);
        public static void ManualStreamFailed(ILogger logger, Exception exception, string connectionId, byte systemId) => _manualStreamFailed(logger, connectionId, systemId, exception);
        public static void HoldFailed(ILogger logger, Exception exception, string autopilot, byte systemId) => _holdFailed(logger, autopilot, systemId, exception);
        public static void AuditError(ILogger logger, string message) => _auditError(logger, message, null);
        public static void AuditWarning(ILogger logger, string message) => _auditWarning(logger, message, null);
        public static void AuditInformation(ILogger logger, string message) => _auditInformation(logger, message, null);
    }

    public MavlinkConnection(
        ConnectionDefinition definition,
        IMavlinkTransport transport,
        IMavlinkCodec codec,
        IEnumerable<IMavlinkAutopilotAdapter> adapters,
        IEntityStore<string, OperationalCommandRecord> commands,
        IUiDispatcher dispatcher,
        MavlinkConnectionRegistry registry,
        ILogger<MavlinkConnection> logger,
        TimeSpan? heartbeatTimeout = null,
        MavlinkTransportRoute? bootstrapRoute = null,
        IEntityStore<string, MavlinkCameraDefinitionRecord>? cameraDefinitions = null,
        IMavlinkCameraDefinitionLoader? cameraDefinitionLoader = null)
        : this(definition, transport, codec, adapters,
            [new Px4VehicleDiagnosticsProvider(), new ArduPilotVehicleDiagnosticsProvider()], commands,
            dispatcher, registry, logger, heartbeatTimeout, bootstrapRoute, cameraDefinitions, cameraDefinitionLoader)
    {
    }

    public MavlinkConnection(
        ConnectionDefinition definition,
        IMavlinkTransport transport,
        IMavlinkCodec codec,
        IEnumerable<IMavlinkAutopilotAdapter> adapters,
        IEnumerable<IVehicleDiagnosticsProvider> diagnosticsProviders,
        IEntityStore<string, OperationalCommandRecord> commands,
        IUiDispatcher dispatcher,
        MavlinkConnectionRegistry registry,
        ILogger<MavlinkConnection> logger,
        TimeSpan? heartbeatTimeout = null,
        MavlinkTransportRoute? bootstrapRoute = null,
        IEntityStore<string, MavlinkCameraDefinitionRecord>? cameraDefinitions = null,
        IMavlinkCameraDefinitionLoader? cameraDefinitionLoader = null)
    {
        Definition = definition;
        _transport = transport;
        _codec = codec;
        _adapters = adapters.ToArray();
        var configuredBackend = (definition.Mavlink?.Autopilot ?? MavlinkAutopilotProfile.Px4) == MavlinkAutopilotProfile.ArduPilot
            ? "ArduPilot"
            : "PX4";
        _diagnosticsProviders = diagnosticsProviders
            .GroupBy(item => item.Backend, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!_diagnosticsProviders.ContainsKey(configuredBackend))
        {
            IVehicleDiagnosticsProvider fallback = configuredBackend == "ArduPilot"
                ? new ArduPilotVehicleDiagnosticsProvider()
                : new Px4VehicleDiagnosticsProvider();
            _diagnosticsProviders = new Dictionary<string, IVehicleDiagnosticsProvider>(_diagnosticsProviders, StringComparer.OrdinalIgnoreCase)
            {
                [fallback.Backend] = fallback
            };
        }
        _commands = commands;
        _dispatcher = dispatcher;
        _registry = registry;
        _logger = logger;
        _cameraDefinitions = cameraDefinitions;
        _cameraDefinitionLoader = cameraDefinitionLoader ?? new MavlinkCameraDefinitionLoader();
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(10);
        _bootstrapRoute = bootstrapRoute;
        _transport.ChunkReceived += OnChunkReceived;
        _transport.Faulted += OnTransportFaulted;
    }

    public event EventHandler? Changed;

    public ConnectionDefinition Definition { get; }

    private string ConfiguredAutopilotName =>
        (Definition.Mavlink?.Autopilot ?? MavlinkAutopilotProfile.Px4) == MavlinkAutopilotProfile.ArduPilot
            ? "ArduPilot"
            : "PX4";

    public AvailabilityState State { get; private set; } = AvailabilityState.Offline;

    public bool IsTransportOpen => _transport.IsOpen;

    public bool HasConnectBeenRequested { get; private set; }

    public bool HasActiveStreams => IsTransportOpen && Observations.Count > 0;

    public DateTimeOffset? LastAttempt { get; private set; }

    public DateTimeOffset? NextReconnectAt { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    public DateTimeOffset? LastConnectedAt { get; private set; }

    public DateTimeOffset? LastSeen
    {
        get
        {
            lock (_gate)
            {
                // A heartbeat identifies the vehicle, but any valid MAVLink
                // packet proves that the link is still carrying traffic.
                // Keep the connection's public last-seen value aligned with
                // the same traffic-based liveness used by EvaluateFreshness.
                return _systems.Count == 0 ? null : _systems.Values.Max(item => item.LastMessageAt);
            }
        }
    }

    public DateTimeOffset? LastSnapshotRefresh { get; private set; }

    public string? LastError { get; private set; }

    public LogosConnectionObservation? LastObservation => Observations.Count > 0 ? Observations[0] : null;

    public IReadOnlyList<LogosConnectionObservation> Observations
    {
        get
        {
            lock (_gate)
            {
                return _systems.Values
                    .OrderBy(item => item.SystemId)
                    .Select(ToObservation)
                    .ToArray();
            }
        }
    }

    public LogosConnectionLiveSnapshot LiveSnapshot
    {
        get
        {
            lock (_gate)
            {
                var telemetry = _systems.Values
                    .OrderBy(item => item.SystemId)
                    .Select(ToTelemetry)
                    .ToArray();
                var links = _systems.Values
                    .OrderBy(item => item.SystemId)
                    .Select(ToLink)
                    .ToArray();
                if (links.Length == 0 && _transport.KeepOpenWithoutHeartbeat && _transport.IsOpen)
                {
                    links = [ToTransportLink()];
                }
                var streams = new[]
                {
                    new LiveStreamRecord(
                        $"{Definition.Id}:mavlink-telemetry",
                        Definition.Id,
                        LiveStreamKind.VehicleTelemetry,
                        State is AvailabilityState.Online or AvailabilityState.Degraded
                            ? LiveStreamState.Live
                            : IsTransportOpen ? LiveStreamState.Starting : LiveStreamState.Stopped,
                        LastSeen,
                        ConnectedAt,
                        LastError: LastError)
                };
                return new LogosConnectionLiveSnapshot(
                    telemetry,
                    links,
                    _events.ToArray(),
                    streams,
                    [],
                    [],
                    _cameras.Values.Select(ToCameraSource).ToArray(),
                    [],
                    _systems.Values.OrderBy(item => item.SystemId).Select(ToDiagnostics).ToArray());
            }
        }
    }

    public async Task<LogosConnectionObservation> ConnectAsync(
        ConnectionCredentials credentials,
        bool reconnecting,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_transport.IsOpen &&
                State is AvailabilityState.Online or AvailabilityState.Degraded &&
                LastObservation is { } existing)
            {
                return existing;
            }

            HasConnectBeenRequested = true;
            LastAttempt = DateTimeOffset.UtcNow;
            LastError = null;
            State = reconnecting ? AvailabilityState.Reconnecting : AvailabilityState.Connecting;
            RaiseChanged();

            // A failed wait must not leave an open UDP socket paired with the
            // old heartbeat task. Reset any incomplete session before retrying.
            if (_transport.IsOpen)
            {
                await StopSessionAsync(clearSystems: true);
            }

            _sessionCancellation?.Dispose();
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _firstHeartbeat = new TaskCompletionSource<LogosConnectionObservation>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _transport.OpenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message;
                State = Definition.AutoReconnect ? AvailabilityState.Reconnecting : AvailabilityState.Faulted;
                ScheduleReconnect();
                RaiseChanged();
                throw;
            }
            _heartbeatTask = HeartbeatLoopAsync(_sessionCancellation.Token);
            await SendBootstrapHeartbeatAsync(_sessionCancellation.Token);

            try
            {
                return await _firstHeartbeat.Task.WaitAsync(_heartbeatTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                LastError = $"No {ConfiguredAutopilotName} MAVLink heartbeat was received on {Definition.Target}.";
                if (_transport.KeepOpenWithoutHeartbeat && _transport.IsOpen)
                {
                    State = AvailabilityState.Degraded;
                    LastError = $"Serial radio is open on {_transport.LocalEndpoint}; no {ConfiguredAutopilotName} MAVLink heartbeat has been received. The remote radio or aircraft may be unavailable.";
                }
                else
                {
                    await StopSessionAsync(clearSystems: true);
                    State = AvailabilityState.Faulted;
                    ScheduleReconnect();
                }
                RaiseChanged();
                throw new TimeoutException(LastError);
            }
            catch (OperationCanceledException)
            {
                // Caller cancellation is not a transport failure. Keep any
                // unsupported/discovered observations for diagnostics while
                // still closing the session so the next attempt is fresh.
                await StopSessionAsync(clearSystems: false);
                State = AvailabilityState.Offline;
                RaiseChanged();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<LogosConnectionObservation> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSnapshotRefresh = DateTimeOffset.UtcNow;
        EvaluateFreshness(LastSnapshotRefresh.Value, SystemStaleAfter, SystemOfflineAfter);
        return Task.FromResult(
            LastObservation ??
            throw new InvalidOperationException($"No supported {ConfiguredAutopilotName} MAVLink system has been discovered."));
    }

    public Task<CameraStreamRecord> OpenCameraStreamAsync(
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<CameraStreamRecord>(
            new NotSupportedException("Direct MAVLink camera control is not supported."));

    public Task CloseCameraStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Direct MAVLink camera control is not supported."));

    public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = request.Action is
            "vehicle.operator.arm" or
            "vehicle.operator.disarm" or
            "vehicle.operator.hold" or
            "vehicle.operator.takeoff" or
            "vehicle.operator.go_to" or
            "vehicle.operator.land" or
            "vehicle.operator.return_home" or
            "vehicle.operator.change_altitude" or
            "vehicle.operator.set_heading" or
            "camera.capture_photo" or
            "camera.start_video" or
            "camera.stop_video" or
            "gimbal.center" or
            "gimbal.nadir" or
            "gimbal.set_attitude";
        return Task.FromResult(new OperatorPolicyEvaluation(
            true,
            allowed,
            allowed ? "LOCAL_MAVLINK_ALLOW" : "LOCAL_MAVLINK_DENY",
            allowed
                ? $"The {ConfiguredAutopilotName} MAVLink operation is allowed by the local Robot Command allow-list."
                : $"The direct MAVLink action '{request.Action}' is not allowed.",
            [new OperatorPolicyFinding(
                allowed ? "MAVLINK_LOCAL_POLICY_ALLOWED" : "MAVLINK_LOCAL_POLICY_DENIED",
                allowed ? "Info" : "Blocking",
                allowed
                    ? "No Logos policy service is contacted for a direct MAVLink unit."
                    : $"Only basic {ConfiguredAutopilotName} Vehicle Operations are allowed.")]));
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            HasConnectBeenRequested = false;
            _reconnectAttempt = 0;
            NextReconnectAt = null;
            await StopSessionAsync(clearSystems: true);

            lock (_gate)
            {
                State = AvailabilityState.Offline;
            }
            RaiseChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void EvaluateFreshness(DateTimeOffset now, TimeSpan staleAfter, TimeSpan offlineAfter)
    {
        var safetyReleases = new List<(byte SystemId, byte ComponentId)>();
        lock (_gate)
        {
            foreach (var system in _systems.Values)
            {
                // Vehicle liveness follows valid MAVLink traffic. Heartbeats
                // identify the autopilot and confirm mode, but a delayed
                // heartbeat must not discard otherwise fresh position/status
                // telemetry on a slow or congested link.
                var age = system.LastMessageAt == default
                    ? TimeSpan.MaxValue
                    : now - system.LastMessageAt;
                system.Availability = age >= offlineAfter
                    ? AvailabilityState.Offline
                    : age >= staleAfter
                        ? AvailabilityState.Stale
                        : AvailabilityState.Online;
                if (system.ActiveOperation is { } staleOperation &&
                    system.Availability is AvailabilityState.Stale or AvailabilityState.Offline)
                {
                    UpdateCommand(
                        staleOperation.CommandId,
                        OperationalCommandState.Cancelled,
                        $"{ConfiguredAutopilotName} {OperatorCommandLabel(staleOperation.Command)} was interrupted because telemetry is {system.Availability.ToString().ToLowerInvariant()}.",
                        "MAVLINK_TELEMETRY_LOSS");
                    if (RequiresSafeReleaseAfterTelemetryLoss(staleOperation.Command))
                    {
                        safetyReleases.Add((system.SystemId, system.ComponentId));
                    }

                    system.ActiveOperation = null;
                }

                if (system.ActiveOperation is { } operation &&
                    now >= operation.ExpiresAt)
                {
                    var isArduPilot = AdapterFor(system)?.Profile == MavlinkAutopilotProfile.ArduPilot;
                    var outcome = operation.AcknowledgementReceived
                        ? $"{ConfiguredAutopilotName} {OperatorCommandLabel(operation.Command)} timed out before telemetry confirmed completion."
                        : $"{ConfiguredAutopilotName} {OperatorCommandLabel(operation.Command)} received no MAVLink acknowledgement and telemetry did not confirm completion. Check the vehicle before retrying.";
                    UpdateCommand(
                        operation.CommandId,
                        OperationalCommandState.TimedOut,
                        outcome,
                        operation.AcknowledgementReceived
                            ? isArduPilot ? "ARDUPILOT_COMMAND_TIMEOUT" : "MAVLINK_OPERATION_TIMEOUT"
                            : "MAVLINK_NO_ACK_TELEMETRY_UNCONFIRMED");
                    system.ActiveOperation = null;
                }
            }

            State = BestConnectionState();
            LastSnapshotRefresh = now;
        }
        RaiseChanged();
        foreach (var (systemId, componentId) in safetyReleases)
        {
            _ = TransitionToHoldAfterCompletionAsync(systemId, componentId);
        }
    }

    private static bool RequiresSafeReleaseAfterTelemetryLoss(OperatorCommandKind command)
        => command is OperatorCommandKind.Takeoff or
            OperatorCommandKind.GoTo or
            OperatorCommandKind.ChangeAltitude or
            OperatorCommandKind.SetHeading or
            OperatorCommandKind.Recover;

    public bool TryGetVehicle(
        string vehicleId,
        out byte systemId,
        out byte componentId,
        out VehicleTelemetryRecord? telemetry,
        out IMavlinkAutopilotAdapter? adapter)
    {
        lock (_gate)
        {
            var system = _systems.Values.FirstOrDefault(item =>
                VehicleId(item.SystemId).Equals(vehicleId, StringComparison.Ordinal));
            if (system is null)
            {
                systemId = 0;
                componentId = 0;
                telemetry = null;
                adapter = null;
                return false;
            }

            systemId = system.SystemId;
            componentId = system.ComponentId;
            telemetry = ToTelemetry(system);
            adapter = AdapterFor(system);
            return adapter is not null;
        }
    }

    public MavlinkCameraCapabilitySnapshot GetCameraCapabilities(string vehicleId, string? cameraSourceId = null)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out _, out _))
            return new(false, false, null, null, null, null, null, null, null);

        lock (_gate)
        {
            var camera = ResolveCamera(systemId, cameraSourceId);
            var gimbal = _gimbals.Values
                .Where(item => item.SystemId == systemId)
                .OrderByDescending(item => item.ManagerInformationReported)
                .ThenBy(item => item.ComponentId)
                .FirstOrDefault();
            var cameraFlags = camera?.CapabilityFlags ?? 0;
            var cameraFlagsKnown = camera is not null && cameraFlags != 0;
            var gimbalFlagsKnown = gimbal is not null && gimbal.CapabilityFlagsReported;
            var observedAt = new[] { camera?.LastMessageAt, gimbal?.LastMessageAt }
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .OrderByDescending(item => item)
                .FirstOrDefault();
            return new(
                camera is not null,
                gimbal is not null,
                camera is null ? null : !cameraFlagsKnown || (cameraFlags & 2) != 0,
                camera is null ? null : !cameraFlagsKnown || (cameraFlags & 1) != 0,
                camera is null ? null : !cameraFlagsKnown || (cameraFlags & 64) != 0,
                gimbal is null ? null : !gimbalFlagsKnown || (gimbal.CapabilityFlags & 4) != 0,
                camera is null ? null : CameraSourceId(camera.SystemId, camera.ComponentId),
                camera is null ? null : string.Join(" ", new[] { camera.VendorName, camera.ModelName }.Where(item => !string.IsNullOrWhiteSpace(item))),
                observedAt == default ? null : observedAt);
        }
    }

    public async Task<Px4ParameterFile> DownloadParametersAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) ||
            adapter?.Profile is not (MavlinkAutopilotProfile.Px4 or MavlinkAutopilotProfile.ArduPilot))
        {
            throw new InvalidOperationException($"The selected unit is not an available {ConfiguredAutopilotName} MAVLink vehicle.");
        }

        SystemState system;
        TaskCompletionSource<bool> signal;
        MavlinkTransportRoute route;
        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out system!) || system.Route is not { } systemRoute)
            {
                throw new InvalidOperationException($"The {ConfiguredAutopilotName} MAVLink route is no longer available.");
            }

            route = systemRoute;
            system.Parameters.Clear();
            signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            system.ParameterSignal = signal;
        }

        try
        {
            await _transport.SendAsync(
                _codec.EncodeParameterRequestList(
                    options.SourceSystemId,
                    options.SourceComponentId,
                    systemId,
                    componentId),
                route,
                cancellationToken);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            var retryIndexes = new HashSet<short>();
            var quietSince = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.WhenAny(signal.Task, Task.Delay(250, cancellationToken));

                ushort expected;
                int count;
                DateTimeOffset lastParameter;
                lock (_gate)
                {
                    expected = system.Parameters.Values
                        .Select(value => value.Count)
                        .FirstOrDefault(value => value > 0);
                    count = system.Parameters.Count;
                    lastParameter = system.Parameters.Values
                        .Select(value => value.ReceivedAt)
                        .DefaultIfEmpty(DateTimeOffset.MinValue)
                        .Max();
                }

                if (lastParameter > quietSince)
                {
                    quietSince = lastParameter;
                }

                if (expected > 0 && count >= expected && DateTimeOffset.UtcNow - quietSince > TimeSpan.FromMilliseconds(500))
                {
                    break;
                }

                if (expected > 0 && DateTimeOffset.UtcNow - quietSince > TimeSpan.FromSeconds(2))
                {
                    short[] missing;
                    lock (_gate)
                    {
                        missing = Enumerable.Range(0, expected)
                            .Select(index => (short)index)
                            .Where(index => !system.Parameters.Values.Any(value => value.Index == index))
                            .Where(retryIndexes.Add)
                            .Take(64)
                            .ToArray();
                    }

                    foreach (var index in missing)
                    {
                        await _transport.SendAsync(
                            _codec.EncodeParameterRequestRead(
                                options.SourceSystemId,
                                options.SourceComponentId,
                                systemId,
                                componentId,
                                string.Empty,
                                index),
                            route,
                            cancellationToken);
                    }
                    quietSince = DateTimeOffset.UtcNow;
                }
            }

            lock (_gate)
            {
                return new Px4ParameterFile(
                    system.Parameters.Values
                        .OrderBy(value => value.ComponentId)
                        .ThenBy(value => value.Index)
                        .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(value => new Px4ParameterEntry(
                            value.SystemId,
                            value.ComponentId,
                            value.Name,
                            value.Value.ToString("R", CultureInfo.InvariantCulture),
                            value.Type))
                        .ToArray(),
                    []);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var current) && ReferenceEquals(current.ParameterSignal, signal))
                {
                    current.ParameterSignal = null;
                }
            }
        }
    }

    public async Task<bool> SetParameterAsync(
        string vehicleId,
        Px4ParameterEntry entry,
        float value,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) ||
            adapter?.Profile is not (MavlinkAutopilotProfile.Px4 or MavlinkAutopilotProfile.ArduPilot))
        {
            return false;
        }

        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
        var effectiveComponentId = entry.ComponentId == 0 ? componentId : entry.ComponentId;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            TaskCompletionSource<MavlinkParameterValue> waiter;
            MavlinkTransportRoute route;
            lock (_gate)
            {
                if (!_systems.TryGetValue(systemId, out var system) || system.Route is not { } systemRoute)
                {
                    return false;
                }

                route = systemRoute;
                waiter = new TaskCompletionSource<MavlinkParameterValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                system.ParameterWaiters[(effectiveComponentId, entry.Name)] = waiter;
            }

            try
            {
                await _transport.SendAsync(
                    _codec.EncodeParameterSet(
                        options.SourceSystemId,
                        options.SourceComponentId,
                        systemId,
                        effectiveComponentId,
                        entry.Name,
                        value,
                        entry.Type),
                    route,
                    cancellationToken);
                var completed = await Task.WhenAny(waiter.Task, Task.Delay(1500, cancellationToken));
                if (completed == waiter.Task)
                {
                    var response = await waiter.Task;
                    if (Math.Abs(response.Value - value) <= Math.Max(0.000001f, Math.Abs(value) * 0.000001f))
                    {
                        return true;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_systems.TryGetValue(systemId, out var system) &&
                        system.ParameterWaiters.TryGetValue((effectiveComponentId, entry.Name), out var current) &&
                        ReferenceEquals(current, waiter))
                    {
                        system.ParameterWaiters.Remove((effectiveComponentId, entry.Name));
                    }
                }
            }
        }

        return false;
    }

    public async Task<MavlinkMissionTransferResult> UploadMissionAsync(
        string vehicleId,
        IReadOnlyList<MavlinkMissionItem> items,
        CancellationToken cancellationToken = default)
        => await UploadMissionCoreAsync(vehicleId, items, 0, cancellationToken);

    public async Task<MavlinkMissionTransferResult> UploadFenceAsync(string vehicleId, IReadOnlyList<MavlinkMissionItem> items, CancellationToken cancellationToken = default)
        => await UploadMissionCoreAsync(vehicleId, items.Select(item => item with { MissionType = 1 }).ToArray(), 1, cancellationToken);

    public async Task<MavlinkMissionTransferResult> ClearFenceAsync(string vehicleId, CancellationToken cancellationToken = default)
        => await UploadMissionCoreAsync(vehicleId, [], 1, cancellationToken);

    private async Task<MavlinkMissionTransferResult> UploadMissionCoreAsync(
        string vehicleId,
        IReadOnlyList<MavlinkMissionItem> items,
        byte missionType,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0 && missionType == 0) return new(false, "A mission needs at least one item.", []);
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) || adapter is null || !adapter.SupportsMissionExecution)
            return new(false, "The selected unit is not an available PX4 or ArduPilot MAVLink multicopter.", []);
        MavlinkTransportRoute route;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system) || system.Route is null)
                return new(false, "No MAVLink route is available for the selected unit.", []);
            route = system.Route;
        }
        var transferKey = new MissionTransferKey(systemId, componentId, missionType);
        var session = MissionTransferSession.ForUpload(items, missionType, componentId);
        if (!_missionTransfers.TryAdd(transferKey, session)) return new(false, "Another mission transfer is already active for this unit and mission type.", []);
        try
        {
            var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
            session.ResetSignal();
            _logger.LogInformation(
                "Starting MAVLink mission upload to system {SystemId}, component {ComponentId}: {Count} items via {Route}",
                systemId,
                componentId,
                items.Count,
                route.DisplayName);
            await _transport.SendAsync(_codec.EncodeMissionCount(options.SourceSystemId, options.SourceComponentId, systemId, componentId, checked((ushort)items.Count), missionType), route, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, items.Count * 3)));
            var retryCount = 0;
            while (!timeout.IsCancellationRequested)
            {
                MissionSignal signal;
                try
                {
                    signal = await session.Signal.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
                }
                catch (TimeoutException) when (retryCount++ < 2)
                {
                    _logger.LogInformation(
                        "Retrying MAVLink mission count for system {SystemId} after no item request (attempt {Attempt}).",
                        systemId,
                        retryCount + 1);
                    session.ResetSignal();
                    await _transport.SendAsync(_codec.EncodeMissionCount(options.SourceSystemId, options.SourceComponentId, systemId, componentId, checked((ushort)items.Count), missionType), route, timeout.Token);
                    continue;
                }
                if (signal.Kind == MissionSignalKind.Request)
                {
                    var item = items.FirstOrDefault(value => value.Sequence == signal.Sequence);
                    if (item is null) return new(false, $"Vehicle requested unknown mission item {signal.Sequence}.", items);
                    retryCount = 0;
                    session.ResetSignal();
                    _logger.LogInformation(
                        "Sending MAVLink mission item {Sequence} to system {SystemId} as {WireFormat}.",
                        item.Sequence,
                        systemId,
                        signal.UsesIntegerItems ? "MISSION_ITEM_INT" : "MISSION_ITEM");
                    var payload = signal.UsesIntegerItems
                        ? _codec.EncodeMissionItemInt(options.SourceSystemId, options.SourceComponentId, systemId, componentId, item)
                        : _codec.EncodeMissionItem(options.SourceSystemId, options.SourceComponentId, systemId, componentId, item);
                    await _transport.SendAsync(payload, route, timeout.Token);
                    continue;
                }
                if (signal.Kind == MissionSignalKind.Ack)
                {
                    _logger.LogInformation(
                        "MAVLink mission upload acknowledgement from system {SystemId}: MAV_MISSION_RESULT {Result}.",
                        systemId,
                        signal.Result);
                    return new(signal.Result == 0, signal.Result == 0
                        ? $"Uploaded {items.Count} mission items."
                        : $"Vehicle rejected mission item {session.LastRequestedSequence?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}: {DescribeMissionResult(signal.Result)}.", items);
                }
            }
            return new(false, "Mission upload timed out waiting for the vehicle.", items);
        }
        // The bounded transfer timeout intentionally cancels the linked token. Do
        // not let that implementation detail escape as an opaque task-cancelled
        // workflow failure.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Mission upload timed out waiting for the vehicle.", items);
        }
        catch (TimeoutException) { return new(false, "Mission upload timed out waiting for the vehicle.", items); }
        finally { _missionTransfers.TryRemove(transferKey, out _); }
    }

    public async Task<MavlinkMissionTransferResult> DownloadMissionAsync(string vehicleId, CancellationToken cancellationToken = default)
        => await DownloadMissionCoreAsync(vehicleId, 0, cancellationToken);

    public async Task<MavlinkMissionTransferResult> DownloadFenceAsync(string vehicleId, CancellationToken cancellationToken = default)
        => await DownloadMissionCoreAsync(vehicleId, 1, cancellationToken);

    private async Task<MavlinkMissionTransferResult> DownloadMissionCoreAsync(string vehicleId, byte missionType, CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) || adapter is null || !adapter.SupportsMissionExecution)
            return new(false, "The selected unit is not an available PX4 or ArduPilot MAVLink multicopter.", []);
        MavlinkTransportRoute route;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system) || system.Route is null) return new(false, "No MAVLink route is available for the selected unit.", []);
            route = system.Route;
        }
        var transferKey = new MissionTransferKey(systemId, componentId, missionType);
        var session = MissionTransferSession.ForDownload(missionType, componentId);
        if (!_missionTransfers.TryAdd(transferKey, session)) return new(false, "Another mission transfer is already active for this unit and mission type.", []);
        try
        {
            var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
            session.ResetSignal();
            await _transport.SendAsync(_codec.EncodeMissionRequestList(options.SourceSystemId, options.SourceComponentId, systemId, componentId, missionType), route, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var retryCount = 0;
            ushort? requestedSequence = null;
            while (!timeout.IsCancellationRequested)
            {
                MissionSignal signal;
                try
                {
                    signal = await session.Signal.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
                }
                catch (TimeoutException) when (retryCount++ < 2)
                {
                    session.ResetSignal();
                    if (requestedSequence is { } sequence)
                        await _transport.SendAsync(_codec.EncodeMissionRequestInt(options.SourceSystemId, options.SourceComponentId, systemId, componentId, sequence, missionType), route, timeout.Token);
                    else
                        await _transport.SendAsync(_codec.EncodeMissionRequestList(options.SourceSystemId, options.SourceComponentId, systemId, componentId, missionType), route, timeout.Token);
                    continue;
                }
                if (signal.Kind == MissionSignalKind.Count)
                {
                    session.ExpectedCount = signal.Sequence;
                    if (signal.Sequence == 0) return new(true, "Vehicle mission is empty.", []);
                    retryCount = 0;
                    requestedSequence = 0;
                    session.ResetSignal();
                    await _transport.SendAsync(_codec.EncodeMissionRequestInt(options.SourceSystemId, options.SourceComponentId, systemId, componentId, 0, missionType), route, timeout.Token);
                    continue;
                }
                if (signal.Kind == MissionSignalKind.Item && signal.Item is not null)
                {
                    retryCount = 0;
                    if (session.Items.All(item => item.Sequence != signal.Item.Sequence)) session.Items.Add(signal.Item);
                    if (session.HasAllItems)
                    {
                        await _transport.SendAsync(_codec.EncodeMissionAck(options.SourceSystemId, options.SourceComponentId, systemId, componentId, 0, missionType), route, timeout.Token);
                        return new(true, $"Downloaded {session.Items.Count} mission items.", session.Items.OrderBy(item => item.Sequence).ToArray());
                    }
                    requestedSequence = session.NextMissingSequence;
                    session.ResetSignal();
                    await _transport.SendAsync(_codec.EncodeMissionRequestInt(options.SourceSystemId, options.SourceComponentId, systemId, componentId, requestedSequence.Value, missionType), route, timeout.Token);
                }
            }
            return new(false, "Mission download timed out waiting for the vehicle.", []);
        }
        // See UploadMissionAsync: the linked timeout is an expected, bounded
        // mission-protocol outcome rather than a caller-requested cancellation.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Mission download timed out waiting for the vehicle.", []);
        }
        catch (TimeoutException) { return new(false, "Mission download timed out waiting for the vehicle.", []); }
        finally { _missionTransfers.TryRemove(transferKey, out _); }
    }

    public async Task<MavlinkMissionTransferResult> SetMissionModeAsync(string vehicleId, bool paused, bool restartFromBeginning = true, bool sendMissionStartCommand = false, CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) || adapter is null || !adapter.SupportsMissionExecution)
            return new(false, "The selected unit is not an available PX4 or ArduPilot MAVLink multicopter.", []);

        if (!paused && restartFromBeginning)
        {
            MavlinkTransportRoute route;
            lock (_gate)
            {
                if (!_systems.TryGetValue(systemId, out var system) || system.Route is null)
                    return new(false, "Mission start could not select item 0 because no remote MAVLink endpoint is known.", []);
                route = system.Route;
            }

            try
            {
                var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
                await _transport.SendAsync(
                    _codec.EncodeMissionSetCurrent(options.SourceSystemId, options.SourceComponentId, systemId, componentId, 0),
                    route,
                    cancellationToken);
                lock (_gate)
                {
                    if (_systems.TryGetValue(systemId, out var system))
                    {
                        system.CurrentMissionItem = 0;
                        system.LastReachedMissionItem = null;
                        system.MissionUpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                return new(false, $"Mission start could not select item 0: {exception.Message}", []);
            }
        }

        // Start the confirmation window before sending the mode request. A PX4
        // heartbeat can arrive synchronously on an in-process transport (and
        // very quickly over a local SITL bridge), so capturing this timestamp
        // afterward can incorrectly discard the confirming heartbeat.
        var confirmationRequestedAt = DateTimeOffset.UtcNow;
        var customMode = paused ? adapter.HoldMode : adapter.MissionMode;
        var description = paused ? $"{adapter.DisplayName} mission pause" : $"{adapter.DisplayName} mission start";
        var modeCommand = new MavlinkCommandEnvelope(
            MavlinkCommandIds.DoSetMode,
            [],
            description,
            MavlinkWireKind.SetMode,
            BaseMode: MavlinkValues.MavModeFlagCustomModeEnabled,
            CustomMode: customMode);
        var acknowledgement = await SendCommandAsync(systemId, componentId, modeCommand, cancellationToken);
        if (!acknowledgement.Accepted)
        {
            var message = adapter.Profile == MavlinkAutopilotProfile.ArduPilot
                ? await DescribeArduPilotAcknowledgementAsync(systemId, description, acknowledgement, cancellationToken)
                : $"{description} rejected: {acknowledgement.ResultCode}.";
            return new(false, message, []);
        }

        var confirmed = await WaitForModeAsync(systemId, customMode, confirmationRequestedAt, cancellationToken);
        if (!confirmed)
        {
            return new(false,
                $"{adapter.DisplayName} did not confirm {adapter.MissionModeName} or {adapter.HoldModeName} in a heartbeat. Check the latest vehicle status text.",
                []);
        }

        if (!paused && sendMissionStartCommand && adapter.Profile == MavlinkAutopilotProfile.ArduPilot)
        {
            var missionStart = new MavlinkCommandEnvelope(
                MavlinkCommandIds.MissionStart,
                [0f, 0f, 0f, 0f, 0f, 0f, 0f],
                "ArduPilot mission start");
            var missionStartAcknowledgement = await SendCommandAsync(systemId, componentId, missionStart, cancellationToken);
            if (!missionStartAcknowledgement.Accepted)
                return new(false,
                    await DescribeArduPilotAcknowledgementAsync(
                        systemId,
                        "mission start",
                        missionStartAcknowledgement,
                        cancellationToken), []);
        }

        return new(true, paused
            ? $"{adapter.DisplayName} confirmed {adapter.HoldModeName}."
            : $"{adapter.DisplayName} confirmed {adapter.MissionModeName} mode from {(restartFromBeginning ? "item 0" : "the current item")}.", []);
    }

    public bool TryGetMissionProgress(string vehicleId, out int? currentItemIndex, out DateTimeOffset updatedAt)
    {
        lock (_gate)
        {
            var system = _systems.Values.FirstOrDefault(item => VehicleId(item.SystemId).Equals(vehicleId, StringComparison.Ordinal));
            currentItemIndex = system?.LastReachedMissionItem ?? system?.CurrentMissionItem;
            updatedAt = system?.MissionUpdatedAt ?? DateTimeOffset.MinValue;
            return system is not null;
        }
    }

    public async Task<MavlinkMissionTransferResult> SetReturnToLaunchAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        var request = new OperatorCommandRequest(
            $"mission-end-rtl-{Definition.Id}-{Guid.NewGuid():N}",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            OperatorCommandKind.Recover,
            new OperatorCommandTarget(Definition.Id, vehicleId, null, DateTimeOffset.UtcNow),
            "Configured mission end action: Return to launch",
            false,
            DateTimeOffset.UtcNow,
            Parameters: OperatorCommandParameters.None);
        var result = await SendOperatorCommandAsync(request, cancellationToken);
        return new(result.Accepted, result.Message, []);
    }

    public bool TryGetMissionState(string vehicleId, out MavlinkMissionState state)
    {
        lock (_gate)
        {
            var system = _systems.Values.FirstOrDefault(item => VehicleId(item.SystemId).Equals(vehicleId, StringComparison.Ordinal));
            if (system is null)
            {
                state = default!;
                return false;
            }

            state = new(
                system.CurrentMissionItem,
                system.LastReachedMissionItem,
                system.MissionUpdatedAt,
                Armed(system),
                system.LandedState,
                AdapterFor(system)?.DecodeMode(system.CustomMode) ?? "Unknown",
                system.MissionState);
            return true;
        }
    }

    public async Task<MavlinkMissionTransferResult> SetMissionCurrentAsync(string vehicleId, ushort missionItemIndex, CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) || adapter is null || !adapter.SupportsMissionExecution)
            return new(false, "The selected unit is not an available PX4 or ArduPilot MAVLink multicopter.", []);
        MavlinkTransportRoute route;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system) || system.Route is null)
                return new(false, "No MAVLink route is available for the selected unit.", []);
            route = system.Route;
        }
        try
        {
            var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
            await _transport.SendAsync(_codec.EncodeMissionSetCurrent(options.SourceSystemId, options.SourceComponentId, systemId, componentId, missionItemIndex), route, cancellationToken);
            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var system))
                {
                    system.CurrentMissionItem = missionItemIndex;
                    system.LastReachedMissionItem = null;
                    system.MissionUpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            return new(true, $"{adapter.DisplayName} mission resume position set to item {missionItemIndex}.", []);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return new(false, $"{adapter.DisplayName} mission resume position could not be set: {exception.Message}", []);
        }
    }

    public async Task<MavlinkMissionTransferResult> ClearMissionAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out var adapter) || adapter is null || !adapter.SupportsMissionExecution)
            return new(false, "The selected unit is not an available PX4 or ArduPilot MAVLink multicopter.", []);
        MavlinkTransportRoute route;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system) || system.Route is null)
                return new(false, "No MAVLink route is available for the selected unit.", []);
            route = system.Route;
        }
        try
        {
            var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
            await _transport.SendAsync(_codec.EncodeMissionClearAll(options.SourceSystemId, options.SourceComponentId, systemId, componentId), route, cancellationToken);
            return new(true, $"{adapter.DisplayName} onboard mission removed.", []);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return new(false, $"PX4 onboard mission could not be removed: {exception.Message}", []);
        }
    }

    public async Task<MavlinkCommandDispatchResult> SendOperatorCommandAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(
                request.Target.VehicleId,
                out var systemId,
                out var componentId,
                out var telemetry,
                out var adapter) ||
            telemetry is null ||
            adapter is null)
        {
            return MavlinkCommandDispatchResult.Rejected($"The {ConfiguredAutopilotName} MAVLink unit is no longer available.");
        }

        // Keep ArduPilot arm operations out of pilot-throttle modes. A manual
        // controller can continue sending throttle input in Stabilize, while
        // Guided makes the vehicle's app-controlled arm/start state explicit.
        // This is only a mode transition before an explicit Arm request; it
        // does not bypass pre-arm checks and never arms implicitly.
        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot &&
            request.Command == OperatorCommandKind.Arm &&
            !telemetry.Armed &&
            !string.Equals(telemetry.AirframeMode, "Guided", StringComparison.OrdinalIgnoreCase))
        {
            var requestedAt = DateTimeOffset.UtcNow;
            var mode = await SendModeAsync(
                systemId,
                MavlinkValues.ArduPilotGuidedCustomMode,
                "ArduPilot Guided before Arm",
                cancellationToken);
            if (!mode.Accepted ||
                !await WaitForModeAsync(
                    systemId,
                    MavlinkValues.ArduPilotGuidedCustomMode,
                    requestedAt,
                    cancellationToken))
            {
                return MavlinkCommandDispatchResult.Rejected(
                    $"ArduPilot could not enter Guided mode before Arm from {telemetry.AirframeMode}. " +
                    "The vehicle must confirm Guided mode before it can be armed.");
            }
        }

        // ArduCopter's global movement commands are Guided-mode commands.  A
        // COMMAND_ACK for DO_REPOSITION/NAV_TAKEOFF only says that the
        // command reached the autopilot; it does not mean that ArduCopter has
        // admitted the vehicle into Guided.  Make that transition explicit so
        // the subsequent command cannot be silently ignored by another mode.
        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot &&
            RequiresArduPilotGuidedMode(request.Command) &&
            !string.Equals(telemetry.AirframeMode, "Guided", StringComparison.OrdinalIgnoreCase))
        {
            var requestedAt = DateTimeOffset.UtcNow;
            var mode = await SendModeAsync(
                systemId,
                MavlinkValues.ArduPilotGuidedCustomMode,
                "ArduPilot Guided",
                cancellationToken);
            if (!mode.Accepted || !await WaitForModeAsync(systemId, MavlinkValues.ArduPilotGuidedCustomMode, requestedAt, cancellationToken))
            {
                return MavlinkCommandDispatchResult.Rejected(
                    $"{adapter.DisplayName} could not enter Guided mode before {OperatorCommandLabel(request.Command)}. " +
                    "Verify the vehicle is armed/ready and review the latest PreArm message.");
            }
        }

        MavlinkCommandEnvelope command;
        try
        {
            command = adapter.BuildCommand(request, telemetry);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return MavlinkCommandDispatchResult.Rejected(ex.Message);
        }

        AddEvent(
            "Info",
            "mavlink-command",
            $"Dispatching {command.Description} for {request.Command} (MAV_CMD {command.CommandId}, {command.WireKind}) from {request.Reason}.",
            systemId,
            "MAVLINK_COMMAND_DISPATCH",
            request.CommandId);

        MavlinkCommandAck ack;
        try
        {
            ack = await SendCommandAsync(systemId, componentId, command, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryBeginActiveOperation(systemId, request, adapter, command, acknowledgementReceived: false, out _);
            AddEvent(
                "Warning",
                "mavlink-command",
                $"{command.Description} (MAV_CMD {command.CommandId}) timed out without an acknowledgement; telemetry outcome remains under observation.",
                systemId,
                "MAVLINK_COMMAND_TIMEOUT",
                request.CommandId);
            return new(
                false,
                OperationalCommandState.TimedOut,
                $"{command.Description} was sent, but no MAVLink acknowledgement arrived. Monitoring telemetry; the vehicle may or may not have acted. Check its current mode before retrying.",
                command.CommandId);
        }
        catch (TimeoutException)
        {
            TryBeginActiveOperation(systemId, request, adapter, command, acknowledgementReceived: false, out _);
            AddEvent(
                "Warning",
                "mavlink-command",
                $"{command.Description} (MAV_CMD {command.CommandId}) timed out without an acknowledgement; telemetry outcome remains under observation.",
                systemId,
                "MAVLINK_COMMAND_TIMEOUT",
                request.CommandId);
            return new(
                false,
                OperationalCommandState.TimedOut,
                $"{command.Description} was sent, but no MAVLink acknowledgement arrived. Monitoring telemetry; the vehicle may or may not have acted. Check its current mode before retrying.",
                command.CommandId);
        }
        catch (InvalidOperationException exception)
        {
            return MavlinkCommandDispatchResult.Rejected(exception.Message);
        }
        if (!ack.Accepted)
        {
            if (ack.Result == 255)
            {
                TryBeginActiveOperation(systemId, request, adapter, command, acknowledgementReceived: false, out _);
                AddEvent(
                    "Warning",
                    "mavlink-command",
                    $"{command.Description} (MAV_CMD {command.CommandId}) received no acknowledgement; telemetry outcome remains under observation.",
                    systemId,
                    "MAVLINK_COMMAND_NO_ACK",
                    request.CommandId);
                return new(
                    false,
                    OperationalCommandState.TimedOut,
                    $"{command.Description} was sent, but no MAVLink acknowledgement arrived. Monitoring telemetry; the vehicle may or may not have acted. Check its current mode before retrying.",
                    command.CommandId);
            }
            var message = adapter.Profile == MavlinkAutopilotProfile.ArduPilot
                ? await DescribeArduPilotCommandRejectionAsync(
                    systemId,
                    request.Command,
                    ack,
                    cancellationToken)
                : $"{command.Description} rejected by {adapter.DisplayName}: {ack.ResultName}.";
            if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot &&
                request.Command == OperatorCommandKind.Arm)
            {
                lock (_gate)
                {
                    if (_systems.TryGetValue(systemId, out var system) &&
                        LatestArduPilotCommandReason(system, DateTimeOffset.UtcNow) is null)
                    {
                        // A failed Arm ACK is authoritative evidence that
                        // readiness is not currently established. Keep that
                        // state visible until native evidence resolves it.
                        system.ActiveTextBlockers[$"Arm: {ack.ResultName}"] = DateTimeOffset.UtcNow;
                    }
                }
                RaiseChanged();
            }
            AddEvent(
                "Warning",
                "mavlink-command",
                $"{command.Description} (MAV_CMD {command.CommandId}) acknowledgement: {ack.ResultName}. {message}",
                systemId,
                "MAVLINK_COMMAND_REJECTED",
                request.CommandId);
            return new MavlinkCommandDispatchResult(
                false,
                OperationalCommandState.Rejected,
                message,
                command.CommandId);
        }

        AddEvent(
            "Info",
            "mavlink-command",
            $"{command.Description} (MAV_CMD {command.CommandId}) acknowledgement: {ack.ResultName}.",
            systemId,
            "MAVLINK_COMMAND_ACK",
            request.CommandId);

        var registered = TryBeginActiveOperation(
            systemId,
            request,
            adapter,
            command,
            acknowledgementReceived: true,
            out var immediate);
        if (!registered)
        {
            return MavlinkCommandDispatchResult.Rejected($"The {adapter.DisplayName} MAVLink unit disappeared after acknowledgement.");
        }

        return new MavlinkCommandDispatchResult(
            true,
            immediate ? OperationalCommandState.Succeeded : OperationalCommandState.InProgress,
            immediate
                ? $"{command.Description} acknowledged and confirmed by telemetry."
                : $"{command.Description} accepted by {adapter.DisplayName}; awaiting telemetry completion.",
            command.CommandId);
    }

    /// <summary>
    /// Sends one standard MAVLink camera or gimbal action immediately. This
    /// path never arms, changes flight mode, or bypasses vehicle safety checks.
    /// </summary>
    public async Task<MavlinkCommandDispatchResult> SendCameraActionAsync(
        string vehicleId,
        string? cameraSourceId,
        FlightMissionCameraAction action,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var vehicleComponentId, out _, out var adapter) ||
            adapter is null)
        {
            return MavlinkCommandDispatchResult.Rejected(
                $"The {ConfiguredAutopilotName} MAVLink vehicle is no longer available.");
        }

        MavlinkCommandEnvelope command;
        byte targetComponentId;
        byte? cameraComponentId = null;
        byte gimbalDeviceId = 0;
        var rollUnsupported = false;
        lock (_gate)
        {
            var camera = ResolveCamera(systemId, cameraSourceId);
            var gimbal = _gimbals.Values
                .Where(item => item.SystemId == systemId)
                .OrderByDescending(item => item.ManagerInformationReported)
                .ThenBy(item => item.ComponentId)
                .FirstOrDefault();
            var requiresCamera = action.Kind is
                FlightMissionCameraActionKind.PhotoOnce or
                FlightMissionCameraActionKind.PhotoByTime or
                FlightMissionCameraActionKind.PhotoByDistance or
                FlightMissionCameraActionKind.StopPhotos or
                FlightMissionCameraActionKind.StartVideo or
                FlightMissionCameraActionKind.StopVideo or
                FlightMissionCameraActionKind.CameraMode or
                FlightMissionCameraActionKind.CameraZoom;
            if (requiresCamera && camera is null)
            {
                return MavlinkCommandDispatchResult.Rejected(
                    "No MAVLink camera component has been discovered for this vehicle. Wait for camera discovery and try again.");
            }

            if (requiresCamera)
            {
                cameraComponentId = camera!.ComponentId;
            }
            if (action.Kind == FlightMissionCameraActionKind.Gimbal && gimbal is null)
            {
                return MavlinkCommandDispatchResult.Rejected(
                    "No MAVLink gimbal component has been discovered for this vehicle.");
            }

            if (action.Kind == FlightMissionCameraActionKind.Gimbal)
            {
                gimbalDeviceId = gimbal!.DeviceId;
            }

            rollUnsupported = action.Kind == FlightMissionCameraActionKind.Gimbal &&
                action.GimbalRollDegrees is not null && gimbal?.CapabilityFlagsReported == true &&
                (gimbal.CapabilityFlags & 4) == 0;
            if (rollUnsupported)
            {
                // The standard manager pitch/yaw message has no roll field;
                // preserve the requested pitch/yaw command and report the
                // unsupported axis to the operator.
                action = action with { GimbalRollDegrees = null };
            }

            try
            {
                command = MavlinkCameraActionMissionCompiler.CompileStandalone(action, adapter.Profile);
            }
            catch (InvalidOperationException exception)
            {
                return MavlinkCommandDispatchResult.Rejected(exception.Message);
            }

            targetComponentId = action.Kind switch
            {
                FlightMissionCameraActionKind.Gimbal when command.CommandId == MavlinkCommandIds.DoMountControl
                    => gimbal!.ComponentId,
                FlightMissionCameraActionKind.Gimbal => gimbal!.ManagerInformationReported
                    ? gimbal.ComponentId
                    : vehicleComponentId,
                FlightMissionCameraActionKind.RegionOfInterest => vehicleComponentId,
                _ => camera!.ComponentId
            };

            if (action.Kind == FlightMissionCameraActionKind.Gimbal &&
                command.CommandId == MavlinkCommandIds.DoGimbalManagerPitchYaw)
            {
                var parameters = command.Parameters.ToArray();
                parameters[6] = gimbalDeviceId;
                command = command with { Parameters = parameters };
            }
        }

        if (action.Kind == FlightMissionCameraActionKind.Gimbal)
        {
            try
            {
                var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
                var configure = new MavlinkCommandEnvelope(
                    MavlinkCommandIds.DoGimbalManagerConfigure,
                    [options.SourceSystemId, options.SourceComponentId, -1, -1, float.NaN, float.NaN, gimbalDeviceId],
                    "Acquire gimbal control");
                var configureAck = await SendCommandAsync(systemId, targetComponentId, configure, cancellationToken);
                if (!configureAck.Accepted)
                {
                    return new(false, OperationalCommandState.Rejected,
                        $"Gimbal control was not acquired ({configureAck.ResultCode}).");
                }

                var gimbalAcknowledgement = await SendCommandAsync(systemId, targetComponentId, command, cancellationToken);
                if (!gimbalAcknowledgement.Accepted)
                {
                    return new(false, OperationalCommandState.Rejected,
                        $"Gimbal setpoint was rejected by MAVLink ({gimbalAcknowledgement.ResultCode}).");
                }

                AddEvent(
                    "Info",
                    "mavlink-camera-command",
                    $"Dispatched gimbal setpoint to manager {targetComponentId}, device {gimbalDeviceId}.",
                    systemId,
                    "MAVLINK_GIMBAL_SETPOINT_DISPATCH",
                    action.Kind.ToString());
                var rollWarning = rollUnsupported ? "Requested roll is not supported by this gimbal; pitch and yaw were sent." : string.Empty;
                return new(true, OperationalCommandState.Succeeded,
                    rollWarning.Length == 0 ? "Gimbal setpoint acknowledged by MAVLink." : $"Gimbal setpoint acknowledged by MAVLink. {rollWarning}",
                    command.CommandId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                return MavlinkCommandDispatchResult.Rejected(exception.Message);
            }
        }

        MavlinkCommandAck acknowledgement;
        try
        {
            acknowledgement = await SendCommandAsync(systemId, targetComponentId, command, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, OperationalCommandState.TimedOut,
                $"{command.Description} was sent, but no MAVLink acknowledgement arrived.", command.CommandId);
        }
        catch (TimeoutException)
        {
            return new(false, OperationalCommandState.TimedOut,
                $"{command.Description} was sent, but no MAVLink acknowledgement arrived.", command.CommandId);
        }
        catch (InvalidOperationException exception)
        {
            return MavlinkCommandDispatchResult.Rejected(exception.Message);
        }

        if (!acknowledgement.Accepted)
        {
            return new(false, OperationalCommandState.Rejected,
                $"{command.Description} was rejected by MAVLink ({acknowledgement.ResultCode}).",
                command.CommandId);
        }

        AddEvent(
            "Info",
            "mavlink-camera-command",
            $"Dispatched {command.Description} (MAV_CMD {command.CommandId}) to component {targetComponentId}.",
            systemId,
            "MAVLINK_CAMERA_COMMAND_DISPATCH",
            command.CommandId.ToString(CultureInfo.InvariantCulture));
        if (action.Kind is (FlightMissionCameraActionKind.StartVideo or FlightMissionCameraActionKind.StopVideo) &&
            cameraComponentId is { } statusComponentId)
        {
            // CAMERA_CAPTURE_STATUS is the authoritative recording state. Ask
            // the camera for a fresh status after a video transition instead of
            // changing the UI from the operator's requested action.
            _ = RequestCameraCaptureStatusAsync(systemId, statusComponentId);
        }
        return new(true, OperationalCommandState.Succeeded,
            $"{command.Description} acknowledged by MAVLink.", command.CommandId);
    }

    private MavlinkCameraState? ResolveCamera(byte systemId, string? cameraSourceId)
    {
        if (!string.IsNullOrWhiteSpace(cameraSourceId))
        {
            var selected = _cameras.Values.FirstOrDefault(item =>
                item.SystemId == systemId &&
                CameraSourceId(item.SystemId, item.ComponentId).Equals(cameraSourceId, StringComparison.Ordinal));
            if (selected is not null)
            {
                return selected;
            }
        }

        return _cameras.Values
            .Where(item => item.SystemId == systemId)
            .OrderBy(item => item.ComponentId)
            .FirstOrDefault();
    }

    private bool TryBeginActiveOperation(
        byte systemId,
        OperatorCommandRequest request,
        IMavlinkAutopilotAdapter adapter,
        MavlinkCommandEnvelope command,
        bool acknowledgementReceived,
        out bool immediate)
    {
        immediate = false;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system))
            {
                return false;
            }

            if (system.ActiveOperation is { } previous)
            {
                UpdateCommand(
                    previous.CommandId,
                    OperationalCommandState.Cancelled,
                    $"Replaced by {request.Command}.",
                    "OPERATOR_OVERRIDE");
            }

            // A COMMAND_ACK proves dispatch acceptance, while a missing ACK
            // leaves the outcome uncertain. In both cases, keep monitoring the
            // authoritative telemetry so an operation can become confirmed
            // without misreporting a timeout as an explicit rejection.
            var startedAt = DateTimeOffset.UtcNow;
            var operation = new ActiveOperation(
                request.CommandId,
                request.Command,
                request.Parameters ?? OperatorCommandParameters.None,
                startedAt,
                Armed(system),
                DateTimeOffset.UtcNow.Add(
                    request.Command is OperatorCommandKind.Recover or OperatorCommandKind.Land
                        ? TimeSpan.FromMinutes(3)
                        : TimeSpan.FromMinutes(1)),
                command.Parameters.Length > 6 && float.IsFinite(command.Parameters[6])
                    ? command.Parameters[6]
                    : null,
                OperationTargetHeading(adapter, request, ToTelemetry(system), command),
                adapter.Profile == MavlinkAutopilotProfile.ArduPilot && request.Command == OperatorCommandKind.Hold
                    ? new HoldStabilityState()
                    : null,
                acknowledgementReceived);
            system.ActiveOperation = operation;
            if (OperationCompleted(system, operation))
            {
                system.ActiveOperation = null;
                immediate = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Acquires the selected autopilot's MAVLink manual-input path. The adapter
    /// decides whether a mode transition is required; this never enters
    /// Offboard mode.
    /// </summary>
    public async Task<MavlinkManualControlDispatchResult> BeginManualControlAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out var telemetry, out var adapter) ||
            telemetry is null || adapter is null || adapter.ClassifyManualControlMode(telemetry.AirframeMode) == ManualControlModeClass.Unsupported ||
            telemetry.State is AvailabilityState.Offline or AvailabilityState.Reconnecting)
        {
            return MavlinkManualControlDispatchResult.Rejected(
                $"Manual control requires an available {ConfiguredAutopilotName} multicopter with a compatible mode and a known MAVLink route.");
        }

        if (adapter.Profile == MavlinkAutopilotProfile.Px4)
        {
            var inputMode = await ReadPx4ManualInputModeAsync(systemId, componentId, cancellationToken);
            if (inputMode is 0 or 4)
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    inputMode == 0
                        ? "PX4 is configured for RC-only manual input (COM_RC_IN_MODE=0). Configure PX4 to accept MAVLink manual input before taking control."
                        : "PX4 manual input is disabled (COM_RC_IN_MODE=4). Configure PX4 to accept MAVLink manual input before taking control.");
            }

            // PX4 only starts accepting MANUAL_CONTROL after it has seen a real
            // stream.  A single neutral packet followed by SET_MODE was enough
            // to make Robot Command look active while PX4 silently ignored the
            // controller.  Prime the stream, request Position mode, then wait
            // for PX4's heartbeat to confirm MAVLink manual input is enabled.
            var activation = await ActivatePx4ManualControlAsync(systemId, cancellationToken);
            if (!activation.Accepted)
            {
                return activation;
            }

            var modeDetail = inputMode == 3
                ? " COM_RC_IN_MODE=3 accepts the first valid manual-input source; restart PX4 if another source has already claimed it."
                : string.Empty;
            return MavlinkManualControlDispatchResult.Success($"PX4 MAVLink manual input is active.{modeDetail}");
        }

        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot)
        {
            var modeClass = adapter.ClassifyManualControlMode(telemetry.AirframeMode);
            if (modeClass == ManualControlModeClass.PositionAssisted && !HasValidGlobalPosition(systemId))
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    $"ArduPilot manual control in {telemetry.AirframeMode} requires a current valid global position. Use Stabilize, Acro, AltHold, or Sport for GPS-independent stick control.");
            }

            var admission = await CheckArduPilotManualInputAsync(systemId, componentId, cancellationToken);
            if (!admission.Accepted)
            {
                return MavlinkManualControlDispatchResult.Rejected(admission.Message);
            }

            var session = new ArduPilotManualSession(
                ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow),
                new ManualControlProfile(),
                CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation?.Token ?? CancellationToken.None));
            if (!_arduPilotManualSessions.TryAdd(systemId, session))
            {
                session.Dispose();
                return MavlinkManualControlDispatchResult.Rejected("ArduPilot manual input is already active for this vehicle.");
            }

            session.Pump = Task.Run(() => RunArduPilotManualPumpAsync(systemId, session), session.Cancellation.Token);
            try
            {
                // ArduPilot accepts MANUAL_CONTROL as pilot input, but it can
                // ignore the stream when the source/system configuration is
                // incompatible. Keep a real neutral stream running before
                // reporting activation and let the mode/parameter checks above
                // provide the authoritative admission evidence.
                await Task.Delay(TimeSpan.FromMilliseconds(550), cancellationToken);
                if (!string.IsNullOrWhiteSpace(session.Failure))
                {
                    _arduPilotManualSessions.TryRemove(systemId, out _);
                    return await StopArduPilotManualControlAsync(systemId, session,
                        $"ArduPilot manual input stream failed: {session.Failure}", cancellationToken);
                }

                var verification = admission.Verified
                    ? "ArduPilot MAVLink manual input is active; MAV_GCS_SYSID, MAV_OPTIONS, and RC_OPTIONS permit pilot input."
                    : "ArduPilot MAVLink manual input is active; source-parameter verification was unavailable, so confirm MAV_GCS_SYSID/MAV_OPTIONS/RC_OPTIONS on the vehicle.";
                session.ParameterAdmissionVerified = admission.Verified;
                return MavlinkManualControlDispatchResult.Success(verification);
            }
            catch (OperationCanceledException)
            {
                _arduPilotManualSessions.TryRemove(systemId, out _);
                await StopArduPilotManualControlAsync(systemId, session, "ArduPilot manual input activation was cancelled.", CancellationToken.None);
                throw;
            }
        }

        // Supply a neutral frame first. A centred MAVLink throttle is 500, not zero.
        var neutral = await SendManualControlFrameAsync(systemId, ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow), new ManualControlProfile(), cancellationToken);
        return neutral.Accepted
            ? MavlinkManualControlDispatchResult.Success("ArduPilot MAVLink manual input is active.")
            : neutral;
    }

    public bool TryGetManualControlStatus(string vehicleId, out MavlinkManualControlStatus status)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out var telemetry, out var adapter) ||
            telemetry is null || adapter is null)
        {
            status = null!;
            return false;
        }

        SystemState? system = null;
        lock (_gate) _systems.TryGetValue(systemId, out system);
        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot &&
            _arduPilotManualSessions.TryGetValue(systemId, out var session))
        {
            var elapsed = Math.Max(0.001, (DateTimeOffset.UtcNow - session.StartedAt).TotalSeconds);
            status = new(
                telemetry.AirframeMode,
                adapter.ClassifyManualControlMode(telemetry.AirframeMode),
                session.ParameterAdmissionVerified,
                session.SentSamples / elapsed,
                session.LastSentAt,
                system?.ManualInputEchoAt is { } activeEcho && DateTimeOffset.UtcNow - activeEcho <= TimeSpan.FromSeconds(2),
                system?.ManualInputEchoAt,
                session.SafeReleaseMode,
                session.SafeReleaseConfirmed,
                session.Failure);
            return true;
        }

        status = new(telemetry.AirframeMode, adapter.ClassifyManualControlMode(telemetry.AirframeMode),
            true, 0, null,
            system?.ManualInputEchoAt is { } idleEcho && DateTimeOffset.UtcNow - idleEcho <= TimeSpan.FromSeconds(2),
            system?.ManualInputEchoAt,
            null, null, null);
        return true;
    }

    /// <summary>
    /// Streams an assisted Mode 2 setpoint through MAVLink MANUAL_CONTROL.
    /// Values are scaled from the Robot Command profile to MAVLink's
    /// normalized [-1000, 1000] axis range.
    /// </summary>
    public async Task<MavlinkManualControlDispatchResult> SendManualControlAsync(
        string vehicleId,
        ManualControlSetpoint setpoint,
        ManualControlProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out var telemetry, out var adapter) ||
            telemetry is null || adapter is null || adapter.ClassifyManualControlMode(telemetry.AirframeMode) == ManualControlModeClass.Unsupported ||
            telemetry.State is AvailabilityState.Offline or AvailabilityState.Reconnecting)
        {
            return MavlinkManualControlDispatchResult.Rejected(
                $"{ConfiguredAutopilotName} manual control stopped because the MAVLink route is unavailable or the current mode is incompatible.");
        }

        // Sending a MANUAL_CONTROL frame only proves that it left this process.
        // PX4 exposes the authoritative admission result in HEARTBEAT.  Refuse
        // to keep a controller session apparently active after PX4 has dropped
        // the input source or changed to a mode that no longer accepts it.
        if (adapter.Profile == MavlinkAutopilotProfile.Px4 && !HasPx4ManualInputEnabled(systemId))
        {
            return MavlinkManualControlDispatchResult.Rejected(
                "PX4 manual input is no longer enabled. Release LB, confirm PX4 is in Position mode and accepting MAVLink manual input, then engage it again.");
        }

        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot)
        {
            if (!_arduPilotManualSessions.TryGetValue(systemId, out var session))
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    "ArduPilot manual input is not active. Engage manual control with the deadman button first.");
            }

            if (!string.IsNullOrWhiteSpace(session.Failure))
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    $"ArduPilot manual input stopped: {session.Failure}");
            }

            if (adapter.ClassifyManualControlMode(telemetry.AirframeMode) == ManualControlModeClass.PositionAssisted &&
                !HasValidGlobalPosition(systemId))
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    $"ArduPilot left position readiness for {telemetry.AirframeMode}; manual input was stopped.");
            }

            session.Setpoint = setpoint;
            session.Profile = profile;
            return string.IsNullOrWhiteSpace(session.Failure)
                ? MavlinkManualControlDispatchResult.Success("ArduPilot manual input accepted by the 20 Hz stream.")
                : MavlinkManualControlDispatchResult.Rejected($"ArduPilot manual input stream failed: {session.Failure}");
        }

        return await SendManualControlFrameAsync(systemId, setpoint, profile, cancellationToken);
    }

    /// <summary>
    /// Starts a PX4 Offboard formation session. The connection owns a 20 Hz
    /// setpoint pump so UI and formation-controller scheduling cannot starve
    /// PX4's proof-of-life stream.
    /// </summary>
    public async Task<Px4FormationControlResult> BeginFormationControlAsync(
        string vehicleId,
        string lockId,
        Px4FormationSetpoint setpoint,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out var telemetry, out var adapter) ||
            adapter?.Profile != MavlinkAutopilotProfile.Px4 || telemetry is null ||
            telemetry.State is not AvailabilityState.Online || telemetry.IsStale || !telemetry.Armed ||
            string.Equals(telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase))
        {
            return Px4FormationControlResult.Rejected("PX4 formation control requires an armed, airborne PX4 multicopter with current telemetry.");
        }

        Px4FormationSession? existing = null;
        lock (_gate)
        {
            if (_formationSessions.TryGetValue(systemId, out existing) && !string.Equals(existing.LockId, lockId, StringComparison.Ordinal))
            {
                return Px4FormationControlResult.Rejected("PX4 is already controlled by another formation session.");
            }
        }
        if (existing is not null)
        {
            existing.Setpoint = setpoint;
            return Px4FormationControlResult.Active("PX4 formation Offboard session is already active.", existing.LastSetpointAt);
        }

        var session = new Px4FormationSession(lockId, setpoint, CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation?.Token ?? CancellationToken.None));
        if (!_formationSessions.TryAdd(systemId, session))
        {
            session.Dispose();
            return Px4FormationControlResult.Rejected("PX4 formation session could not be created.");
        }

        session.Pump = Task.Run(() => RunFormationPumpAsync(systemId, session), session.Cancellation.Token);
        try
        {
            // PX4 requires an established setpoint stream before it accepts
            // Offboard. The pump sends a current-position hold target every
            // 50 ms while this bounded pre-stream delay elapses.
            await Task.Delay(TimeSpan.FromSeconds(1.15), cancellationToken);
            var requestedAt = DateTimeOffset.UtcNow;
            var mode = await SendModeAsync(systemId, MavlinkValues.Px4OffboardCustomMode, "PX4 Offboard", cancellationToken);
            if (!mode.Accepted || !await WaitForModeAsync(systemId, MavlinkValues.Px4OffboardCustomMode, requestedAt, cancellationToken))
            {
                await StopFormationControlAsync(vehicleId, lockId, "PX4 did not confirm Offboard mode.", CancellationToken.None);
                return Px4FormationControlResult.Rejected("PX4 did not confirm Offboard mode after the required setpoint pre-stream.");
            }

            session.OffboardConfirmed = true;
            return Px4FormationControlResult.Active("PX4 Offboard formation control is active.", session.LastSetpointAt);
        }
        catch (OperationCanceledException)
        {
            await StopFormationControlAsync(vehicleId, lockId, "PX4 formation activation was cancelled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await StopFormationControlAsync(vehicleId, lockId, "PX4 formation activation failed.", CancellationToken.None);
            return Px4FormationControlResult.Rejected($"PX4 formation activation failed: {ex.Message}");
        }
    }

    public Task<Px4FormationControlResult> UpdateFormationControlAsync(
        string vehicleId,
        string lockId,
        Px4FormationSetpoint setpoint,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out _, out var adapter) || adapter?.Profile != MavlinkAutopilotProfile.Px4 ||
            !_formationSessions.TryGetValue(systemId, out var session) || !string.Equals(session.LockId, lockId, StringComparison.Ordinal))
        {
            return Task.FromResult(Px4FormationControlResult.Rejected("PX4 is not in the requested formation Offboard session."));
        }
        if (!string.IsNullOrWhiteSpace(session.Failure))
        {
            return Task.FromResult(Px4FormationControlResult.Rejected($"PX4 Offboard setpoint stream failed: {session.Failure}"));
        }
        session.Setpoint = setpoint;
        return Task.FromResult(session.OffboardConfirmed
            ? Px4FormationControlResult.Active("PX4 Offboard target updated.", session.LastSetpointAt)
            : new Px4FormationControlResult(true, "Pre-streaming", "PX4 formation setpoints are pre-streaming.", session.LastSetpointAt));
    }

    public async Task<Px4FormationControlResult> StopFormationControlAsync(
        string vehicleId,
        string lockId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out _, out var adapter) || adapter?.Profile != MavlinkAutopilotProfile.Px4 ||
            !_formationSessions.TryGetValue(systemId, out var session) || !string.Equals(session.LockId, lockId, StringComparison.Ordinal) ||
            !_formationSessions.TryRemove(systemId, out session))
        {
            return Px4FormationControlResult.Rejected("PX4 formation session is no longer active.");
        }

        try
        {
            // Keep the pump alive while PX4 transitions away from Offboard;
            // stopping first would intentionally trigger its configured
            // Offboard-loss failsafe rather than our explicit Hold command.
            var requestedAt = DateTimeOffset.UtcNow;
            var hold = await SendModeAsync(systemId, MavlinkValues.Px4AutoLoiterCustomMode, "PX4 Hold", cancellationToken);
            if (hold.Accepted)
            {
                await WaitForModeAsync(systemId, MavlinkValues.Px4AutoLoiterCustomMode, requestedAt, cancellationToken);
            }
            return new Px4FormationControlResult(hold.Accepted, "Hold", hold.Accepted ? reason : $"{reason} PX4 Hold request could not be sent.", session.LastSetpointAt);
        }
        finally
        {
            session.Cancellation.Cancel();
            try { if (session.Pump is not null) await session.Pump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
            catch { /* The connection is being released; no setpoint pump may survive it. */ }
            session.Dispose();
        }
    }

    /// <summary>
    /// Starts an ArduCopter Guided formation session. ArduPilot does not use
    /// PX4 Offboard mode here: it receives a continuously refreshed global
    /// relative-altitude position/velocity target while Guided is active.
    /// </summary>
    public async Task<ArduPilotFormationControlResult> BeginArduPilotFormationControlAsync(
        string vehicleId,
        string lockId,
        ArduPilotFormationSetpoint setpoint,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out var telemetry, out var adapter) ||
            adapter?.Profile != MavlinkAutopilotProfile.ArduPilot || telemetry is null ||
            telemetry.State is not AvailabilityState.Online || telemetry.IsStale || !telemetry.Armed ||
            string.Equals(telemetry.LandedState, "Landed", StringComparison.OrdinalIgnoreCase) ||
            telemetry.LatitudeDegrees is null || telemetry.LongitudeDegrees is null ||
            telemetry.AltitudeAglMetres is null)
        {
            return ArduPilotFormationControlResult.Rejected(
                "ArduPilot formation control requires an armed, airborne ArduCopter with current global position telemetry.");
        }

        if (!double.IsFinite(setpoint.LatitudeDegrees) || !double.IsFinite(setpoint.LongitudeDegrees) ||
            !double.IsFinite(setpoint.AltitudeRelativeMetres))
        {
            return ArduPilotFormationControlResult.Rejected("ArduPilot formation target contains a non-finite position or altitude.");
        }

        if (_arduPilotManualSessions.ContainsKey(systemId))
        {
            return ArduPilotFormationControlResult.Rejected(
                "ArduPilot is currently owned by a manual-control session; release manual control before locking formation.");
        }

        if (_arduPilotFormationSessions.TryGetValue(systemId, out var existing))
        {
            if (!string.Equals(existing.LockId, lockId, StringComparison.Ordinal))
                return ArduPilotFormationControlResult.Rejected("ArduPilot is already controlled by another formation session.");

            existing.Setpoint = setpoint;
            return ArduPilotFormationControlResult.Active("ArduPilot Guided formation session is already active.", existing.LastSetpointAt);
        }

        var session = new ArduPilotFormationSession(
            lockId,
            setpoint,
            CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation?.Token ?? CancellationToken.None));
        if (!_arduPilotFormationSessions.TryAdd(systemId, session))
        {
            session.Dispose();
            return ArduPilotFormationControlResult.Rejected("ArduPilot formation session could not be created.");
        }

        session.Pump = Task.Run(() => RunArduPilotFormationPumpAsync(systemId, session), session.Cancellation.Token);
        try
        {
            // Guided does not require PX4-style Offboard proof-of-life, but a
            // short pre-stream makes the first mode transition deterministic
            // and ensures the controller has a current target immediately.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            if (!string.IsNullOrWhiteSpace(session.Failure))
            {
                await StopArduPilotFormationControlAsync(vehicleId, lockId,
                    "ArduPilot Guided formation stream failed during activation.", CancellationToken.None);
                return ArduPilotFormationControlResult.Rejected(session.Failure);
            }

            var requestedAt = DateTimeOffset.UtcNow;
            var mode = await SendModeAsync(systemId, MavlinkValues.ArduPilotGuidedCustomMode, "ArduPilot Guided formation", cancellationToken);
            if (!mode.Accepted || !await WaitForModeAsync(systemId, MavlinkValues.ArduPilotGuidedCustomMode, requestedAt, cancellationToken))
            {
                await StopArduPilotFormationControlAsync(vehicleId, lockId,
                    "ArduPilot did not confirm Guided mode.", CancellationToken.None);
                return ArduPilotFormationControlResult.Rejected(
                    "ArduPilot did not confirm Guided mode for formation control.");
            }

            session.GuidedConfirmed = true;
            return ArduPilotFormationControlResult.Active(
                "ArduPilot Guided formation control is active.", session.LastSetpointAt);
        }
        catch (OperationCanceledException)
        {
            await StopArduPilotFormationControlAsync(vehicleId, lockId,
                "ArduPilot formation activation was cancelled.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await StopArduPilotFormationControlAsync(vehicleId, lockId,
                "ArduPilot formation activation failed.", CancellationToken.None);
            return ArduPilotFormationControlResult.Rejected($"ArduPilot formation activation failed: {ex.Message}");
        }
    }

    public Task<ArduPilotFormationControlResult> UpdateArduPilotFormationControlAsync(
        string vehicleId,
        string lockId,
        ArduPilotFormationSetpoint setpoint,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out _, out var adapter) ||
            adapter?.Profile != MavlinkAutopilotProfile.ArduPilot ||
            !_arduPilotFormationSessions.TryGetValue(systemId, out var session) ||
            !string.Equals(session.LockId, lockId, StringComparison.Ordinal))
        {
            return Task.FromResult(ArduPilotFormationControlResult.Rejected(
                "ArduPilot is not in the requested Guided formation session."));
        }

        if (!string.IsNullOrWhiteSpace(session.Failure))
        {
            return Task.FromResult(ArduPilotFormationControlResult.Rejected(
                $"ArduPilot Guided formation stream failed: {session.Failure}"));
        }

        session.Setpoint = setpoint;
        return Task.FromResult(session.GuidedConfirmed
            ? ArduPilotFormationControlResult.Active("ArduPilot Guided formation target updated.", session.LastSetpointAt)
            : new ArduPilotFormationControlResult(true, "Pre-streaming",
                "ArduPilot formation setpoints are pre-streaming.", session.LastSetpointAt));
    }

    public async Task<ArduPilotFormationControlResult> StopArduPilotFormationControlAsync(
        string vehicleId,
        string lockId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out _, out _, out var adapter) ||
            adapter?.Profile != MavlinkAutopilotProfile.ArduPilot ||
            !_arduPilotFormationSessions.TryGetValue(systemId, out var session) ||
            !string.Equals(session.LockId, lockId, StringComparison.Ordinal))
        {
            return ArduPilotFormationControlResult.Rejected("ArduPilot formation session is no longer active.");
        }

        var holdConfirmed = false;
        try
        {
            // Keep the Guided stream alive while Brake is selected. This
            // avoids leaving the vehicle dependent on a timeout/failsafe for
            // its transition out of Guided.
            var requestedAt = DateTimeOffset.UtcNow;
            var hold = await SendModeAsync(systemId, MavlinkValues.ArduPilotBrakeCustomMode,
                "ArduPilot Brake", cancellationToken);
            if (hold.Accepted && await WaitForModeAsync(systemId, MavlinkValues.ArduPilotBrakeCustomMode, requestedAt, cancellationToken))
            {
                holdConfirmed = await WaitForStableHoldAsync(
                    systemId,
                    adapter.HoldPolicy,
                    requestedAt,
                    cancellationToken);
            }

            var message = holdConfirmed
                ? reason
                : $"{reason} ArduPilot Brake did not confirm stable position and altitude.";
            return new(holdConfirmed, holdConfirmed ? "Holding" : "Failed", message, session.LastSetpointAt);
        }
        finally
        {
            _arduPilotFormationSessions.TryRemove(systemId, out _);
            session.Cancellation.Cancel();
            try { if (session.Pump is not null) await session.Pump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
            catch { /* Connection teardown must not leave a Guided pump alive. */ }
            session.Dispose();
        }
    }

    private async Task RunArduPilotFormationPumpAsync(byte systemId, ArduPilotFormationSession session)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(session.Cancellation.Token))
            {
                MavlinkTransportRoute? route;
                byte componentId;
                lock (_gate)
                {
                    if (!_systems.TryGetValue(systemId, out var system) || system.Route is null ||
                        system.Availability is AvailabilityState.Stale or AvailabilityState.Offline ||
                        AdapterFor(system)?.Profile != MavlinkAutopilotProfile.ArduPilot)
                    {
                        session.Failure = "ArduPilot telemetry or MAVLink route became unavailable.";
                        return;
                    }

                    route = system.Route;
                    componentId = system.ComponentId;
                }

                var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
                // Use position and velocity; ignore acceleration, yaw, and
                // yaw-rate so ArduPilot may choose its own vehicle heading.
                const ushort typeMask = 1 << 6 | 1 << 7 | 1 << 8 | 1 << 9 | 1 << 10 | 1 << 11;
                var target = session.Setpoint;
                await _transport.SendAsync(_codec.EncodeSetPositionTargetGlobalInt(
                    options.SourceSystemId,
                    options.SourceComponentId,
                    systemId,
                    componentId,
                    unchecked((uint)Environment.TickCount64),
                    MavlinkValues.MavFrameGlobalRelativeAltInt,
                    typeMask,
                    checked((int)Math.Round(target.LatitudeDegrees * 10_000_000d)),
                    checked((int)Math.Round(target.LongitudeDegrees * 10_000_000d)),
                    (float)target.AltitudeRelativeMetres,
                    target.VelocityNorthMetresPerSecond * FormationVelocityFeedForwardGain,
                    target.VelocityEastMetresPerSecond * FormationVelocityFeedForwardGain,
                    target.VelocityDownMetresPerSecond * FormationVelocityFeedForwardGain),
                    route,
                    session.Cancellation.Token);
                session.LastSetpointAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            session.Failure = ex.Message;
            _logger.LogWarning(ex,
                "ArduPilot Guided formation stream failed for {ConnectionId}/{SystemId}",
                Definition.Id,
                systemId);
        }
    }

    public bool TryGetFormationReference(string vehicleId, out float north, out float east, out float down, out string? error)
    {
        lock (_gate)
        {
            var system = _systems.Values.FirstOrDefault(item => VehicleId(item.SystemId).Equals(vehicleId, StringComparison.Ordinal));
            if (system is null || system.LocalNorth is null || system.LocalEast is null || system.LocalDown is null)
            {
                north = east = down = 0;
                error = "PX4 has not reported LOCAL_POSITION_NED required for Offboard formation control.";
                return false;
            }
            north = (float)system.LocalNorth.Value;
            east = (float)system.LocalEast.Value;
            down = (float)system.LocalDown.Value;
            error = null;
            return true;
        }
    }

    private async Task RunFormationPumpAsync(byte systemId, Px4FormationSession session)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(session.Cancellation.Token))
            {
                MavlinkTransportRoute? route;
                byte componentId;
                lock (_gate)
                {
                    if (!_systems.TryGetValue(systemId, out var system) || system.Route is null ||
                        system.Availability is AvailabilityState.Stale or AvailabilityState.Offline)
                    {
                        session.Failure = "PX4 telemetry or MAVLink route became unavailable.";
                        return;
                    }
                    route = system.Route;
                    componentId = system.ComponentId;
                }
                var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
                // Enable position and velocity. Ignore acceleration, yaw and
                // yaw-rate: PX4 is free to orient itself for its own flight
                // dynamics while it follows the common formation trajectory.
                const ushort typeMask = 1 << 6 | 1 << 7 | 1 << 8 | 1 << 9 | 1 << 10 | 1 << 11;
                var target = session.Setpoint;
                await _transport.SendAsync(_codec.EncodeSetPositionTargetLocalNed(
                    options.SourceSystemId, options.SourceComponentId, systemId, componentId,
                    unchecked((uint)Environment.TickCount64), 1, typeMask,
                    target.NorthMetres, target.EastMetres, target.DownMetres,
                    target.VelocityNorthMetresPerSecond * FormationVelocityFeedForwardGain,
                    target.VelocityEastMetresPerSecond * FormationVelocityFeedForwardGain,
                    target.VelocityDownMetresPerSecond * FormationVelocityFeedForwardGain), route, session.Cancellation.Token);
                session.LastSetpointAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            session.Failure = ex.Message;
            _logger.LogWarning(ex, "PX4 formation Offboard setpoint stream failed for {ConnectionId}/{SystemId}", Definition.Id, systemId);
        }
    }

    /// <summary>Places the target back into autopilot Hold before releasing a manual-control lease.</summary>
    public async Task<MavlinkManualControlDispatchResult> EndManualControlAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVehicle(vehicleId, out var systemId, out var componentId, out _, out _))
        {
            return MavlinkManualControlDispatchResult.Rejected($"The {ConfiguredAutopilotName} MAVLink unit is no longer available.");
        }

        if (ConfiguredAutopilotName == "ArduPilot" && _arduPilotManualSessions.TryRemove(systemId, out var arduSession))
        {
            return await StopArduPilotManualControlAsync(systemId, arduSession,
                "ArduPilot manual control released.", cancellationToken);
        }

        return await SendModeAsync(systemId, MavlinkValues.Px4AutoLoiterCustomMode,
            $"{ConfiguredAutopilotName} Hold", cancellationToken);
    }

    private async Task<int?> ReadPx4ManualInputModeAsync(
        byte systemId,
        byte componentId,
        CancellationToken cancellationToken)
    {
        const string parameterName = "COM_RC_IN_MODE";
        const byte parameterComponentId = MavlinkValues.MavCompIdAutopilot1;
        SystemState system;
        MavlinkTransportRoute route;
        TaskCompletionSource<MavlinkParameterValue>? waiter = null;
        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();

        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out system!) || system.Route is not { } knownRoute)
            {
                return null;
            }

            if (system.Parameters.TryGetValue((parameterComponentId, parameterName), out var cached))
            {
                return (int)Math.Round(cached.Value, MidpointRounding.AwayFromZero);
            }

            route = knownRoute;
            waiter = new TaskCompletionSource<MavlinkParameterValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            system.ParameterWaiters[(parameterComponentId, parameterName)] = waiter;
        }

        try
        {
            // A parameter request is advisory: PX4 may not expose parameters on
            // a constrained link.  The heartbeat activation check below remains
            // authoritative, so an absent reply must not be mistaken for a
            // configuration value or make the session hang indefinitely.
            await _transport.SendAsync(
                _codec.EncodeParameterRequestRead(
                    options.SourceSystemId,
                    options.SourceComponentId,
                    systemId,
                    componentId,
                    parameterName,
                    -1),
                route,
                cancellationToken);
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(750, cancellationToken));
            return completed == waiter.Task
                ? (int)Math.Round((await waiter.Task).Value, MidpointRounding.AwayFromZero)
                : null;
        }
        finally
        {
            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var current) &&
                    current.ParameterWaiters.TryGetValue((parameterComponentId, parameterName), out var active) &&
                    ReferenceEquals(active, waiter))
                {
                    current.ParameterWaiters.Remove((parameterComponentId, parameterName));
                }
            }
        }
    }

    private bool HasPx4ManualInputEnabled(byte systemId)
    {
        lock (_gate)
        {
            return _systems.TryGetValue(systemId, out var system) &&
                   (system.BaseMode & MavlinkValues.MavModeFlagManualInputEnabled) != 0 &&
                   AdapterFor(system)?.DecodeMode(system.CustomMode) == "Position";
        }
    }

    private async Task<MavlinkManualControlDispatchResult> ActivatePx4ManualControlAsync(
        byte systemId,
        CancellationToken cancellationToken)
    {
        var profile = new ManualControlProfile();
        var neutral = ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow);

        // Give PX4 a short, real input stream before requesting the assisted
        // position mode.  The frequency is deliberately below the normal 60 Hz
        // controller loop but above PX4's input timeout.
        for (var frame = 0; frame < 6; frame++)
        {
            var sent = await SendManualControlFrameAsync(systemId, neutral with { Timestamp = DateTimeOffset.UtcNow }, profile, cancellationToken);
            if (!sent.Accepted)
            {
                return sent;
            }
            await Task.Delay(50, cancellationToken);
        }

        var mode = await SendModeAsync(systemId, MavlinkValues.Px4PositionCustomMode, "PX4 Position control", cancellationToken);
        if (!mode.Accepted)
        {
            return mode;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var sent = await SendManualControlFrameAsync(systemId, neutral with { Timestamp = DateTimeOffset.UtcNow }, profile, cancellationToken);
            if (!sent.Accepted)
            {
                return sent;
            }

            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var system) &&
                    (system.BaseMode & MavlinkValues.MavModeFlagManualInputEnabled) != 0 &&
                    AdapterFor(system)?.DecodeMode(system.CustomMode) == "Position")
                {
                    return MavlinkManualControlDispatchResult.Success("PX4 accepted MAVLink manual input in Position mode.");
                }
            }
            await Task.Delay(100, cancellationToken);
        }

        return MavlinkManualControlDispatchResult.Rejected(
            "PX4 did not enable MAVLink manual input after the control stream and Position-mode request. " +
            "Check COM_RC_IN_MODE and restart PX4 if another RC or MAVLink source has claimed manual-input priority.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.ChunkReceived -= OnChunkReceived;
        _transport.Faulted -= OnTransportFaulted;
        try
        {
            await DisconnectAsync();
        }
        finally
        {
            _registry.Unregister(this);
            await _transport.DisposeAsync();
            _lifecycleGate.Dispose();
        }
    }

    private async Task StopSessionAsync(bool clearSystems)
    {
        foreach (var pending in _pendingAcks.Values)
        {
            pending.TrySetCanceled();
        }

        _pendingAcks.Clear();
        _ackQuarantine.Clear();

        foreach (var (systemId, manualSession) in _arduPilotManualSessions.ToArray())
        {
            if (_arduPilotManualSessions.TryRemove(systemId, out var removed))
            {
                removed.Cancellation.Cancel();
                try { if (removed.Pump is not null) await removed.Pump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
                catch { /* Transport teardown must not leave manual input running. */ }
                removed.Dispose();
            }
        }

        foreach (var (_, formationSession) in _formationSessions.ToArray())
        {
            formationSession.Cancellation.Cancel();
            try
            {
                if (formationSession.Pump is not null)
                    await formationSession.Pump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            catch { /* Transport teardown must not leave an Offboard pump alive. */ }
            formationSession.Dispose();
        }
        _formationSessions.Clear();

        foreach (var (_, formationSession) in _arduPilotFormationSessions.ToArray())
        {
            formationSession.Cancellation.Cancel();
            try
            {
                if (formationSession.Pump is not null)
                    await formationSession.Pump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            catch { /* Transport teardown must not leave a Guided pump alive. */ }
            formationSession.Dispose();
        }
        _arduPilotFormationSessions.Clear();
        var sessionCancellation = _sessionCancellation;
        var heartbeatTask = _heartbeatTask;
        _sessionCancellation = null;
        _heartbeatTask = null;
        _firstHeartbeat = null;

        sessionCancellation?.Cancel();
        await _transport.CloseAsync(CancellationToken.None);
        if (heartbeatTask is not null)
        {
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (sessionCancellation?.IsCancellationRequested == true)
            {
            }
        }
        sessionCancellation?.Dispose();

        if (clearSystems)
        {
            lock (_gate)
            {
                _systems.Clear();
                _cameras.Clear();
                _gimbals.Clear();
                _cameraInformationRequested.Clear();
                _gimbalInformationRequested.Clear();
            }
            if (_cameraDefinitions is not null)
            {
                foreach (var definition in _cameraDefinitions.Items
                             .Where(item => item.ConnectionId == Definition.Id)
                             .ToArray())
                {
                    _cameraDefinitions.Remove(definition.Id);
                }
            }
        }
    }

    // Transport chunks are raised in receive order. Process them synchronously so
    // ordered MAVLink exchanges (notably mission request/item/ack) cannot race
    // each other through independent async-void continuations.
    private void OnChunkReceived(object? sender, MavlinkTransportChunk chunk)
    {
        try
        {
            if (!_codec.TryDecode(chunk.Payload.Span, chunk.ReceivedAt, out var packet, out var error) ||
                packet is null)
            {
                if (_transport is SerialMavlinkTransport serial) serial.RecordDecodeError();
                Interlocked.Increment(ref _decodeErrors);
                if (!_missionTransfers.IsEmpty)
                {
                    _logger.LogInformation(
                        "Discarded MAVLink frame during an active mission transfer: {Reason}; bytes={Bytes}",
                        error,
                        Convert.ToHexString(chunk.Payload.Span));
                }
                _logger.LogDebug("Discarded MAVLink frame: {Reason}", error);
                return;
            }

            if (packet.ProtocolVersion >= 2) Interlocked.Increment(ref _mavlinkTwoFrames);
            else Interlocked.Increment(ref _mavlinkOneFrames);

            ProcessPacket(packet, chunk.Route);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "MAVLink frame processing failed for {ConnectionId}", Definition.Id);
            RaiseChanged();
        }
    }

    private void OnTransportFaulted(object? sender, MavlinkTransportFault fault)
        => _ = HandleTransportFaultAsync(fault);

    private async Task HandleTransportFaultAsync(MavlinkTransportFault fault)
    {
        LastError = fault.Message;
        State = Definition.AutoReconnect && HasConnectBeenRequested
            ? AvailabilityState.Reconnecting
            : AvailabilityState.Offline;
        if (State == AvailabilityState.Reconnecting) ScheduleReconnect();
        _logger.LogWarning(fault.Exception, "MAVLink transport failed for {ConnectionId}: {Message}", Definition.Id, fault.Message);
        try { await StopSessionAsync(clearSystems: false); }
        catch (Exception ex) { _logger.LogDebug(ex, "MAVLink transport cleanup failed for {ConnectionId}", Definition.Id); }
        RaiseChanged();
    }

    private void ProcessPacket(MavlinkPacket packet, MavlinkTransportRoute route)
    {
        TaskCompletionSource<LogosConnectionObservation>? firstHeartbeat = null;
        LogosConnectionObservation? firstObservation = null;
        var newlyDiscovered = false;
        if (packet.MessageId == MavlinkMessageIds.Ping)
        {
            lock (_gate)
            {
                ProcessPing(packet, route);
            }
            RaiseChanged();
            return;
        }

        lock (_gate)
        {
            if (packet.MessageId is MavlinkMessageIds.CameraInformation or
                MavlinkMessageIds.CameraSettings or
                MavlinkMessageIds.CameraCaptureStatus or
                MavlinkMessageIds.VideoStreamInformation or
                MavlinkMessageIds.VideoStreamStatus)
            {
                TrackEarlyPacket(packet.SystemId, packet, route);
                ProcessCameraPacket(packet, route);
                // Component packets update camera state and their own sequence
                // stream. They must not contribute to flight-controller link
                // health or keep the vehicle alive when FC telemetry is stale.
                RaiseChanged();
                return;
            }

            if (packet.MessageId is MavlinkMessageIds.GimbalManagerInformation or
                MavlinkMessageIds.GimbalDeviceInformation or
                MavlinkMessageIds.GimbalDeviceAttitudeStatus)
            {
                TrackEarlyPacket(packet.SystemId, packet, route);
                ProcessGimbalPacket(packet, route);
                RaiseChanged();
                return;
            }

            if (packet.MessageId is MavlinkMessageIds.MissionRequest or MavlinkMessageIds.MissionRequestInt or MavlinkMessageIds.MissionAck or MavlinkMessageIds.MissionCount or MavlinkMessageIds.MissionItem or MavlinkMessageIds.MissionItemInt)
            {
                TrackEarlyPacket(packet.SystemId, packet, route);
                ProcessMissionPacket(packet);
                return;
            }

            if (packet.MessageId == MavlinkMessageIds.MissionCurrent)
            {
                TrackEarlyPacket(packet.SystemId, packet, route);
                if (_systems.TryGetValue(packet.SystemId, out var missionSystem))
                {
                    missionSystem.CurrentMissionItem = packet.UInt16("seq");
                    var missionState = packet.Byte("mission_state", byte.MaxValue);
                    missionSystem.MissionState = missionState == byte.MaxValue ? null : missionState;
                    missionSystem.MissionUpdatedAt = packet.ReceivedAt;
                }
                RaiseChanged();
                return;
            }

            if (packet.MessageId == MavlinkMessageIds.MissionItemReached)
            {
                TrackEarlyPacket(packet.SystemId, packet, route);
                if (_systems.TryGetValue(packet.SystemId, out var missionSystem))
                {
                    missionSystem.LastReachedMissionItem = packet.UInt16("seq");
                    missionSystem.MissionUpdatedAt = packet.ReceivedAt;
                }
                RaiseChanged();
                return;
            }

            if (packet.MessageId == MavlinkMessageIds.CommandAck)
            {
                TrackEarlyPacket(packet.SystemId, packet, route);
                ProcessAck(packet);
                return;
            }

            if (packet.MessageId == MavlinkMessageIds.Heartbeat)
            {
                var mavType = packet.Byte("type");
                var autopilot = packet.Byte("autopilot");
                if ((mavType == MavlinkValues.MavTypeCamera ||
                    packet.ComponentId >= MavlinkValues.MavCompIdCamera) &&
                    packet.ComponentId is not MavlinkValues.MavCompIdGimbal and not MavlinkValues.MavCompIdGimbal2)
                {
                    ProcessCameraHeartbeat(packet, route);
                    RaiseChanged();
                    return;
                }
                if (mavType == MavlinkValues.MavTypeGimbal ||
                    packet.ComponentId is MavlinkValues.MavCompIdGimbal or MavlinkValues.MavCompIdGimbal2)
                {
                    ProcessGimbalHeartbeat(packet, route);
                    RaiseChanged();
                    return;
                }
                if (mavType is MavlinkValues.MavTypeGcs or MavlinkValues.MavTypeOnboardController ||
                    autopilot == MavlinkValues.MavAutopilotInvalid)
                {
                    return;
                }

                if (!_systems.TryGetValue(packet.SystemId, out var system))
                {
                    system = new SystemState(packet.SystemId);
                    _systems.Add(packet.SystemId, system);
                    newlyDiscovered = true;
                    AddEvent(
                        "Info",
                        "mavlink",
                        $"Discovered MAVLink system {packet.SystemId}, component {packet.ComponentId}.",
                        packet.SystemId);
                }

                var hadHeartbeat = system.LastHeartbeatAt != default;
                var previousBaseMode = system.BaseMode;
                var previousCustomMode = system.CustomMode;
                var previousAdapter = AdapterFor(system);
                var previousMode = previousAdapter?.DecodeMode(previousCustomMode) ?? $"Mode 0x{previousCustomMode:X8}";
                var previousArmed = (previousBaseMode & MavlinkValues.MavModeFlagSafetyArmed) != 0;
                system.ComponentId = packet.ComponentId;
                system.MavType = mavType;
                system.MavAutopilot = autopilot;
                system.BaseMode = packet.Byte("base_mode");
                system.CustomMode = packet.UInt32("custom_mode");
                system.SystemStatus = packet.Byte("system_status");
                system.LastHeartbeatAt = packet.ReceivedAt;
                system.LastMessageAt = packet.ReceivedAt;
                // A configured bootstrap route is also the stable command route for
                // containerized SITL relays.  Keep using it after discovery so
                // mission requests/acks return through the relay instead of the
                // ephemeral NAT source port of the inbound datagram.
                system.Route = _bootstrapRoute ?? route;
                system.ProtocolVersion = packet.ProtocolVersion;
                system.Availability = AvailabilityState.Online;
                TrackSequence(system, packet);

                var supported = AdapterFor(system) is not null;
                if (supported)
                {
                    ConnectedAt ??= packet.ReceivedAt;
                    if (State is not AvailabilityState.Online and not AvailabilityState.Degraded)
                    {
                        LastConnectedAt = packet.ReceivedAt;
                    }
                    State = _systems.Values.Any(item => item.SignalState is SignalHealth.Degraded or SignalHealth.Severe)
                        ? AvailabilityState.Degraded
                        : AvailabilityState.Online;
                    _reconnectAttempt = 0;
                    NextReconnectAt = null;
                }
                LastSnapshotRefresh = packet.ReceivedAt;
                LastError = !supported
                    ? $"MAVLink system {packet.SystemId} is not a supported {ConfiguredAutopilotName} multicopter."
                    : State == AvailabilityState.Degraded
                        ? "MAVLink radio signal is degraded; commands remain available while telemetry is fresh."
                        : null;
                if (hadHeartbeat)
                {
                    var currentMode = AdapterFor(system)?.DecodeMode(system.CustomMode) ?? $"Mode 0x{system.CustomMode:X8}";
                    var currentArmed = (system.BaseMode & MavlinkValues.MavModeFlagSafetyArmed) != 0;
                    if (previousCustomMode != system.CustomMode)
                    {
                        AddEvent(
                            "Info",
                            "mavlink-mode",
                            $"Vehicle mode changed from {previousMode} to {currentMode}.",
                            packet.SystemId,
                            "MAVLINK_MODE_CHANGED",
                            $"mode:{system.CustomMode}");
                    }

                    if (previousArmed != currentArmed)
                    {
                        AddEvent(
                            "Info",
                            "mavlink-state",
                            $"Vehicle arm state changed to {(currentArmed ? "Armed" : "Disarmed")} (mode: {currentMode}).",
                            packet.SystemId,
                            "MAVLINK_ARM_STATE_CHANGED",
                            currentArmed ? "armed" : "disarmed");
                    }
                }
                firstObservation = ToObservation(system);
                firstHeartbeat = supported ? _firstHeartbeat : null;
            }
            else if (_systems.TryGetValue(packet.SystemId, out var system))
            {
                if (packet.ComponentId == system.ComponentId)
                {
                    system.LastMessageAt = packet.ReceivedAt;
                    system.Route = _bootstrapRoute ?? route;
                }
                TrackSequence(system, packet);
                switch (packet.MessageId)
                {
                    case MavlinkMessageIds.GlobalPositionInt:
                        system.LatitudeDegrees = packet.Int32("lat") / 1e7;
                        system.LongitudeDegrees = packet.Int32("lon") / 1e7;
                        system.AltitudeMslMetres = packet.Int32("alt") / 1000d;
                        system.AltitudeAglMetres = packet.Int32("relative_alt") / 1000d;
                        system.LastPositionAt = packet.ReceivedAt;
                        system.VelocityNorth = packet.Int32("vx") / 100d;
                        system.VelocityEast = packet.Int32("vy") / 100d;
                        system.VelocityDown = packet.Int32("vz") / 100d;
                        var heading = packet.UInt16("hdg", ushort.MaxValue);
                        if (heading != ushort.MaxValue)
                        {
                            system.HeadingDegrees = heading / 100d;
                        }
                        break;
                    case MavlinkMessageIds.HomePosition:
                        // HOME_POSITION is the authoritative launch reference
                        // used to turn the generic Recover/RTL operation into
                        // an application-level return-and-hold action. Keep
                        // it separate from the live vehicle position.
                        var homeLatitude = packet.Int32("latitude") / 1e7d;
                        var homeLongitude = packet.Int32("longitude") / 1e7d;
                        if (double.IsFinite(homeLatitude) && double.IsFinite(homeLongitude) &&
                            Math.Abs(homeLatitude) <= 90 && Math.Abs(homeLongitude) <= 180)
                        {
                            system.HomeLatitudeDegrees = homeLatitude;
                            system.HomeLongitudeDegrees = homeLongitude;
                            system.HomePositionAt = packet.ReceivedAt;
                        }
                        break;
                    case MavlinkMessageIds.LocalPositionNed:
                        system.LocalNorth = packet.Single("x");
                        system.LocalEast = packet.Single("y");
                        system.LocalDown = packet.Single("z");
                        break;
                    case MavlinkMessageIds.Attitude:
                        if (system.HeadingDegrees is null)
                        {
                            system.HeadingDegrees = NormalizeHeading(
                                packet.Single("yaw") * 180d / Math.PI);
                        }
                        break;
                    case MavlinkMessageIds.ExtendedSystemState:
                        var previousLandedState = system.LandedState;
                        var landedState = packet.Byte("landed_state") switch
                        {
                            MavlinkValues.MavLandedStateOnGround => "Landed",
                            MavlinkValues.MavLandedStateInAir => "Flying",
                            3 => "Taking off",
                            4 => "Landing",
                            _ => "Unknown"
                        };
                        system.LandedState = landedState;
                        if (!string.Equals(previousLandedState, landedState, StringComparison.OrdinalIgnoreCase))
                        {
                            var mode = AdapterFor(system)?.DecodeMode(system.CustomMode) ?? $"Mode 0x{system.CustomMode:X8}";
                            AddEvent(
                                "Info",
                                "mavlink-state",
                                $"Vehicle landed state changed from {previousLandedState} to {landedState} (mode: {mode}).",
                                packet.SystemId,
                                "MAVLINK_LANDED_STATE_CHANGED",
                                landedState);
                        }
                        break;
                    case MavlinkMessageIds.SystemStatus:
                        system.SensorsPresent = packet.UInt32("onboard_control_sensors_present");
                        system.SensorsEnabled = packet.UInt32("onboard_control_sensors_enabled");
                        system.SensorsHealthy = packet.UInt32("onboard_control_sensors_health");
                        system.SensorsUpdatedAt = packet.ReceivedAt;
                        system.DropRateComm = packet.UInt16("drop_rate_comm", ushort.MaxValue) is var drop && drop != ushort.MaxValue ? drop : null;
                        system.CommunicationErrors = packet.UInt16("errors_comm", ushort.MaxValue) is var errors && errors != ushort.MaxValue ? errors : null;
                        system.BatteryVoltageMillivolts = packet.UInt16("voltage_battery", ushort.MaxValue) is var voltage && voltage is > 0 and < ushort.MaxValue ? voltage : null;
                        system.BatteryCurrentCentiamps = packet.Int16("current_battery", -1) is var current && current >= 0 ? current : null;
                        system.BatteryRemainingPercent = packet.Int8("battery_remaining", -1) is var remaining && remaining is >= 0 and <= 100 ? remaining : null;
                        system.BatteryObservedAt = packet.ReceivedAt;
                        system.Health = "Diagnostics available";
                        break;
                    case MavlinkMessageIds.GpsRawInt:
                        system.GpsFixType = packet.Byte("fix_type");
                        system.GpsSatellites = packet.Byte("satellites_visible", byte.MaxValue) is var satellites && satellites != byte.MaxValue ? satellites : null;
                        system.GpsHorizontalAccuracyMetres = packet.UInt16("eph", ushort.MaxValue) is var eph && eph != ushort.MaxValue ? eph / 100d : null;
                        system.GpsVerticalAccuracyMetres = packet.UInt16("epv", ushort.MaxValue) is var epv && epv != ushort.MaxValue ? epv / 100d : null;
                        break;
                    case MavlinkMessageIds.BatteryStatus:
                        system.BatteryStatusVoltageMillivolts = TryReadBatteryVoltage(packet, out var batteryVoltage)
                            ? batteryVoltage
                            : null;
                        system.BatteryStatusCurrentCentiamps = packet.Int16("current_battery", -1) is var batteryCurrent && batteryCurrent >= 0 ? batteryCurrent : null;
                        system.BatteryStatusRemainingPercent = packet.Int8("battery_remaining", -1) is var batteryRemaining && batteryRemaining is >= 0 and <= 100 ? batteryRemaining : null;
                        system.BatteryFaults = packet.UInt32("fault_bitmask");
                        system.BatteryStatusObservedAt = packet.ReceivedAt;
                        break;
                    case MavlinkMessageIds.EstimatorStatus:
                        system.EstimatorFlags = packet.UInt32("flags");
                        break;
                    case MavlinkMessageIds.Vibration:
                        system.VibrationX = packet.Single("vibration_x");
                        system.VibrationY = packet.Single("vibration_y");
                        system.VibrationZ = packet.Single("vibration_z");
                        system.Clipping0 = packet.UInt32("clipping_0");
                        system.Clipping1 = packet.UInt32("clipping_1");
                        system.Clipping2 = packet.UInt32("clipping_2");
                        break;
                    case MavlinkMessageIds.StatusText:
                        ProcessStatusText(system, packet);
                        break;
                    case MavlinkMessageIds.AutopilotVersion:
                        system.Version = DecodeFlightVersion(packet.UInt32("flight_sw_version"));
                        break;
                    case MavlinkMessageIds.RadioStatus:
                        system.Rssi = packet.Byte("rssi");
                        system.RemoteRssi = packet.Byte("remrssi");
                        system.Noise = packet.Byte("noise");
                        system.RemoteNoise = packet.Byte("remnoise");
                        system.TransmitBufferPercent = packet.Byte("txbuf", byte.MaxValue);
                        system.ReceiveErrors = packet.UInt16("rxerrors", ushort.MaxValue);
                        system.CorrectedPackets = packet.UInt16("fixed", ushort.MaxValue);
                        break;
                    case MavlinkMessageIds.RcChannels:
                        // RC_CHANNELS is the optional ArduPilot-side observation
                        // of the normalized pilot-input path. It is status only;
                        // activation remains based on mode and admission checks.
                        system.ManualInputEchoAt = packet.ReceivedAt;
                        if (IsArduPilotSystem(system) && HasValidRcChannels(system, packet))
                        {
                            foreach (var blocker in system.ActiveTextBlockers.Keys
                                         .Where(IsReceiverPreArmBlocker)
                                         .ToArray())
                            {
                                system.ActiveTextBlockers.Remove(blocker);
                            }
                        }
                        break;
                    case MavlinkMessageIds.ParamValue:
                        ProcessParameterValue(system, packet);
                        break;
                }

                EvaluateSignalHealth(system, packet.ReceivedAt);
                if (_systems.Values.Any(item => item.SignalState is SignalHealth.Degraded or SignalHealth.Severe))
                {
                    State = AvailabilityState.Degraded;
                    LastError = "MAVLink radio signal is degraded; commands remain available while telemetry is fresh.";
                }
                else if (_systems.Values.Any(item => item.Availability == AvailabilityState.Online))
                {
                    State = AvailabilityState.Online;
                    LastError = null;
                }
                EvaluateOperation(system);
            }
        }

        if (firstObservation is not null)
        {
            firstHeartbeat?.TrySetResult(firstObservation);
            if (newlyDiscovered)
            {
                _ = RequestTelemetryStreamsAsync(packet.SystemId);
                lock (_gate)
                {
                    if (_cameraInformationRequested.Add((packet.SystemId, packet.ComponentId)))
                    {
                        _ = RequestCameraInformationAsync(packet.SystemId, packet.ComponentId);
                    }
                    var cameraKey = (packet.SystemId, MavlinkValues.MavCompIdCamera);
                    if (_cameraInformationRequested.Add(cameraKey))
                    {
                        _ = RequestCameraInformationAsync(packet.SystemId, MavlinkValues.MavCompIdCamera);
                    }
                }
            }
        }
        RaiseChanged();
    }

    private void ProcessPing(MavlinkPacket packet, MavlinkTransportRoute route)
    {
        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
        var targetSystem = packet.Byte("target_system");
        var targetComponent = packet.Byte("target_component");
        if (targetSystem == options.SourceSystemId &&
            targetComponent == options.SourceComponentId &&
            _systems.TryGetValue(packet.SystemId, out var system))
        {
            var sentMicroseconds = packet.UInt64("time_usec");
            var nowMicroseconds = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            if (sentMicroseconds > 0 && sentMicroseconds <= nowMicroseconds)
            {
                system.LatencyMilliseconds = (nowMicroseconds - sentMicroseconds) / 1000d;
            }
            system.LastMessageAt = packet.ReceivedAt;
            system.Route = _bootstrapRoute ?? route;
            TrackSequence(system, packet);
            return;
        }

        if (targetSystem == 0 && targetComponent == 0)
        {
            var response = _codec.EncodePing(
                options.SourceSystemId,
                options.SourceComponentId,
                packet.UInt64("time_usec"),
                packet.UInt32("seq"),
                packet.SystemId,
                packet.ComponentId);
            _ = SendPingResponseAsync(response, route);
        }
    }

    private void ProcessCameraHeartbeat(MavlinkPacket packet, MavlinkTransportRoute route)
    {
        var key = (packet.SystemId, packet.ComponentId);
        if (!_cameras.TryGetValue(key, out var camera))
        {
            camera = new MavlinkCameraState(packet.SystemId, packet.ComponentId);
            _cameras.Add(key, camera);
        }

        camera.LastHeartbeatAt = packet.ReceivedAt;
        camera.LastMessageAt = packet.ReceivedAt;
        camera.Route = _bootstrapRoute ?? route;
        camera.Availability = AvailabilityState.Online;
        if (_cameraInformationRequested.Add(key))
        {
            _ = RequestCameraInformationAsync(packet.SystemId, packet.ComponentId);
        }
    }

    private void ProcessCameraPacket(MavlinkPacket packet, MavlinkTransportRoute route)
    {
        var key = (packet.SystemId, packet.ComponentId);
        if (!_cameras.TryGetValue(key, out var camera))
        {
            camera = new MavlinkCameraState(packet.SystemId, packet.ComponentId);
            _cameras.Add(key, camera);
        }

        camera.LastMessageAt = packet.ReceivedAt;
        camera.Route = _bootstrapRoute ?? route;
        camera.Availability = AvailabilityState.Online;
        if (packet.MessageId == MavlinkMessageIds.CameraInformation)
        {
            var previousDefinitionUri = camera.DefinitionUri;
            var previousDefinitionVersion = camera.DefinitionVersion;
            camera.VendorName = packet.Text("vendor_name");
            camera.ModelName = packet.Text("model_name");
            camera.FirmwareVersion = FormatCameraFirmware(packet.UInt32("firmware_version"));
            camera.DefinitionVersion = packet.UInt16("cam_definition_version");
            camera.DefinitionUri = packet.Text("cam_definition_uri");
            camera.CapabilityFlags = packet.UInt32("flags");
            UpsertCameraDefinition(camera);
            var definitionChanged = !camera.DefinitionLoadRequested ||
                !string.Equals(previousDefinitionUri, camera.DefinitionUri, StringComparison.Ordinal) ||
                previousDefinitionVersion != camera.DefinitionVersion;
            if (definitionChanged && !string.IsNullOrWhiteSpace(camera.DefinitionUri))
            {
                camera.DefinitionLoadRequested = true;
                _ = LoadCameraDefinitionAsync(camera, camera.DefinitionUri);
            }
        }
        else if (packet.MessageId == MavlinkMessageIds.CameraSettings)
        {
            var mode = packet.Byte("mode_id", packet.Byte("mode", byte.MaxValue));
            var zoom = packet.Single("zoomLevel", float.NaN);
            if (float.IsFinite(zoom) && zoom is >= 0 and <= 100)
                camera.ZoomPercent = zoom;
            if (mode != byte.MaxValue)
            {
                camera.Settings = camera.Settings
                    .Select(setting => setting.Name.Equals("camera-mode", StringComparison.OrdinalIgnoreCase) ||
                                       setting.Name.Equals("mode", StringComparison.OrdinalIgnoreCase)
                        ? setting with { CurrentValue = mode.ToString(CultureInfo.InvariantCulture) }
                        : setting)
                    .ToArray();
                UpsertCameraDefinition(camera);
            }
        }
        else if (packet.MessageId == MavlinkMessageIds.CameraCaptureStatus)
        {
            camera.RecordingVideo = packet.Byte("video_status") != 0;
        }
    }

    private void ProcessGimbalHeartbeat(MavlinkPacket packet, MavlinkTransportRoute route)
    {
        var key = (packet.SystemId, packet.ComponentId);
        var discovered = false;
        if (!_gimbals.TryGetValue(key, out var gimbal))
        {
            gimbal = new MavlinkGimbalState(packet.SystemId, packet.ComponentId);
            _gimbals.Add(key, gimbal);
            discovered = true;
        }

        gimbal.LastHeartbeatAt = packet.ReceivedAt;
        gimbal.LastMessageAt = packet.ReceivedAt;
        gimbal.Route = _bootstrapRoute ?? route;
        gimbal.Availability = AvailabilityState.Online;
        if (packet.MessageId == MavlinkMessageIds.GimbalManagerInformation)
        {
            gimbal.CapabilityFlags = packet.UInt32("cap_flags");
            gimbal.CapabilityFlagsReported = packet.Fields.ContainsKey("cap_flags");
            gimbal.DeviceId = packet.Byte("gimbal_device_id", gimbal.DeviceId);
            gimbal.ManagerInformationReported = true;
        }
        else if (packet.MessageId == MavlinkMessageIds.GimbalDeviceInformation)
        {
            gimbal.CapabilityFlags = packet.UInt16("cap_flags");
            gimbal.CapabilityFlagsReported = packet.Fields.ContainsKey("cap_flags");
            gimbal.DeviceId = packet.Byte("gimbal_device_id", gimbal.ComponentId);
        }
        else if (packet.MessageId == MavlinkMessageIds.GimbalDeviceAttitudeStatus)
        {
            gimbal.DeviceId = packet.Byte("gimbal_device_id", gimbal.DeviceId == 0 ? gimbal.ComponentId : gimbal.DeviceId);
            if (packet.Fields.ContainsKey("flags"))
            {
                var flags = packet.UInt16("flags");
                gimbal.YawInEarthFrame = (flags & GimbalDeviceFlagsYawInEarthFrame) != 0 ||
                    (flags & GimbalDeviceFlagsYawInVehicleFrame) == 0 && (flags & GimbalDeviceFlagsYawLock) != 0;
            }
            if (TryReadQuaternion(packet, out var w, out var x, out var y, out var z))
            {
                var (roll, pitch, yaw) = QuaternionToEulerDegrees(w, x, y, z);
                gimbal.RollDegrees = roll;
                gimbal.PitchDegrees = pitch;
                gimbal.YawDegrees = yaw;
            }
        }

        if (discovered)
        {
            var managerComponentId = _systems.TryGetValue(packet.SystemId, out var system)
                ? system.ComponentId
                : MavlinkValues.MavCompIdAutopilot1;
            if (_gimbalInformationRequested.Add((packet.SystemId, managerComponentId, MavlinkMessageIds.GimbalManagerInformation)))
            {
                _ = RequestGimbalInformationAsync(
                    packet.SystemId,
                    managerComponentId,
                    MavlinkMessageIds.GimbalManagerInformation);
            }
            if (_gimbalInformationRequested.Add((packet.SystemId, packet.ComponentId, MavlinkMessageIds.GimbalDeviceInformation)))
            {
                _ = RequestGimbalInformationAsync(
                    packet.SystemId,
                    packet.ComponentId,
                    MavlinkMessageIds.GimbalDeviceInformation);
            }
        }
    }

    private static bool TryReadQuaternion(
        MavlinkPacket packet,
        out double w,
        out double x,
        out double y,
        out double z)
    {
        w = x = y = z = 0;
        if (!packet.Fields.TryGetValue("q", out var value) || value is not Array values || values.Length < 4)
            return false;

        try
        {
            w = Convert.ToDouble(values.GetValue(0), CultureInfo.InvariantCulture);
            x = Convert.ToDouble(values.GetValue(1), CultureInfo.InvariantCulture);
            y = Convert.ToDouble(values.GetValue(2), CultureInfo.InvariantCulture);
            z = Convert.ToDouble(values.GetValue(3), CultureInfo.InvariantCulture);
            return new[] { w, x, y, z }.All(double.IsFinite);
        }
        catch (Exception) when (value is IConvertible or Array)
        {
            return false;
        }
    }

    private static bool TryReadBatteryVoltage(MavlinkPacket packet, out ushort voltageMillivolts)
    {
        voltageMillivolts = 0;
        if (!packet.Fields.TryGetValue("voltages", out var value) || value is not Array values)
            return false;

        for (var index = 0; index < values.Length; index++)
        {
            try
            {
                var voltage = Convert.ToUInt16(values.GetValue(index), CultureInfo.InvariantCulture);
                if (voltage != ushort.MaxValue && voltage > 0)
                {
                    voltageMillivolts = voltage;
                    return true;
                }
            }
            catch (Exception) when (values is IConvertible or Array)
            {
                return false;
            }
        }

        return false;
    }

    private static ushort? BatteryVoltageMillivolts(SystemState system)
        => system.BatteryStatusVoltageMillivolts ?? system.BatteryVoltageMillivolts;

    private static short? BatteryCurrentCentiamps(SystemState system)
        => system.BatteryStatusCurrentCentiamps ?? system.BatteryCurrentCentiamps;

    private static sbyte? BatteryRemainingPercent(SystemState system)
        => BatteryVoltageMillivolts(system) is > 0
            ? system.BatteryStatusRemainingPercent ?? system.BatteryRemainingPercent
            : null;

    private static DateTimeOffset? BatteryObservedAt(SystemState system)
        => system.BatteryStatusObservedAt ?? system.BatteryObservedAt;

    private static (double Roll, double Pitch, double Yaw) QuaternionToEulerDegrees(
        double w,
        double x,
        double y,
        double z)
    {
        var roll = Math.Atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y));
        var pitchSin = Math.Clamp(2 * (w * y - z * x), -1, 1);
        var pitch = Math.Asin(pitchSin);
        var yaw = Math.Atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z));
        const double radiansToDegrees = 180d / Math.PI;
        return (roll * radiansToDegrees, pitch * radiansToDegrees, yaw * radiansToDegrees);
    }

    private void ProcessGimbalPacket(MavlinkPacket packet, MavlinkTransportRoute route)
        => ProcessGimbalHeartbeat(packet, route);

    private async Task RequestCameraInformationAsync(byte systemId, byte componentId)
    {
        try
        {
            var command = new MavlinkCommandEnvelope(
                MavlinkCommandIds.RequestMessage,
                [MavlinkMessageIds.CameraInformation, 0, 0, 0, 0, 0, 0],
                "Request MAVLink camera information");
            var acknowledgement = await SendCommandAsync(systemId, componentId, command, _sessionCancellation?.Token ?? default);
            if (!acknowledgement.Accepted)
            {
                await SendCommandAsync(
                    systemId,
                    componentId,
                    new MavlinkCommandEnvelope(
                        MavlinkCommandIds.RequestCameraInformation,
                        [],
                        "Request legacy MAVLink camera information"),
                    _sessionCancellation?.Token ?? default);
            }

            await RequestCameraCaptureStatusAsync(systemId, componentId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not request camera information from MAVLink camera {SystemId}/{ComponentId}", systemId, componentId);
        }
    }

    private async Task RequestCameraCaptureStatusAsync(byte systemId, byte componentId)
    {
        try
        {
            var command = new MavlinkCommandEnvelope(
                MavlinkCommandIds.RequestMessage,
                [MavlinkMessageIds.CameraCaptureStatus, 0, 0, 0, 0, 0, 0],
                "Request MAVLink camera capture status");
            await SendCommandAsync(systemId, componentId, command, _sessionCancellation?.Token ?? default);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not request MAVLink camera capture status from {SystemId}/{ComponentId}", systemId, componentId);
        }
    }

    private async Task RequestGimbalInformationAsync(byte systemId, byte componentId, uint messageId)
    {
        try
        {
            var command = new MavlinkCommandEnvelope(
                MavlinkCommandIds.RequestMessage,
                [messageId, 0, 0, 0, 0, 0, 0],
                $"Request MAVLink gimbal message {messageId}");
            await SendCommandAsync(systemId, componentId, command, _sessionCancellation?.Token ?? default);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not request MAVLink gimbal message {MessageId} from {SystemId}/{ComponentId}", messageId, systemId, componentId);
        }
    }

    private async Task LoadCameraDefinitionAsync(MavlinkCameraState camera, string definitionUri)
    {
        var sessionCancellation = _sessionCancellation;
        var cancellationToken = sessionCancellation?.Token ?? default;
        try
        {
            var settings = await _cameraDefinitionLoader.LoadAsync(definitionUri, cancellationToken);
            lock (_gate)
            {
                if (_disposed ||
                    cancellationToken.IsCancellationRequested ||
                    !ReferenceEquals(_sessionCancellation, sessionCancellation))
                {
                    return;
                }
                camera.Settings = settings;
                camera.DefinitionStatus = settings.Count == 0 ? "Definition contains no settings" : "Definition loaded";
                UpsertCameraDefinition(camera);
            }
            RaiseChanged();
        }
        catch (OperationCanceledException) when (_disposed || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogDebug(ex, "Could not load MAVLink camera definition {DefinitionUri}", definitionUri);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_sessionCancellation, sessionCancellation))
                {
                    return;
                }
                camera.DefinitionStatus = "Definition unavailable";
                UpsertCameraDefinition(camera);
            }
        }
    }

    private void UpsertCameraDefinition(MavlinkCameraState camera)
    {
        _cameraDefinitions?.Upsert(new MavlinkCameraDefinitionRecord(
            CameraSourceId(camera.SystemId, camera.ComponentId),
            Definition.Id,
            camera.SystemId,
            camera.ComponentId,
            camera.CapabilityFlags,
            camera.VendorName,
            camera.ModelName,
            camera.FirmwareVersion,
            camera.DefinitionVersion,
            camera.DefinitionUri,
            camera.Settings,
            camera.LastMessageAt == default ? DateTimeOffset.UtcNow : camera.LastMessageAt,
            camera.DefinitionStatus));
    }

    private CameraSourceRecord ToCameraSource(MavlinkCameraState camera)
    {
        var displayName = string.Join(" ", new[] { camera.VendorName, camera.ModelName }
            .Where(item => !string.IsNullOrWhiteSpace(item))).Trim();
        if (displayName.Length == 0) displayName = $"MAVLink camera {camera.SystemId}/{camera.ComponentId}";
        var sourceId = CameraSourceId(camera.SystemId, camera.ComponentId);
        var gimbal = _gimbals.Values
            .Where(item => item.SystemId == camera.SystemId)
            .OrderByDescending(item => item.ManagerInformationReported)
            .ThenBy(item => item.ComponentId)
            .FirstOrDefault();
        // A zero capability bitmask means the camera did not report optional
        // flags, not that it is incapable. The command acknowledgement remains
        // authoritative for execution.
        var capabilitiesUnknown = camera.CapabilityFlags == 0;
        return new CameraSourceRecord(
            sourceId,
            sourceId,
            Definition.Id,
            null,
            displayName,
            "MAVLink camera",
            camera.Availability,
            camera.DefinitionStatus,
            "Discovered",
            true,
            DateTimeOffset.UtcNow - camera.LastMessageAt < SystemStaleAfter,
            false,
            0,
            0,
            0,
            0,
            $"mavlink-{camera.SystemId}-{camera.ComponentId}",
            "MAVLINK_CAMERA_DISCOVERED",
            camera.DefinitionStatus,
            camera.LastMessageAt == default ? DateTimeOffset.UtcNow : camera.LastMessageAt,
            SupportsPhoto: capabilitiesUnknown || (camera.CapabilityFlags & 2) != 0,
            SupportsVideo: capabilitiesUnknown || (camera.CapabilityFlags & 1) != 0,
            SupportsGimbal: gimbal is not null,
            GimbalComponentId: gimbal?.ComponentId);
    }

    private static string CameraSourceId(byte systemId, byte componentId) => $"mavlink:{systemId}:{componentId}";

    private static string FormatCameraFirmware(uint version)
    {
        if (version == 0) return "Not reported";
        var major = version & 0xff;
        var minor = (version >> 8) & 0xff;
        var patch = (version >> 16) & 0xff;
        var development = (version >> 24) & 0xff;
        return development == 0
            ? $"{major}.{minor}.{patch}"
            : $"{major}.{minor}.{patch}.{development}";
    }

    private static void ProcessParameterValue(SystemState system, MavlinkPacket packet)
    {
        var name = packet.Text("param_id");
        if (name.Length == 0) return;

        var value = new MavlinkParameterValue(
            packet.SystemId,
            packet.ComponentId,
            name,
            packet.Single("param_value"),
            packet.Byte("param_type"),
            packet.Int16("param_index", -1),
            packet.UInt16("param_count"),
            packet.ReceivedAt);
        system.Parameters[(value.ComponentId, value.Name)] = value;
        system.ParameterSignal?.TrySetResult(true);
        if (system.ParameterWaiters.TryGetValue((value.ComponentId, value.Name), out var waiter))
        {
            waiter.TrySetResult(value);
        }
    }

    private async Task SendPingResponseAsync(byte[] response, MavlinkTransportRoute route)
    {
        try
        {
            await _transport.SendAsync(
                response,
                route,
                _sessionCancellation?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not respond to MAVLink PING.");
        }
    }

    private void ProcessAck(MavlinkPacket packet)
    {
        var command = packet.UInt16("command");
        var result = packet.Byte("result");
        var key = new CommandKey(packet.SystemId, packet.ComponentId, command);
        if (_ackQuarantine.TryGetValue(key, out var quarantineUntil))
        {
            if (quarantineUntil > packet.ReceivedAt)
            {
                _logger.LogWarning(
                    "Ignoring delayed MAVLink ACK for command {Command} from system {SystemId}; the previous transaction timed out until {QuarantineUntil}.",
                    command,
                    packet.SystemId,
                    quarantineUntil);
                return;
            }

            _ackQuarantine.TryRemove(key, out _);
        }

        if (_pendingAcks.TryGetValue(key, out var pending))
        {
            pending.TrySetResult(new MavlinkCommandAck(
                command,
                result,
                packet.Int32("result_param2"),
                packet.Byte("progress")));
        }
    }

    private void ProcessMissionPacket(MavlinkPacket packet)
    {
        var packetMissionType = packet.Byte("mission_type");
        var key = new MissionTransferKey(packet.SystemId, packet.ComponentId, packetMissionType);
        if (!_missionTransfers.TryGetValue(key, out var session))
        {
            Log.MissionWithoutTransfer(_logger, packet.MessageId, packet.SystemId);
            return;
        }
        if (packetMissionType != session.MissionType)
        {
            Log.MissionTypeMismatch(_logger, packet.MessageId, packetMissionType, session.MissionType);
            return;
        }
        Log.MissionReceived(_logger, packet.MessageId, packet.SystemId, packet.ComponentId);
        switch (packet.MessageId)
        {
            case MavlinkMessageIds.MissionRequest:
            case MavlinkMessageIds.MissionRequestInt:
                var requestedSequence = packet.UInt16("seq");
                if (!session.IsValidRequestedSequence(requestedSequence))
                {
                    Log.InvalidUploadRequest(_logger, requestedSequence, session.UploadCount);
                    return;
                }

                session.LastRequestedSequence = requestedSequence;
                Log.ItemRequested(_logger, session.LastRequestedSequence.GetValueOrDefault(),
                    packet.MessageId == MavlinkMessageIds.MissionRequestInt ? "MISSION_ITEM_INT" : "MISSION_ITEM");
                session.Set(new(MissionSignalKind.Request, packet.UInt16("seq"), UsesIntegerItems: packet.MessageId == MavlinkMessageIds.MissionRequestInt));
                break;
            case MavlinkMessageIds.MissionAck:
                var missionResult = packet.Byte("type");
                Log.MissionAcknowledged(_logger, missionResult);
                session.Set(new(MissionSignalKind.Ack, 0, missionResult));
                break;
            case MavlinkMessageIds.MissionCount:
                session.Set(new(MissionSignalKind.Count, packet.UInt16("count")));
                break;
            case MavlinkMessageIds.MissionItem:
            case MavlinkMessageIds.MissionItemInt:
                var isInt = packet.MessageId == MavlinkMessageIds.MissionItemInt;
                var itemSequence = packet.UInt16("seq");
                if (!session.IsValidReceivedSequence(itemSequence))
                {
                    Log.InvalidReceivedItem(_logger, itemSequence, session.ExpectedCount);
                    return;
                }

                session.Set(new(MissionSignalKind.Item, itemSequence, Item: new MavlinkMissionItem(
                    itemSequence, packet.UInt16("command"), packet.Byte("frame"),
                    isInt ? packet.Int32("x") : checked((int)Math.Round(packet.Single("x") * 10_000_000d)),
                    isInt ? packet.Int32("y") : checked((int)Math.Round(packet.Single("y") * 10_000_000d)), packet.Single("z"),
                    packet.Single("param1"), packet.Single("param2"), packet.Single("param3"), packet.Single("param4"), packet.Byte("current") != 0, packetMissionType,
                    RawX: isInt ? packet.Int32("x") : null,
                    RawY: isInt ? packet.Int32("y") : null,
                    RawZ: packet.Single("z"))));
                break;
        }
    }

    private async Task<MavlinkCommandAck> SendCommandAsync(
        byte systemId,
        byte componentId,
        MavlinkCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        // Retrying an operator action after an ACK timeout can duplicate an
        // action that actually reached the flight controller. Only telemetry
        // housekeeping is safe to retry automatically.
        var maxAttempts = IsSafeToRetry(command.CommandId) ? 3 : 1;
        for (byte attempt = 0; attempt < maxAttempts; attempt++)
        {
            MavlinkTransportRoute route;
            lock (_gate)
            {
                route = _systems.TryGetValue(systemId, out var system) &&
                           system.Route is not null
                    ? system.Route
                    : throw new InvalidOperationException($"No MAVLink route is known for system {systemId}.");
            }

            var key = new CommandKey(systemId, componentId, command.CommandId);
            var expectsAck = command.WireKind != MavlinkWireKind.SetMode;
            if (expectsAck && _ackQuarantine.TryGetValue(key, out var quarantineUntil))
            {
                if (quarantineUntil > DateTimeOffset.UtcNow)
                {
                    throw new InvalidOperationException(
                        $"MAVLink command {command.CommandId} is temporarily blocked while a delayed acknowledgement is quarantined. Verify the vehicle before retrying.");
                }

                _ackQuarantine.TryRemove(key, out _);
            }

            var completion = expectsAck
                ? new TaskCompletionSource<MavlinkCommandAck>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
            if (completion is not null && !_pendingAcks.TryAdd(key, completion))
            {
                throw new InvalidOperationException(
                    $"MAVLink command {command.CommandId} is already pending for system {systemId}.");
            }

            try
            {
                var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
                var bytes = command.WireKind switch
                {
                    MavlinkWireKind.CommandInt => _codec.EncodeCommandInt(
                        options.SourceSystemId,
                        options.SourceComponentId,
                        systemId,
                        componentId,
                        command.Frame,
                        command.CommandId,
                        command.Parameters,
                        command.X,
                        command.Y,
                        command.Z,
                        attempt),
                    MavlinkWireKind.SetMode => _codec.EncodeSetMode(
                        options.SourceSystemId,
                        options.SourceComponentId,
                        systemId,
                        command.BaseMode,
                        command.CustomMode),
                    _ => _codec.EncodeCommandLong(
                        options.SourceSystemId,
                        options.SourceComponentId,
                        systemId,
                        componentId,
                        command.CommandId,
                        command.Parameters,
                        attempt)
                };
                await _transport.SendAsync(bytes, route, cancellationToken);
                if (!expectsAck)
                {
                    // SET_MODE is a MAVLink control message, not a command
                    // acknowledgement transaction. Completion is confirmed by
                    // the next HEARTBEAT mode update.
                    return new MavlinkCommandAck(command.CommandId, MavlinkValues.MavResultAccepted, 0, 0);
                }
                try
                {
                    return await completion!.Task.WaitAsync(CommandAckTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    if (!IsSafeToRetry(command.CommandId))
                    {
                        _ackQuarantine[key] = DateTimeOffset.UtcNow + DelayedAckQuarantine;
                    }

                    if (attempt + 1 < maxAttempts)
                    {
                        Log.CommandRetry(_logger, command.CommandId, systemId);
                    }
                }
            }
            finally
            {
                if (expectsAck)
                {
                    _pendingAcks.TryRemove(key, out _);
                }
            }
        }

        Log.CommandAcknowledgementMissing(_logger, command.CommandId, systemId, maxAttempts);
        return new MavlinkCommandAck(command.CommandId, 255, 0, 0);
    }

    private static bool IsSafeToRetry(ushort commandId)
        => commandId == MavlinkCommandIds.SetMessageInterval;

    private Task<string> DescribeArduPilotCommandRejectionAsync(
        byte systemId,
        OperatorCommandKind command,
        MavlinkCommandAck acknowledgement,
        CancellationToken cancellationToken)
        => DescribeArduPilotAcknowledgementAsync(
            systemId,
            OperatorCommandLabel(command),
            acknowledgement,
            cancellationToken);

    private async Task<string> DescribeArduPilotAcknowledgementAsync(
        byte systemId,
        string action,
        MavlinkCommandAck acknowledgement,
        CancellationToken cancellationToken)
    {
        // ArduCopter commonly emits the COMMAND_ACK and the explanatory
        // PreArm/Arming denied STATUSTEXT as adjacent packets. Give the
        // receiver a short, bounded window to associate the two without
        // delaying normal command handling or turning status text into a
        // second acknowledgement path.
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500);
        while (true)
        {
            string? reason;
            lock (_gate)
            {
                reason = _systems.TryGetValue(systemId, out var system)
                    ? LatestArduPilotCommandReason(system, DateTimeOffset.UtcNow)
                    : null;
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                return $"ArduPilot rejected {action}: {reason}";
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        string? modeContext = null;
        lock (_gate)
        {
            if (_systems.TryGetValue(systemId, out var system) &&
                action.Equals("Arm", StringComparison.OrdinalIgnoreCase) &&
                AdapterFor(system)?.DecodeMode(system.CustomMode) is { Length: > 0 } mode &&
                mode is not ("Guided" or "Stabilize" or "Acro" or "AltHold" or "Sport" or "Loiter" or "PosHold"))
            {
                modeContext = $" The vehicle is currently in {mode}; select a supported manual mode such as Stabilize, AltHold, or Loiter before arming.";
            }
        }

        return $"ArduPilot rejected {action}: {acknowledgement.ResultCode}." +
               (modeContext ?? " No vehicle status reason was reported.");
    }

    private static string? LatestArduPilotCommandReason(SystemState system, DateTimeOffset now)
    {
        var active = system.ActiveTextBlockers
            .Where(item => now - item.Value <= TimeSpan.FromSeconds(10))
            .OrderByDescending(item => item.Value)
            .Select(item => item.Key)
            .FirstOrDefault(IsArduPilotCommandReason);
        if (!string.IsNullOrWhiteSpace(active))
        {
            return active;
        }

        return system.RecentDiagnosticMessages
            .Where(item => now - item.Timestamp <= TimeSpan.FromSeconds(3))
            .OrderByDescending(item => item.Timestamp)
            .Select(item => item.Text)
            .FirstOrDefault(IsArduPilotCommandReason);
    }

    private static bool IsArduPilotCommandReason(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           (text.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Arming denied:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Preflight Fail:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Arm:", StringComparison.OrdinalIgnoreCase));

    private async Task RequestTelemetryStreamsAsync(byte systemId)
    {
        ushort[] messageIds =
        [
            (ushort)MavlinkMessageIds.SystemStatus,
            (ushort)MavlinkMessageIds.GpsRawInt,
            (ushort)MavlinkMessageIds.Attitude,
            (ushort)MavlinkMessageIds.LocalPositionNed,
            (ushort)MavlinkMessageIds.GlobalPositionInt,
            (ushort)MavlinkMessageIds.HomePosition,
            (ushort)MavlinkMessageIds.BatteryStatus,
            (ushort)MavlinkMessageIds.EstimatorStatus,
            (ushort)MavlinkMessageIds.Vibration,
            (ushort)MavlinkMessageIds.RadioStatus,
            (ushort)MavlinkMessageIds.RcChannels,
            (ushort)MavlinkMessageIds.AutopilotVersion,
            (ushort)MavlinkMessageIds.ExtendedSystemState
        ];
        foreach (var messageId in messageIds)
        {
            try
            {
                var intervalMicroseconds = messageId is
                    (ushort)MavlinkMessageIds.Attitude or (ushort)MavlinkMessageIds.LocalPositionNed or (ushort)MavlinkMessageIds.GlobalPositionInt
                    ? 200_000f
                    : messageId is (ushort)MavlinkMessageIds.EstimatorStatus or (ushort)MavlinkMessageIds.Vibration
                        ? 500_000f
                    : 1_000_000f;
                var command = new MavlinkCommandEnvelope(
                    MavlinkCommandIds.SetMessageInterval,
                    [messageId, intervalMicroseconds, 0, 0, 0, 0, 0],
                    $"Request message {messageId}");
                byte componentId;
                lock (_gate)
                {
                    if (!_systems.TryGetValue(systemId, out var system))
                    {
                        return;
                    }
                    componentId = system.ComponentId;
                }
                await SendCommandAsync(systemId, componentId, command, _sessionCancellation?.Token ?? default);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not request MAVLink message {MessageId}", messageId);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
                var heartbeat = _codec.EncodeHeartbeat(
                    options.SourceSystemId,
                    options.SourceComponentId);
                MavlinkTransportRoute[] routes;
                lock (_gate)
                {
                    routes = _systems.Values
                        .Select(item => item.Route)
                        .OfType<MavlinkTransportRoute>()
                        .Distinct()
                        .ToArray();
                }

                if (routes.Length == 0 && _bootstrapRoute is not null)
                {
                    routes = [_bootstrapRoute];
                }

                foreach (var route in routes)
                {
                    await _transport.SendAsync(heartbeat, route, cancellationToken);
                }
                SystemState[] systems;
                lock (_gate)
                {
                    systems = _systems.Values
                        .Where(item => item.Route is not null)
                        .ToArray();
                }
                foreach (var system in systems)
                {
                    var sentMicroseconds =
                        (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
                    var ping = _codec.EncodePing(
                        options.SourceSystemId,
                        options.SourceComponentId,
                        sentMicroseconds,
                        unchecked(++_pingSequence),
                        system.SystemId,
                        system.ComponentId);
                    await _transport.SendAsync(
                        ping,
                        system.Route!,
                        cancellationToken);
                }
                EvaluateFreshness(DateTimeOffset.UtcNow, SystemStaleAfter, SystemOfflineAfter);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendBootstrapHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_bootstrapRoute is null) return;

        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
        var heartbeat = _codec.EncodeHeartbeat(options.SourceSystemId, options.SourceComponentId);
        await _transport.SendAsync(heartbeat, _bootstrapRoute, cancellationToken);
        _logger.LogDebug("Sent MAVLink bootstrap heartbeat to {BootstrapRoute} for {ConnectionId}.",
            _bootstrapRoute.DisplayName, Definition.Id);
    }

    private void ProcessStatusText(SystemState system, MavlinkPacket packet)
    {
        var text = packet.Text("text");
        var id = packet.UInt16("id");
        var sequence = packet.Byte("chunk_seq");
        if (id != 0)
        {
            if (sequence == 0 || !system.StatusTextChunks.TryGetValue(id, out var chunks))
            {
                chunks = new List<string>();
                system.StatusTextChunks[id] = chunks;
            }
            while (chunks.Count <= sequence) chunks.Add(string.Empty);
            chunks[sequence] = text;
            text = string.Concat(chunks);
            if (packet.Text("text").Length >= 50) return;
            system.StatusTextChunks.Remove(id);
        }

        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var severityValue = packet.Byte("severity", 6);
        if (severityValue > 4) return;
        var severity = severityValue switch
        {
            0 => VehicleDiagnosticSeverity.Emergency,
            1 or 2 => VehicleDiagnosticSeverity.Critical,
            3 => VehicleDiagnosticSeverity.Error,
            _ => VehicleDiagnosticSeverity.Warning
        };
        var existing = system.RecentDiagnosticMessages.LastOrDefault(item =>
            string.Equals(item.Text, text, StringComparison.OrdinalIgnoreCase) &&
            packet.ReceivedAt - item.Timestamp < TimeSpan.FromSeconds(2));
        if (existing is null)
        {
            system.RecentDiagnosticMessages.Enqueue(new VehicleDiagnosticMessage(
                $"{VehicleId(system.SystemId)}:statustext:{packet.ReceivedAt.ToUnixTimeMilliseconds()}:{StableTextId(text)}",
                packet.ReceivedAt, severity, text, ConfiguredAutopilotName));
            while (system.RecentDiagnosticMessages.Count > 100) system.RecentDiagnosticMessages.Dequeue();
            AddEvent(severity.ToString(), ConfiguredAutopilotName, text, system.SystemId);
        }
        if (text.StartsWith("Preflight Fail:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Arming denied:", StringComparison.OrdinalIgnoreCase) ||
            (IsArduPilotSystem(system) &&
             (text.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
              text.StartsWith("Arm:", StringComparison.OrdinalIgnoreCase))))
        {
            system.ActiveTextBlockers[text] = packet.ReceivedAt;
        }
    }

    private static bool IsReceiverPreArmBlocker(string text)
        => text.Contains("RC", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("receiver", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("radio", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidRcChannels(SystemState system, MavlinkPacket packet)
    {
        // ArduPilot may continue publishing the last channel values while the
        // receiver is in failsafe. RC_CHANNELS.rssi is vehicle-side receiver
        // evidence; a telemetry RADIO_STATUS packet is not a substitute.
        var receiverRssi = packet.Byte("rssi", byte.MaxValue);
        var validChannels = 0;
        for (var channel = 1; channel <= 18; channel++)
        {
            var raw = packet.UInt16($"chan{channel}_raw", ushort.MaxValue);
            if (raw is >= 900 and <= 2100 && ++validChannels >= 2)
            {
                if (receiverRssi is > 0 and < byte.MaxValue)
                {
                    return true;
                }

                // Some receivers do not provide RSSI in RC_CHANNELS. In
                // that case accept the channel evidence only after a newer
                // SYS_STATUS explicitly reports the RC receiver healthy.
                return system.SensorsUpdatedAt is { } statusAt &&
                       statusAt > LatestReceiverBlockerAt(system) &&
                       HasHealthyRcReceiver(system);
            }
        }

        return false;
    }

    private static bool HasHealthyRcReceiver(SystemState system)
        => system.SensorsPresent is { } present &&
           system.SensorsEnabled is { } enabled &&
           system.SensorsHealthy is { } healthy &&
           (present & MavlinkValues.MavSysStatusSensorRcReceiver) != 0 &&
           (enabled & MavlinkValues.MavSysStatusSensorRcReceiver) != 0 &&
           (healthy & MavlinkValues.MavSysStatusSensorRcReceiver) != 0;

    private static DateTimeOffset LatestReceiverBlockerAt(SystemState system)
        => system.ActiveTextBlockers
            .Where(item => IsReceiverPreArmBlocker(item.Key))
            .Select(item => item.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

    private static string DescribeMissionResult(byte result) => result switch
    {
        0 => "accepted",
        1 => "generic mission error",
        2 => "unsupported mission frame",
        3 => "unsupported mission command",
        4 => "mission storage is full",
        5 => "mission is invalid",
        6 => "invalid parameter 1",
        7 => "invalid parameter 2",
        8 => "invalid parameter 3",
        9 => "invalid parameter 4",
        10 => "invalid parameter 5",
        11 => "invalid parameter 6",
        12 => "invalid parameter 7",
        13 => "invalid mission sequence",
        14 => "mission was denied",
        15 => "mission operation was cancelled",
        _ => $"MAV_MISSION_RESULT_{result}"
    };

    private async Task<(bool Accepted, bool Verified, string Message)> CheckArduPilotManualInputAsync(
        byte systemId,
        byte componentId,
        CancellationToken cancellationToken)
    {
        var sourceSystemId = (Definition.Mavlink ?? new MavlinkConnectionOptions()).SourceSystemId;
        // These reads are independent. Keep admission bounded by one parameter
        // timeout rather than multiplying the timeout by the number of optional
        // compatibility parameters on a constrained ArduPilot link.
        var values = await Task.WhenAll(
            ReadManualParameterAsync(systemId, componentId, "MAV_GCS_SYSID", cancellationToken),
            ReadManualParameterAsync(systemId, componentId, "MAV_OPTIONS", cancellationToken),
            ReadManualParameterAsync(systemId, componentId, "RC_OPTIONS", cancellationToken));
        var gcsSysId = values[0];
        var mavOptions = values[1];
        var rcOptions = values[2];

        if (gcsSysId is { } configuredGcs &&
            Math.Abs(configuredGcs - sourceSystemId) > 0.5)
        {
            return (false, true,
                $"ArduPilot will ignore MAVLink pilot input from system {sourceSystemId}: MAV_GCS_SYSID is {configuredGcs:0}.");
        }

        // MAV_OPTIONS bit 0 enforces the configured GCS system ID. If it is
        // enabled, the exact MAV_GCS_SYSID match above is required. A value of
        // zero is valid and does not block input by itself.
        if (mavOptions is { } options && (Convert.ToInt64(Math.Round(options)) & 1L) != 0 &&
            gcsSysId is null)
        {
            return (true, false,
                "ArduPilot MAV_OPTIONS enforces MAV_GCS_SYSID, but MAV_GCS_SYSID could not be read; verify it matches Robot Command's source system ID.");
        }

        // RC_OPTIONS bit 1 means Ignore RC overrides from the GCS. That is an
        // explicit incompatibility with this MANUAL_CONTROL path.
        if (rcOptions is { } rc && (Convert.ToInt64(Math.Round(rc)) & (1L << 1)) != 0)
        {
            return (false, true,
                $"ArduPilot RC_OPTIONS={rc:0} is configured to ignore MAVLink pilot input (bit 1).");
        }

        var verified = gcsSysId is not null && mavOptions is not null && rcOptions is not null;
        return (true, verified, verified
            ? "ArduPilot MAVLink pilot-input parameters are compatible."
            : "ArduPilot pilot-input parameters were not all reported.");
    }

    private async Task<double?> ReadManualParameterAsync(
        byte systemId,
        byte componentId,
        string parameterName,
        CancellationToken cancellationToken)
    {
        const byte parameterComponentId = MavlinkValues.MavCompIdAutopilot1;
        SystemState system;
        MavlinkTransportRoute route;
        TaskCompletionSource<MavlinkParameterValue>? waiter = null;
        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();

        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out system!) || system.Route is not { } knownRoute)
                return null;
            if (system.Parameters.TryGetValue((parameterComponentId, parameterName), out var cached))
                return cached.Value;
            route = knownRoute;
            waiter = new TaskCompletionSource<MavlinkParameterValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            system.ParameterWaiters[(parameterComponentId, parameterName)] = waiter;
        }

        try
        {
            await _transport.SendAsync(
                _codec.EncodeParameterRequestRead(options.SourceSystemId, options.SourceComponentId,
                    systemId, componentId, parameterName, -1), route, cancellationToken);
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(750, cancellationToken));
            return completed == waiter.Task ? (await waiter.Task).Value : null;
        }
        finally
        {
            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var current) &&
                    current.ParameterWaiters.TryGetValue((parameterComponentId, parameterName), out var active) &&
                    ReferenceEquals(active, waiter))
                    current.ParameterWaiters.Remove((parameterComponentId, parameterName));
            }
        }
    }

    private bool HasValidGlobalPosition(byte systemId)
    {
        lock (_gate)
        {
            return _systems.TryGetValue(systemId, out var system) &&
                   system.LatitudeDegrees is { } lat && system.LongitudeDegrees is { } lon &&
                   double.IsFinite(lat) && double.IsFinite(lon) &&
                   Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 &&
                   !(Math.Abs(lat) < 0.000001 && Math.Abs(lon) < 0.000001);
        }
    }

    private async Task RunArduPilotManualPumpAsync(byte systemId, ArduPilotManualSession session)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(session.Cancellation.Token))
            {
                lock (_gate)
                {
                    if (!_systems.TryGetValue(systemId, out var system) ||
                        system.Availability is AvailabilityState.Stale or AvailabilityState.Offline)
                    {
                        session.Failure = "ArduPilot telemetry or MAVLink route is no longer current.";
                        return;
                    }

                    var adapter = AdapterFor(system);
                    var mode = adapter?.DecodeMode(system.CustomMode) ?? string.Empty;
                    if (adapter?.ClassifyManualControlMode(mode) == ManualControlModeClass.Unsupported)
                    {
                        session.Failure = $"ArduPilot left a manual-control mode (current mode: {mode}).";
                        return;
                    }

                    if (system.RecentDiagnosticMessages.Any(item =>
                            item.Text.Contains("failsafe", StringComparison.OrdinalIgnoreCase) &&
                            DateTimeOffset.UtcNow - item.Timestamp < TimeSpan.FromSeconds(3)))
                    {
                        session.Failure = "ArduPilot manual input stopped because a failsafe was reported.";
                        return;
                    }
                }

                var result = await SendManualControlFrameAsync(systemId, session.Setpoint, session.Profile, session.Cancellation.Token);
                session.LastSentAt = DateTimeOffset.UtcNow;
                session.SentSamples++;
                if (!result.Accepted)
                {
                    session.Failure = result.Message;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            session.Failure = ex.Message;
            Log.ManualStreamFailed(_logger, ex, Definition.Id, systemId);
        }
    }

    private async Task<MavlinkManualControlDispatchResult> StopArduPilotManualControlAsync(
        byte systemId,
        ArduPilotManualSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        session.Cancellation.Cancel();
        try { if (session.Pump is not null) await session.Pump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
        catch { /* shutdown must not leave a manual-input pump running */ }

        var neutral = ManualControlSetpoint.Neutral(DateTimeOffset.UtcNow);
        for (var i = 0; i < 3; i++)
        {
            var sent = await SendManualControlFrameAsync(systemId, neutral with { Timestamp = DateTimeOffset.UtcNow }, session.Profile, cancellationToken);
            if (!sent.Accepted) return MavlinkManualControlDispatchResult.Rejected($"{reason} Neutral input failed: {sent.Message}");
            await Task.Delay(50, cancellationToken);
        }

        var targetMode = HasValidGlobalPosition(systemId) ? 5u : 2u;
        var targetName = targetMode == 5u ? "Loiter" : "AltHold";
        var requestedAt = DateTimeOffset.UtcNow;
        var mode = await SendModeAsync(systemId, targetMode, $"ArduPilot {targetName} safe release", cancellationToken);
        if (!mode.Accepted)
            return MavlinkManualControlDispatchResult.Rejected($"{reason} {mode.Message}");
        if (!await WaitForModeAsync(systemId, targetMode, requestedAt, cancellationToken))
            return MavlinkManualControlDispatchResult.Rejected($"{reason} ArduPilot did not confirm {targetName} after manual-control release.");
        return MavlinkManualControlDispatchResult.Success($"{reason} ArduPilot confirmed {targetName} safe release.");
    }

    private bool IsArduPilotSystem(SystemState system)
        => (Definition.Mavlink?.Autopilot ?? MavlinkAutopilotProfile.Px4) == MavlinkAutopilotProfile.ArduPilot ||
           system.MavAutopilot == MavlinkValues.MavAutopilotArduPilot;

    private static bool IsPersistentArduPilotBlocker(string text)
        => text.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Arming denied:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Preflight Fail:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Arm:", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresArduPilotGuidedMode(OperatorCommandKind command)
        => command is OperatorCommandKind.Takeoff or
            OperatorCommandKind.GoTo or
            OperatorCommandKind.ChangeAltitude or
            OperatorCommandKind.SetHeading;

    private static string OperatorCommandLabel(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.GoTo => "Go To",
            OperatorCommandKind.ChangeAltitude => "Change Altitude",
            OperatorCommandKind.SetHeading => "Set Heading",
            _ => command.ToString()
        };

    private VehicleDiagnosticsSnapshot ToDiagnostics(SystemState system)
    {
        var packetLoss = PacketLossPercent(system);
        if (Armed(system)) system.ActiveTextBlockers.Clear();
        else
        {
            foreach (var key in system.ActiveTextBlockers
                         .Where(item => system.LastMessageAt - item.Value > TimeSpan.FromSeconds(10) &&
                             !(IsArduPilotSystem(system) && IsPersistentArduPilotBlocker(item.Key)))
                         .Select(item => item.Key).ToArray())
                system.ActiveTextBlockers.Remove(key);
        }
        var adapter = AdapterFor(system);
        var backend = adapter?.DisplayName ?? ConfiguredAutopilotName;
        var provider = _diagnosticsProviders.TryGetValue(backend, out var selectedProvider)
            ? selectedProvider
            : _diagnosticsProviders[ConfiguredAutopilotName];
        return provider.Build(new MavlinkDiagnosticEvidence(
            VehicleId(system.SystemId), Definition.Id, system.SystemId, system.ComponentId,
            system.Availability, system.LastMessageAt, system.LastHeartbeatAt, AdapterFor(system) is not null,
            Armed(system), system.LandedState, adapter?.DecodeMode(system.CustomMode) ?? $"Mode 0x{system.CustomMode:X8}",
            system.Version, system.SensorsPresent, system.SensorsEnabled, system.SensorsHealthy,
            system.DropRateComm, system.CommunicationErrors, system.GpsFixType, system.GpsSatellites,
            system.GpsHorizontalAccuracyMetres, system.GpsVerticalAccuracyMetres, system.EstimatorFlags,
            system.LatitudeDegrees, system.LongitudeDegrees, system.AltitudeMslMetres,
            BatteryVoltageMillivolts(system), BatteryCurrentCentiamps(system), BatteryRemainingPercent(system),
            system.BatteryFaults, system.VibrationX, system.VibrationY, system.VibrationZ,
            system.Clipping0, system.Clipping1, system.Clipping2, packetLoss,
            RadioSnr(system.Rssi, system.Noise), system.RecentDiagnosticMessages.ToArray(),
            system.ActiveTextBlockers), DateTimeOffset.UtcNow);
    }

    private LogosConnectionObservation ToObservation(SystemState system)
    {
        var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
        var adapter = AdapterFor(system);
        var name = options.DisplayNameFor(system.SystemId);
        var runtimeId = RuntimeId(system.SystemId);
        var vehicleId = VehicleId(system.SystemId);
        var supported = adapter is not null;
        var diagnostics = ToDiagnostics(system);
        var health = supported ? diagnostics.OverallStatus.ToString() : "Unsupported";
        var readiness = supported ? diagnostics.ArmReadiness.ToString() : "Unsupported autopilot or vehicle type";
        var cameraCapabilities = _cameras.Values
            .Where(item => item.SystemId == system.SystemId)
            .SelectMany(item => new[]
            {
                item.CapabilityFlags == 0 || (item.CapabilityFlags & 2) != 0 ? "camera_photo" : null,
                item.CapabilityFlags == 0 || (item.CapabilityFlags & 1) != 0 ? "camera_video" : null
            })
            .Where(item => item is not null)
            .Cast<string>();
        var gimbalCapabilities = _gimbals.Values.Any(item => item.SystemId == system.SystemId)
            ? new[] { "gimbal" }
            : Array.Empty<string>();
        var capabilities = (adapter?.CapabilityKeys ?? [])
            .Concat(cameraCapabilities)
            .Concat(gimbalCapabilities)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new LogosConnectionObservation(
            new RuntimeObservation(
                runtimeId,
                name,
                $"{adapter?.DisplayName ?? ConfiguredAutopilotName} Autopilot",
                (system.BaseMode & MavlinkValues.MavModeFlagCustomModeEnabled) != 0 ? "MAVLink 2" : "MAVLink",
                adapter?.DisplayName ?? ConfiguredAutopilotName,
                adapter?.VehicleClass(system.MavType) ?? $"MAV_TYPE_{system.MavType}",
                system.Version,
                health,
                readiness,
                supported ? $"MAVLINK_{diagnostics.OverallStatus.ToString().ToUpperInvariant()}" : "MAVLINK_UNSUPPORTED",
                supported ? diagnostics.Summary : "This MAVLink system is diagnostic-only.",
                capabilities),
            null,
            new VehicleObservation(
                vehicleId,
                name,
                runtimeId,
                null,
                adapter?.VehicleClass(system.MavType) ?? $"MAV_TYPE_{system.MavType}",
                adapter?.Domain(system.MavType) ?? "Unknown",
                $"mavlink:{ConfiguredAutopilotName.ToLowerInvariant()}:{system.MavType}",
                readiness,
                system.LandedState,
                Armed(system) ? "Armed" : "Disarmed",
                health,
                capabilities),
            system.Availability,
            system.LastMessageAt);
    }

    private VehicleTelemetryRecord ToTelemetry(SystemState system)
    {
        var adapter = AdapterFor(system);
        var diagnostics = ToDiagnostics(system);
        return new VehicleTelemetryRecord(
            $"{VehicleId(system.SystemId)}:{Definition.Id}",
            VehicleId(system.SystemId),
            Definition.Id,
            RuntimeId(system.SystemId),
            system.Availability,
            Armed(system),
            system.LandedState,
            adapter?.DecodeMode(system.CustomMode) ?? $"Mode 0x{system.CustomMode:X8}",
            $"MAVLink {system.ProtocolVersion}",
            adapter is null ? "Unsupported" : diagnostics.OverallStatus.ToString(),
            adapter is null ? "Unsupported" : diagnostics.ArmReadiness.ToString(),
            system.LatitudeDegrees,
            system.LongitudeDegrees,
            system.AltitudeMslMetres,
            system.AltitudeAglMetres,
            null,
            null,
            null,
            system.VelocityNorth,
            system.VelocityEast,
            system.VelocityDown,
            system.HeadingDegrees,
            system.Availability is AvailabilityState.Stale or AvailabilityState.Offline,
            adapter is null ? "MAVLINK_UNSUPPORTED" : $"MAVLINK_{diagnostics.OverallStatus.ToString().ToUpperInvariant()}",
            adapter is null
                ? $"System {system.SystemId} is not a supported {ConfiguredAutopilotName} multicopter."
                : diagnostics.Summary,
            system.LastMessageAt,
            IsGhost: false,
            GimbalPitchDegrees: GimbalTelemetryFor(system)?.PitchDegrees,
            GimbalYawDegrees: GimbalTelemetryFor(system)?.YawDegrees,
            GimbalRollDegrees: GimbalTelemetryFor(system)?.RollDegrees,
            CameraZoomPercent: CameraFor(system)?.ZoomPercent,
            CameraRecordingVideo: CameraFor(system)?.RecordingVideo,
            BatteryRemainingPercent: BatteryRemainingPercent(system),
            BatteryVoltageVolts: BatteryVoltageMillivolts(system) is { } voltage ? voltage / 1000d : null,
            BatteryObservedAt: BatteryObservedAt(system),
            GimbalYawInEarthFrame: GimbalTelemetryFor(system)?.YawInEarthFrame);
    }

    private MavlinkGimbalState? GimbalFor(SystemState system)
        => _gimbals.Values
            .Where(item => item.SystemId == system.SystemId)
            .OrderByDescending(item => item.ManagerInformationReported)
            .ThenBy(item => item.ComponentId)
            .FirstOrDefault();

    private MavlinkGimbalState? GimbalTelemetryFor(SystemState system)
        => _gimbals.Values
            .Where(item => item.SystemId == system.SystemId &&
                          (item.PitchDegrees is not null || item.YawDegrees is not null || item.RollDegrees is not null))
            .OrderByDescending(item => item.LastMessageAt)
            .FirstOrDefault();

    private MavlinkCameraState? CameraFor(SystemState system)
        => _cameras.Values
            .Where(item => item.SystemId == system.SystemId)
            .OrderBy(item => item.ComponentId)
            .FirstOrDefault();

    private static string StableTextId(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];

    private LinkRecord ToLink(SystemState system)
    {
        var packetLoss = PacketLossPercent(system);
        var quality = Math.Clamp(1d - packetLoss / 100d, 0, 1);
        var signalDegraded = system.SignalState is SignalHealth.Degraded or SignalHealth.Severe;
        var transport = _transport.Statistics;
        return new LinkRecord(
            $"{Definition.Id}:mavlink:{system.SystemId}",
            $"mavlink-{system.SystemId}",
            Definition.Id,
            RuntimeId(system.SystemId),
            $"{ConfiguredAutopilotName} MAVLink system {system.SystemId}",
            _transport.TransportName,
            "Bidirectional",
            signalDegraded ? system.SignalState.ToString() : system.Availability.ToString(),
            signalDegraded ? $"Signal {system.SignalState.ToString().ToLowerInvariant()}" : system.Availability == AvailabilityState.Online ? "Healthy" : system.Availability.ToString(),
            system.Availability == AvailabilityState.Online ? "Ready" : "Not ready",
            system.Availability == AvailabilityState.Online,
            system.Availability is AvailabilityState.Stale or AvailabilityState.Offline,
            system.Route?.DisplayName,
            ConfiguredAutopilotName,
            (Definition.Mavlink ?? new MavlinkConnectionOptions()).DisplayNameFor(system.SystemId),
            RadioDbm(system.Rssi),
            RadioSnr(system.Rssi, system.Noise),
            quality,
            packetLoss,
            system.LatencyMilliseconds,
            _transport.TransportName == "UDP" ? "MAVLINK_UDP" : "MAVLINK_SERIAL",
            $"MAVLink {system.ProtocolVersion}; heartbeat {system.LastHeartbeatAt:O}; " +
            $"local RSSI {FormatRadio(system.Rssi)}, remote RSSI {FormatRadio(system.RemoteRssi)}, " +
            $"tx buffer {FormatPercent(system.TransmitBufferPercent)}, rx errors {FormatCounter(system.ReceiveErrors)}, corrected {FormatCounter(system.CorrectedPackets)}; " +
            $"transport RX/TX {transport.BytesReceived}/{transport.BytesSent} bytes, " +
            $"MAVLink 1/2 frames {Interlocked.Read(ref _mavlinkOneFrames)}/{Interlocked.Read(ref _mavlinkTwoFrames)}, " +
            $"framing errors {transport.FramingErrors}, decode/CRC errors {Interlocked.Read(ref _decodeErrors)}.",
            system.LastMessageAt);
    }

    private LinkRecord ToTransportLink()
    {
        var transport = _transport.Statistics;
        return new LinkRecord(
            $"{Definition.Id}:serial-transport",
            "serial-transport",
            Definition.Id,
            null,
            $"SiK serial transport {_transport.LocalEndpoint}",
            _transport.TransportName,
            "Bidirectional",
            State.ToString(),
            $"No {ConfiguredAutopilotName} heartbeat",
            "Waiting for remote radio / aircraft",
            true,
            true,
            null,
            "SiK",
            _transport.LocalEndpoint,
            null,
            null,
            null,
            null,
            null,
            "MAVLINK_SERIAL_WAITING",
            $"Port open; RX/TX {transport.BytesReceived}/{transport.BytesSent} bytes, " +
            $"frames {transport.FramesReceived} (MAVLink 1 {Interlocked.Read(ref _mavlinkOneFrames)}, " +
            $"MAVLink 2 {Interlocked.Read(ref _mavlinkTwoFrames)}), framing errors {transport.FramingErrors}, " +
            $"decode/CRC errors {Interlocked.Read(ref _decodeErrors)}. {transport.Detail}",
            transport.LastByteAt ?? LastAttempt ?? DateTimeOffset.UtcNow);
    }

    private static void EvaluateSignalHealth(SystemState system, DateTimeOffset now)
    {
        var packetLoss = PacketLossPercent(system);
        var snr = RadioSnr(system.Rssi, system.Noise);
        var severe = packetLoss >= 30 || snr is < 6 || system.TransmitBufferPercent is < 10;
        var degraded = severe || packetLoss >= 10 || snr is < 10 || system.TransmitBufferPercent is < 20;
        if (degraded)
        {
            system.SignalUnhealthySince ??= now;
            system.SignalHealthySince = null;
            if (now - system.SignalUnhealthySince >= TimeSpan.FromSeconds(5))
            {
                system.SignalState = severe ? SignalHealth.Severe : SignalHealth.Degraded;
            }
            return;
        }

        system.SignalUnhealthySince = null;
        if (system.SignalState == SignalHealth.Healthy) return;
        system.SignalHealthySince ??= now;
        if (now - system.SignalHealthySince >= TimeSpan.FromSeconds(10))
        {
            system.SignalState = SignalHealth.Healthy;
            system.SignalHealthySince = null;
        }
    }

    private static string FormatRadio(byte? value) => RadioDbm(value) is { } dbm ? $"{dbm:F1} dBm (raw {value})" : "not reported";
    private static string FormatPercent(byte? value) => value is null or byte.MaxValue ? "not reported" : $"{value}%";
    private static string FormatCounter(ushort? value) => value is null or ushort.MaxValue ? "not reported" : value.Value.ToString(CultureInfo.InvariantCulture);

    private void EvaluateOperation(SystemState system)
    {
        if (system.ActiveOperation is not { } operation)
        {
            return;
        }

        if (TryGetArduPilotInterruption(system, operation, out var interruption))
        {
            UpdateCommand(
                operation.CommandId,
                OperationalCommandState.Cancelled,
                interruption.Message,
                interruption.Code);
            system.ActiveOperation = null;
            return;
        }

        if (!OperationCompleted(system, operation))
        {
            return;
        }

        var shouldAutomaticallyHold = AdapterFor(system) is { } completedAdapter &&
            ShouldAutomaticallyHoldAfterCompletion(completedAdapter, operation.Command);

        UpdateCommand(
            operation.CommandId,
            OperationalCommandState.Succeeded,
            operation.AcknowledgementReceived
                ? $"{ConfiguredAutopilotName} telemetry confirmed {operation.Command} completion."
                : $"{ConfiguredAutopilotName} telemetry indicates {operation.Command} completed, but no MAVLink acknowledgement was received.",
            operation.AcknowledgementReceived
                ? "MAVLINK_OPERATION_COMPLETED"
                : "MAVLINK_OPERATION_CONFIRMED_BY_TELEMETRY");
        system.ActiveOperation = null;
        if (shouldAutomaticallyHold)
        {
            _ = TransitionToHoldAfterCompletionAsync(system.SystemId, system.ComponentId);
        }
    }

    private static bool ShouldAutomaticallyHoldAfterCompletion(
        IMavlinkAutopilotAdapter adapter,
        OperatorCommandKind command)
    {
        // The adapter owns the backend-specific Hold mode. For ArduPilot this
        // is Brake; manual-control release is intentionally handled elsewhere.
        _ = adapter;
        return command is
            OperatorCommandKind.GoTo or
            OperatorCommandKind.ChangeAltitude or
            OperatorCommandKind.SetHeading or
            OperatorCommandKind.Recover;
    }

    private async Task TransitionToHoldAfterCompletionAsync(byte systemId, byte componentId)
    {
        try
        {
            MavlinkCommandEnvelope hold;
            IMavlinkAutopilotAdapter adapter;
            lock (_gate)
            {
                if (!_systems.TryGetValue(systemId, out var system))
                {
                    return;
                }

                adapter = AdapterFor(system)!;
                if (adapter is null)
                {
                    return;
                }

                var request = new OperatorCommandRequest(
                    $"automatic-hold-{Definition.Id}-{systemId}-{Guid.NewGuid():N}",
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    OperatorCommandKind.Hold,
                    new OperatorCommandTarget(
                        Definition.Id,
                        VehicleId(systemId),
                        null,
                        DateTimeOffset.UtcNow),
                    "Automatic hold after movement completion",
                    false,
                    DateTimeOffset.UtcNow,
                    Parameters: OperatorCommandParameters.None);
                hold = adapter.BuildCommand(request, ToTelemetry(system));
            }

            var requestedAt = DateTimeOffset.UtcNow;
            AddEvent(
                "Info",
                "mavlink-command",
                $"Dispatching automatic Hold after movement completion (MAV_CMD {hold.CommandId}, {hold.WireKind}).",
                systemId,
                "MAVLINK_AUTOMATIC_HOLD_DISPATCH",
                $"automatic-hold:{systemId}");
            var ack = await SendCommandAsync(
                systemId,
                componentId,
                hold,
                _sessionCancellation?.Token ?? CancellationToken.None);
            if (!ack.Accepted)
            {
                AddEvent(
                    "Warning",
                    "mavlink-command",
                    $"Automatic Hold was rejected: {ack.ResultName}.",
                    systemId);
                return;
            }

            if (!await WaitForModeAsync(systemId, adapter.HoldMode, requestedAt, _sessionCancellation?.Token ?? CancellationToken.None))
            {
                AddEvent(
                    "Warning",
                    "mavlink-command",
                    $"Automatic {adapter.HoldModeName} was selected but mode confirmation was not received.",
                    systemId);
                return;
            }

            if (adapter.HoldPolicy.RequiresStableTelemetry &&
                !await WaitForStableHoldAsync(systemId, adapter.HoldPolicy, requestedAt, _sessionCancellation?.Token ?? CancellationToken.None))
            {
                AddEvent(
                    "Warning",
                    "mavlink-command",
                    $"Automatic Hold entered {adapter.HoldModeName}, but stable position/altitude telemetry was not confirmed.",
                    systemId);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.HoldFailed(_logger, ex, ConfiguredAutopilotName, systemId);
        }
    }

    private bool OperationCompleted(SystemState system, ActiveOperation operation)
    {
        // An ACK only proves that the autopilot accepted the request.  Never
        // complete from the pre-dispatch snapshot (for example an Arm request
        // sent while the cached heartbeat still says armed).  Completion must
        // be based on a heartbeat received after dispatch.
        if (operation.Command != OperatorCommandKind.Hold &&
            system.LastHeartbeatAt <= operation.StartedAt)
        {
            return false;
        }

        var parameters = operation.Parameters;
        return operation.Command switch
        {
            OperatorCommandKind.Arm => !operation.InitiallyArmed && Armed(system),
            OperatorCommandKind.Disarm => operation.InitiallyArmed && !Armed(system),
            OperatorCommandKind.Hold => HoldCompleted(system, operation),
            OperatorCommandKind.Takeoff =>
                system.LandedState == "Flying" &&
                Near(system.AltitudeAglMetres, parameters.TakeoffAltitudeAglMetres, 0.75),
            OperatorCommandKind.Land =>
                system.LandedState == "Landed" ||
                system.AltitudeAglMetres is <= 0.2,
            OperatorCommandKind.GoTo =>
                HorizontalDistance(
                    system.LatitudeDegrees,
                    system.LongitudeDegrees,
                    parameters.GoToLatitudeDegrees,
                    parameters.GoToLongitudeDegrees) <=
                Math.Max(0.5, parameters.GoToAcceptanceRadiusMetres ?? 2) &&
                (parameters.GoToAltitudeAmslMetres is null || Near(system.AltitudeMslMetres, parameters.GoToAltitudeAmslMetres, 1)),
            OperatorCommandKind.ChangeAltitude =>
                Near(system.AltitudeMslMetres, operation.TargetAltitudeMslMetres, 0.75),
            OperatorCommandKind.SetHeading =>
                HeadingComplete(system, operation.TargetHeadingDegrees),
            OperatorCommandKind.Recover =>
                RecoverCompleted(system),
            _ => false
        };
    }

    private static bool RecoverCompleted(SystemState system)
    {
        // RTL is presented by Robot Command as return-and-hold. Do not wait
        // for ArduPilot/PX4's native RTL landing sequence to disarm the
        // vehicle. Once the vehicle reaches its authoritative HOME_POSITION,
        // the normal completion path requests backend Hold (Brake for
        // ArduPilot). Landing remains a safe fallback when home evidence is
        // unavailable or the autopilot completes RTL before it is observed.
        if (system.LatitudeDegrees is { } latitude &&
            system.LongitudeDegrees is { } longitude &&
            system.HomeLatitudeDegrees is { } homeLatitude &&
            system.HomeLongitudeDegrees is { } homeLongitude &&
            HorizontalDistance(latitude, longitude, homeLatitude, homeLongitude) <= 5)
        {
            return true;
        }

        return system.LandedState == "Landed" ||
               (!Armed(system) && system.AltitudeAglMetres is <= 0.2);
    }

    private bool HoldCompleted(SystemState system, ActiveOperation operation)
    {
        var adapter = AdapterFor(system);
        if (adapter?.IsHoldMode(system.CustomMode) != true)
        {
            return false;
        }

        var policy = adapter.HoldPolicy;
        if (!policy.RequiresStableTelemetry)
        {
            return true;
        }

        return IsStableHoldSample(system, policy, operation.HoldStability, operation.StartedAt);
    }

    private static bool IsStableHoldSample(
        SystemState system,
        MavlinkHoldPolicy policy,
        HoldStabilityState stability,
        DateTimeOffset requestedAt)
    {
        if (system.LastPositionAt <= requestedAt ||
            system.LatitudeDegrees is not { } latitude ||
            system.LongitudeDegrees is not { } longitude ||
            system.AltitudeAglMetres is not { } altitude ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(altitude))
        {
            return false;
        }

        var horizontalSpeed = system.VelocityNorth is { } north && system.VelocityEast is { } east
            ? Math.Sqrt(north * north + east * east)
            : 0;
        var verticalSpeed = system.VelocityDown is { } down ? Math.Abs(down) : 0;
        if (horizontalSpeed > policy.MaximumHorizontalSpeedMetresPerSecond ||
            verticalSpeed > policy.MaximumVerticalSpeedMetresPerSecond)
        {
            stability.Reset();
            return false;
        }

        if (stability.AnchorLatitudeDegrees is not { } anchorLatitude ||
            stability.AnchorLongitudeDegrees is not { } anchorLongitude ||
            stability.AnchorAltitudeAglMetres is not { } anchorAltitude)
        {
            stability.AnchorLatitudeDegrees = latitude;
            stability.AnchorLongitudeDegrees = longitude;
            stability.AnchorAltitudeAglMetres = altitude;
            stability.StableSince = system.LastPositionAt;
            return false;
        }

        if (HorizontalDistance(latitude, longitude, anchorLatitude, anchorLongitude) > policy.PositionToleranceMetres ||
            Math.Abs(altitude - anchorAltitude) > policy.AltitudeToleranceMetres)
        {
            stability.Reset();
            return false;
        }

        return stability.StableSince is { } stableSince &&
               system.LastPositionAt - stableSince >= policy.StabilityWindow;
    }

    private bool TryGetArduPilotInterruption(
        SystemState system,
        ActiveOperation operation,
        out (string Code, string Message) interruption)
    {
        if (AdapterFor(system)?.Profile != MavlinkAutopilotProfile.ArduPilot ||
            operation.Command is not (OperatorCommandKind.Hold or OperatorCommandKind.Takeoff or
                OperatorCommandKind.GoTo or
                OperatorCommandKind.ChangeAltitude or
                OperatorCommandKind.SetHeading))
        {
            interruption = default;
            return false;
        }

        if (system.Availability is AvailabilityState.Stale or AvailabilityState.Offline)
        {
            interruption = (
                "ARDUPILOT_LINK_LOSS",
                $"ArduPilot {OperatorCommandLabel(operation.Command)} was interrupted because telemetry is {system.Availability.ToString().ToLowerInvariant()}.");
            return true;
        }

        var expectedMode = operation.Command == OperatorCommandKind.Hold ? "Brake" : "Guided";
        var mode = AdapterFor(system)?.DecodeMode(system.CustomMode) ?? "Unknown";
        if (!string.Equals(mode, expectedMode, StringComparison.OrdinalIgnoreCase))
        {
            var failsafe = system.RecentDiagnosticMessages.LastOrDefault(item =>
                item.Text.Contains("failsafe", StringComparison.OrdinalIgnoreCase));
            interruption = failsafe is not null
                ? (
                    "ARDUPILOT_FAILSAFE",
                    $"ArduPilot {OperatorCommandLabel(operation.Command)} was interrupted by failsafe: {failsafe.Text}")
                : (
                    operation.Command == OperatorCommandKind.Hold ? "ARDUPILOT_HOLD_MODE_LOSS" : "ARDUPILOT_MODE_LOSS",
                    $"ArduPilot {OperatorCommandLabel(operation.Command)} was interrupted because the vehicle left {expectedMode} mode (current mode: {mode}).");
            return true;
        }

        interruption = default;
        return false;
    }

    private static bool HeadingComplete(SystemState system, double? target)
    {
        if (system.HeadingDegrees is null || target is null)
        {
            return false;
        }
        return Math.Abs(NormalizeSignedHeading(system.HeadingDegrees.Value - target.Value)) <= 3;
    }

    private IMavlinkAutopilotAdapter? AdapterFor(SystemState system)
    {
        var configured = Definition.Mavlink?.Autopilot ?? MavlinkAutopilotProfile.Px4;
        return _adapters.FirstOrDefault(item =>
            item.Profile == configured &&
            item.Supports(system.MavAutopilot, system.MavType));
    }

    private static void TrackSequence(SystemState system, MavlinkPacket packet)
    {
        if (!system.ComponentSequences.TryGetValue(packet.ComponentId, out var sequence))
        {
            sequence = new ComponentSequenceState();
            system.ComponentSequences[packet.ComponentId] = sequence;
        }

        if (sequence.LastSequence is { } previous)
        {
            var expected = unchecked((byte)(previous + 1));
            var gap = unchecked((byte)(packet.Sequence - expected));
            if (gap is > 0 and < 128)
            {
                sequence.LostPackets += gap;
            }
        }
        sequence.LastSequence = packet.Sequence;
        sequence.ReceivedPackets++;
    }

    private void TrackEarlyPacket(byte systemId, MavlinkPacket packet, MavlinkTransportRoute route)
    {
        if (!_systems.TryGetValue(systemId, out var system)) return;
        if (packet.ComponentId == system.ComponentId)
        {
            system.LastMessageAt = packet.ReceivedAt;
            system.Route = _bootstrapRoute ?? route;
        }
        TrackSequence(system, packet);
    }

    private static double PacketLossPercent(SystemState system)
    {
        if (!system.ComponentSequences.TryGetValue(system.ComponentId, out var sequence))
        {
            return 0;
        }

        var received = Math.Max(1, sequence.ReceivedPackets);
        return sequence.LostPackets * 100d / (received + sequence.LostPackets);
    }

    private AvailabilityState BestConnectionState()
    {
        if (_systems.Values.Any(item => item.Availability == AvailabilityState.Online))
        {
            return AvailabilityState.Online;
        }
        if (_systems.Values.Any(item => item.Availability == AvailabilityState.Stale))
        {
            return AvailabilityState.Stale;
        }
        if (_transport.KeepOpenWithoutHeartbeat && IsTransportOpen && _systems.Count == 0)
        {
            return AvailabilityState.Degraded;
        }
        return IsTransportOpen && HasConnectBeenRequested
            ? AvailabilityState.Reconnecting
            : AvailabilityState.Offline;
    }

    private void ScheduleReconnect()
    {
        var delays = new[] { 1, 2, 5, 10 };
        var seconds = delays[Math.Min(_reconnectAttempt, delays.Length - 1)];
        _reconnectAttempt++;
        NextReconnectAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private void AddEvent(
        string severity,
        string source,
        string message,
        byte systemId,
        string? code = null,
        string? subjectId = null)
    {
        _events.Enqueue(new ConsoleEventRecord(
            $"mavlink-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            severity,
            source,
            message,
            Definition.Id,
            RuntimeId(systemId),
            "MAVLink",
            "Connection",
            code,
            subjectId));
        while (_events.Count > 200)
        {
            _events.Dequeue();
        }

        var auditMessage = $"[{code ?? "MAVLINK_EVENT"}] system {systemId}: {message}";
        switch (severity)
        {
            case "Error":
                Log.AuditError(_logger, auditMessage);
                break;
            case "Warning":
                Log.AuditWarning(_logger, auditMessage);
                break;
            default:
                Log.AuditInformation(_logger, auditMessage);
                break;
        }
    }

    private void UpdateCommand(
        string commandId,
        OperationalCommandState state,
        string message,
        string? reason = null)
    {
        void Apply()
        {
            if (_commands.TryGet(commandId, out var command) && command is not null)
            {
                _commands.Upsert(command with
                {
                    State = state,
                    Message = message,
                    Reason = reason ?? command.Reason,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(Apply);
        }
    }

    private void RaiseChanged()
        => Changed?.Invoke(this, EventArgs.Empty);

    private string RuntimeId(byte systemId)
        => $"mavlink-runtime:{Definition.Id}:{systemId}";

    private string VehicleId(byte systemId)
        => $"mavlink:{Definition.Id}:{systemId}";

    private static bool Armed(SystemState system)
        => (system.BaseMode & MavlinkValues.MavModeFlagSafetyArmed) != 0;

    private static bool Near(double? actual, double? target, double tolerance)
        => actual is not null && target is not null &&
           Math.Abs(actual.Value - target.Value) <= tolerance;

    private static double HorizontalDistance(
        double? latitude,
        double? longitude,
        double? targetLatitude,
        double? targetLongitude)
    {
        if (latitude is null || longitude is null ||
            targetLatitude is null || targetLongitude is null)
        {
            return double.PositiveInfinity;
        }

        const double earthRadius = 6_371_000;
        var latitudeRadians = latitude.Value * Math.PI / 180;
        var targetLatitudeRadians = targetLatitude.Value * Math.PI / 180;
        var deltaLatitude = targetLatitudeRadians - latitudeRadians;
        var deltaLongitude = (targetLongitude.Value - longitude.Value) * Math.PI / 180;
        var a = Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2) +
                Math.Cos(latitudeRadians) * Math.Cos(targetLatitudeRadians) *
                Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double NormalizeHeading(double heading)
        => (heading % 360 + 360) % 360;

    private static float ToMavlinkFloat(double? value)
    {
        if (value is null) return float.NaN;
        var converted = (float)value.Value;
        return float.IsFinite(converted)
            ? converted
            : throw new InvalidOperationException("The camera or gimbal angle is outside the MAVLink float range.");
    }

    private static double? OperationTargetHeading(
        IMavlinkAutopilotAdapter adapter,
        OperatorCommandRequest request,
        VehicleTelemetryRecord telemetry,
        MavlinkCommandEnvelope command)
    {
        if (request.Command != OperatorCommandKind.SetHeading)
            return null;

        if (adapter.Profile == MavlinkAutopilotProfile.ArduPilot)
        {
            var parameters = request.Parameters ?? OperatorCommandParameters.None;
            if (telemetry.HeadingDegrees is not { } current || !double.IsFinite(current))
                return null;

            return parameters.HeadingTargetKind switch
            {
                OperatorHeadingTargetKind.AbsoluteHeading when parameters.HeadingDegrees is { } absolute && double.IsFinite(absolute)
                    => NormalizeHeading(absolute),
                OperatorHeadingTargetKind.RelativeYaw when parameters.RelativeYawDegrees is { } relative && double.IsFinite(relative)
                    => NormalizeHeading(current + relative),
                _ => null
            };
        }

        return command.Parameters.Length > 3 && float.IsFinite(command.Parameters[3])
            ? NormalizeHeading(command.Parameters[3])
            : null;
    }

    private async Task<MavlinkManualControlDispatchResult> SendManualControlFrameAsync(
        byte systemId,
        ManualControlSetpoint setpoint,
        ManualControlProfile profile,
        CancellationToken cancellationToken)
    {
        MavlinkTransportRoute route;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system) || system.Route is null)
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    $"{ConfiguredAutopilotName} manual control stopped because no remote MAVLink endpoint is known.");
            }
            route = system.Route;
        }

        try
        {
            var normalized = profile.Normalize();
            var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
            var bytes = _codec.EncodeManualControl(
                options.SourceSystemId,
                options.SourceComponentId,
                systemId,
                ToManualAxis(setpoint.BodyForwardMetresPerSecond, normalized.MaximumHorizontalSpeedMetresPerSecond),
                ToManualAxis(setpoint.BodyRightMetresPerSecond, normalized.MaximumHorizontalSpeedMetresPerSecond),
                ToManualThrottleAxis(setpoint.VerticalMetresPerSecond, normalized.MaximumVerticalSpeedMetresPerSecond),
                ToManualAxis(setpoint.YawRateDegreesPerSecond, normalized.MaximumYawRateDegreesPerSecond));
            await _transport.SendAsync(bytes, route, cancellationToken);
            return MavlinkManualControlDispatchResult.Success($"{ConfiguredAutopilotName} manual control setpoint sent.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return MavlinkManualControlDispatchResult.Rejected(
                $"{ConfiguredAutopilotName} manual-control transport failed: {ex.Message}");
        }
    }

    private async Task<bool> WaitForModeAsync(
        byte systemId,
        uint customMode,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        var deadline = requestedAt.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var system) &&
                    system.CustomMode == customMode &&
                    system.LastHeartbeatAt >= requestedAt)
                {
                    return true;
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitForStableHoldAsync(
        byte systemId,
        MavlinkHoldPolicy policy,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        var stability = new HoldStabilityState();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_systems.TryGetValue(systemId, out var system) &&
                    AdapterFor(system)?.IsHoldMode(system.CustomMode) == true &&
                    IsStableHoldSample(system, policy, stability, requestedAt))
                {
                    return true;
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        return false;
    }

    private async Task<MavlinkManualControlDispatchResult> SendModeAsync(
        byte systemId,
        uint customMode,
        string description,
        CancellationToken cancellationToken)
    {
        MavlinkTransportRoute route;
        lock (_gate)
        {
            if (!_systems.TryGetValue(systemId, out var system) || system.Route is null)
            {
                return MavlinkManualControlDispatchResult.Rejected(
                    $"{description} could not be selected because no remote MAVLink endpoint is known.");
            }
            route = system.Route;
        }

        try
        {
            var options = Definition.Mavlink ?? new MavlinkConnectionOptions();
            var bytes = _codec.EncodeSetMode(
                options.SourceSystemId,
                options.SourceComponentId,
                systemId,
                MavlinkValues.MavModeFlagCustomModeEnabled,
                customMode);
            await _transport.SendAsync(bytes, route, cancellationToken);
            return MavlinkManualControlDispatchResult.Success($"{description} selection sent to {ConfiguredAutopilotName}.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return MavlinkManualControlDispatchResult.Rejected(
                    $"{description} selection could not be sent: {ex.Message}");
        }
    }

    private static short ToManualAxis(double value, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(maximum) || maximum <= 0)
        {
            return 0;
        }
        return (short)Math.Round(Math.Clamp(value / maximum, -1, 1) * 1000);
    }

    // MAVLink MANUAL_CONTROL uses the long-established joystick convention for
    // z: 0 = minimum thrust, 500 = centered/altitude hold, 1000 = maximum thrust.
    private static short ToManualThrottleAxis(double value, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(maximum) || maximum <= 0)
        {
            return 500;
        }
        return (short)Math.Round(500 + Math.Clamp(value / maximum, -1, 1) * 500);
    }

    private static double NormalizeSignedHeading(double heading)
        => (heading + 540) % 360 - 180;

    private static double? RadioDbm(byte? raw)
        => raw is null or 0 or byte.MaxValue ? null : raw.Value / 1.9 - 127;

    private static double? RadioSnr(byte? signal, byte? noise)
        => signal is null || noise is null ? null : (signal.Value - noise.Value) / 1.9;

    private static string DecodeFlightVersion(uint value)
        => value == 0
            ? "Not reported"
            : $"{(value >> 24) & 0xff}.{(value >> 16) & 0xff}.{(value >> 8) & 0xff}";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SystemState(byte systemId)
    {
        public byte SystemId { get; } = systemId;
        public byte ComponentId { get; set; } = MavlinkValues.MavCompIdAutopilot1;
        public byte MavType { get; set; }
        public byte MavAutopilot { get; set; }
        public byte BaseMode { get; set; }
        public uint CustomMode { get; set; }
        public byte SystemStatus { get; set; }
        public byte ProtocolVersion { get; set; } = 2;
        public AvailabilityState Availability { get; set; } = AvailabilityState.Connecting;
        public DateTimeOffset LastHeartbeatAt { get; set; }
        public DateTimeOffset LastMessageAt { get; set; }
        public DateTimeOffset LastPositionAt { get; set; }
        public MavlinkTransportRoute? Route { get; set; }
        public double? LatitudeDegrees { get; set; }
        public double? LongitudeDegrees { get; set; }
        public double? HomeLatitudeDegrees { get; set; }
        public double? HomeLongitudeDegrees { get; set; }
        public DateTimeOffset? HomePositionAt { get; set; }
        public double? AltitudeMslMetres { get; set; }
        public double? AltitudeAglMetres { get; set; }
        public double? LocalNorth { get; set; }
        public double? LocalEast { get; set; }
        public double? LocalDown { get; set; }
        public double? VelocityNorth { get; set; }
        public double? VelocityEast { get; set; }
        public double? VelocityDown { get; set; }
        public double? HeadingDegrees { get; set; }
        public string LandedState { get; set; } = "Unknown";
        public string Health { get; set; } = "Unknown";
        public string Version { get; set; } = "Not reported";
        public uint? SensorsPresent { get; set; }
        public uint? SensorsEnabled { get; set; }
        public uint? SensorsHealthy { get; set; }
        public DateTimeOffset? SensorsUpdatedAt { get; set; }
        public ushort? DropRateComm { get; set; }
        public ushort? CommunicationErrors { get; set; }
        public byte? GpsFixType { get; set; }
        public byte? GpsSatellites { get; set; }
        public double? GpsHorizontalAccuracyMetres { get; set; }
        public double? GpsVerticalAccuracyMetres { get; set; }
        public uint? EstimatorFlags { get; set; }
        public ushort? BatteryVoltageMillivolts { get; set; }
        public short? BatteryCurrentCentiamps { get; set; }
        public sbyte? BatteryRemainingPercent { get; set; }
        public DateTimeOffset? BatteryObservedAt { get; set; }
        public ushort? BatteryStatusVoltageMillivolts { get; set; }
        public short? BatteryStatusCurrentCentiamps { get; set; }
        public sbyte? BatteryStatusRemainingPercent { get; set; }
        public DateTimeOffset? BatteryStatusObservedAt { get; set; }
        public uint? BatteryFaults { get; set; }
        public float? VibrationX { get; set; }
        public float? VibrationY { get; set; }
        public float? VibrationZ { get; set; }
        public uint? Clipping0 { get; set; }
        public uint? Clipping1 { get; set; }
        public uint? Clipping2 { get; set; }
        public byte? Rssi { get; set; }
        public byte? RemoteRssi { get; set; }
        public byte? Noise { get; set; }
        public byte? RemoteNoise { get; set; }
        public byte? TransmitBufferPercent { get; set; }
        public ushort? ReceiveErrors { get; set; }
        public ushort? CorrectedPackets { get; set; }
        public SignalHealth SignalState { get; set; }
        public DateTimeOffset? SignalUnhealthySince { get; set; }
        public DateTimeOffset? SignalHealthySince { get; set; }
        public double? LatencyMilliseconds { get; set; }
        public Dictionary<byte, ComponentSequenceState> ComponentSequences { get; } = [];
        public Dictionary<ushort, List<string>> StatusTextChunks { get; } = [];
        public Queue<VehicleDiagnosticMessage> RecentDiagnosticMessages { get; } = [];
        public Dictionary<string, DateTimeOffset> ActiveTextBlockers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ActiveOperation? ActiveOperation { get; set; }
        public Dictionary<(byte ComponentId, string Name), MavlinkParameterValue> Parameters { get; } = [];
        public TaskCompletionSource<bool>? ParameterSignal { get; set; }
        public Dictionary<(byte ComponentId, string Name), TaskCompletionSource<MavlinkParameterValue>> ParameterWaiters { get; } = [];
        public int? CurrentMissionItem { get; set; }
        public int? LastReachedMissionItem { get; set; }
        public DateTimeOffset MissionUpdatedAt { get; set; }
        public byte? MissionState { get; set; }
        public DateTimeOffset? ManualInputEchoAt { get; set; }
    }

    private sealed class ComponentSequenceState
    {
        public byte? LastSequence { get; set; }
        public long ReceivedPackets { get; set; }
        public long LostPackets { get; set; }
    }

    private sealed class MavlinkCameraState(byte systemId, byte componentId)
    {
        public byte SystemId { get; } = systemId;
        public byte ComponentId { get; } = componentId;
        public MavlinkTransportRoute? Route { get; set; }
        public AvailabilityState Availability { get; set; } = AvailabilityState.Connecting;
        public DateTimeOffset LastHeartbeatAt { get; set; }
        public DateTimeOffset LastMessageAt { get; set; }
        public string VendorName { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public uint CapabilityFlags { get; set; }
        public string FirmwareVersion { get; set; } = "Not reported";
        public ushort DefinitionVersion { get; set; }
        public string DefinitionUri { get; set; } = string.Empty;
        public bool DefinitionLoadRequested { get; set; }
        public double? ZoomPercent { get; set; }
        public bool? RecordingVideo { get; set; }
        public IReadOnlyList<MavlinkCameraSettingRecord> Settings { get; set; } = [];
        public string DefinitionStatus { get; set; } = "Discovered";
    }

    private sealed class MavlinkGimbalState(byte systemId, byte componentId)
    {
        public byte SystemId { get; } = systemId;
        public byte ComponentId { get; } = componentId;
        public MavlinkTransportRoute? Route { get; set; }
        public AvailabilityState Availability { get; set; } = AvailabilityState.Connecting;
        public DateTimeOffset LastHeartbeatAt { get; set; }
        public DateTimeOffset LastMessageAt { get; set; }
        public uint CapabilityFlags { get; set; }
        public bool CapabilityFlagsReported { get; set; }
        public byte DeviceId { get; set; }
        public bool ManagerInformationReported { get; set; }
        public double? PitchDegrees { get; set; }
        public double? YawDegrees { get; set; }
        public bool? YawInEarthFrame { get; set; }
        public double? RollDegrees { get; set; }
    }

    private sealed class ActiveOperation(
        string commandId,
        OperatorCommandKind command,
        OperatorCommandParameters parameters,
        DateTimeOffset startedAt,
        bool initiallyArmed,
        DateTimeOffset expiresAt,
        double? targetAltitudeMslMetres,
        double? targetHeadingDegrees,
        HoldStabilityState? holdStability,
        bool acknowledgementReceived)
    {
        public string CommandId { get; } = commandId;
        public OperatorCommandKind Command { get; } = command;
        public OperatorCommandParameters Parameters { get; } = parameters;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool InitiallyArmed { get; } = initiallyArmed;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public double? TargetAltitudeMslMetres { get; } = targetAltitudeMslMetres;
        public double? TargetHeadingDegrees { get; } = targetHeadingDegrees;
        public HoldStabilityState HoldStability { get; } = holdStability ?? new();
        public bool AcknowledgementReceived { get; } = acknowledgementReceived;
    }

    private sealed class HoldStabilityState
    {
        public double? AnchorLatitudeDegrees { get; set; }
        public double? AnchorLongitudeDegrees { get; set; }
        public double? AnchorAltitudeAglMetres { get; set; }
        public DateTimeOffset? StableSince { get; set; }

        public void Reset()
        {
            AnchorLatitudeDegrees = null;
            AnchorLongitudeDegrees = null;
            AnchorAltitudeAglMetres = null;
            StableSince = null;
        }
    }

    private sealed class Px4FormationSession : IDisposable
    {
        public Px4FormationSession(string lockId, Px4FormationSetpoint setpoint, CancellationTokenSource cancellation)
        {
            LockId = lockId;
            Setpoint = setpoint;
            Cancellation = cancellation;
        }

        public string LockId { get; }
        public CancellationTokenSource Cancellation { get; }
        public Px4FormationSetpoint Setpoint { get; set; }
        public Task? Pump { get; set; }
        public bool OffboardConfirmed { get; set; }
        public DateTimeOffset LastSetpointAt { get; set; }
        public string? Failure { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ArduPilotFormationSession : IDisposable
    {
        public ArduPilotFormationSession(
            string lockId,
            ArduPilotFormationSetpoint setpoint,
            CancellationTokenSource cancellation)
        {
            LockId = lockId;
            Setpoint = setpoint;
            Cancellation = cancellation;
        }

        public string LockId { get; }
        public CancellationTokenSource Cancellation { get; }
        public ArduPilotFormationSetpoint Setpoint { get; set; }
        public Task? Pump { get; set; }
        public bool GuidedConfirmed { get; set; }
        public DateTimeOffset LastSetpointAt { get; set; }
        public string? Failure { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ArduPilotManualSession : IDisposable
    {
        public ArduPilotManualSession(
            ManualControlSetpoint setpoint,
            ManualControlProfile profile,
            CancellationTokenSource cancellation)
        {
            Setpoint = setpoint;
            Profile = profile;
            Cancellation = cancellation;
            StartedAt = DateTimeOffset.UtcNow;
        }

        public ManualControlSetpoint Setpoint { get; set; }
        public ManualControlProfile Profile { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public Task? Pump { get; set; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset LastSentAt { get; set; }
        public string? Failure { get; set; }
        public bool ParameterAdmissionVerified { get; set; }
        public long SentSamples { get; set; }
        public string? SafeReleaseMode { get; set; }
        public bool? SafeReleaseConfirmed { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }

    private readonly record struct CommandKey(byte SystemId, byte ComponentId, ushort CommandId);
    private readonly record struct MissionTransferKey(byte SystemId, byte ComponentId, byte MissionType);

    private enum MissionSignalKind { Request, Ack, Count, Item }
    private sealed record MissionSignal(MissionSignalKind Kind, ushort Sequence, byte Result = 0, MavlinkMissionItem? Item = null, bool UsesIntegerItems = true);
    private sealed class MissionTransferSession
    {
        private TaskCompletionSource<MissionSignal> _signal = NewSignal();
        private MissionTransferSession(byte missionType, byte componentId, int uploadCount)
        {
            MissionType = missionType;
            ComponentId = componentId;
            UploadCount = uploadCount;
        }

        public byte MissionType { get; }
        public byte ComponentId { get; }
        public int UploadCount { get; }
        public int ExpectedCount { get; set; }
        public ushort? LastRequestedSequence { get; set; }
        public List<MavlinkMissionItem> Items { get; } = [];
        public TaskCompletionSource<MissionSignal> Signal => _signal;
        public ushort NextMissingSequence => checked((ushort)Enumerable.Range(0, ExpectedCount).First(sequence => Items.All(item => item.Sequence != sequence)));
        public bool HasAllItems => ExpectedCount > 0 && Items.Count == ExpectedCount &&
            Items.Select(item => item.Sequence).OrderBy(sequence => sequence).SequenceEqual(Enumerable.Range(0, ExpectedCount).Select(sequence => (ushort)sequence));

        public static MissionTransferSession ForUpload(IReadOnlyList<MavlinkMissionItem> items, byte missionType, byte componentId)
            => new(missionType, componentId, items.Count);

        public static MissionTransferSession ForDownload(byte missionType, byte componentId)
            => new(missionType, componentId, 0);

        public bool IsValidRequestedSequence(ushort sequence)
            => sequence < UploadCount;

        public bool IsValidReceivedSequence(ushort sequence)
            => ExpectedCount > 0 && sequence < ExpectedCount;

        public void Set(MissionSignal signal) => _signal.TrySetResult(signal);
        public void ResetSignal() => _signal = NewSignal();
        private static TaskCompletionSource<MissionSignal> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum SignalHealth { Healthy, Degraded, Severe }
}

public sealed record MavlinkCommandAck(
    ushort CommandId,
    byte Result,
    int ResultParam2,
    byte Progress)
{
    public bool Accepted => Result is MavlinkValues.MavResultAccepted or MavlinkValues.MavResultInProgress;

    public string ResultName => Result switch
    {
        MavlinkValues.MavResultAccepted => "Accepted",
        MavlinkValues.MavResultTemporarilyRejected => "Temporarily rejected",
        MavlinkValues.MavResultDenied => "Denied",
        MavlinkValues.MavResultUnsupported => "Unsupported",
        MavlinkValues.MavResultFailed => "Failed",
        MavlinkValues.MavResultInProgress => "In progress",
        MavlinkValues.MavResultCancelled => "Cancelled",
        255 => "No acknowledgement",
        _ => $"MAV_RESULT_{Result}"
    };

    public string ResultCode => Result switch
    {
        MavlinkValues.MavResultAccepted => "MAV_RESULT_ACCEPTED",
        MavlinkValues.MavResultTemporarilyRejected => "MAV_RESULT_TEMPORARILY_REJECTED",
        MavlinkValues.MavResultDenied => "MAV_RESULT_DENIED",
        MavlinkValues.MavResultUnsupported => "MAV_RESULT_UNSUPPORTED",
        MavlinkValues.MavResultFailed => "MAV_RESULT_FAILED",
        MavlinkValues.MavResultInProgress => "MAV_RESULT_IN_PROGRESS",
        MavlinkValues.MavResultCancelled => "MAV_RESULT_CANCELLED",
        255 => "MAV_RESULT_NO_ACKNOWLEDGEMENT",
        _ => $"MAV_RESULT_{Result}"
    };
}

public sealed record MavlinkCommandDispatchResult(
    bool Accepted,
    OperationalCommandState State,
    string Message,
    ushort? MavlinkCommandId = null)
{
    public static MavlinkCommandDispatchResult Rejected(string message)
        => new(false, OperationalCommandState.Rejected, message);
}

public sealed record MavlinkManualControlDispatchResult(bool Accepted, string Message)
{
    public static MavlinkManualControlDispatchResult Success(string message) => new(true, message);

    public static MavlinkManualControlDispatchResult Rejected(string message) => new(false, message);
}
