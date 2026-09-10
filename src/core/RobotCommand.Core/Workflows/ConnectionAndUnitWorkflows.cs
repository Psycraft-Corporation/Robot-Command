namespace RobotCommand.Core;

/// <summary>Front-end-neutral modes supported by saved Robot Command connections.</summary>
public enum ManagedConnectionMode
{
    Direct,
    FieldLink,
    Mavlink
}

public enum ManagedConnectionState
{
    Unknown,
    Connecting,
    Reconnecting,
    Online,
    Degraded,
    Stale,
    Offline,
    Faulted
}

public enum ManagedMavlinkTransport { UdpListener, Serial }
public enum ManagedMavlinkAutopilot { Px4, ArduPilot }

public sealed record ManagedMavlinkOptions(
    ManagedMavlinkTransport Transport = ManagedMavlinkTransport.UdpListener,
    ManagedMavlinkAutopilot Autopilot = ManagedMavlinkAutopilot.Px4,
    byte SourceSystemId = 255,
    byte SourceComponentId = 190,
    IReadOnlyDictionary<byte, string>? SystemAliases = null,
    int? BaudRate = null,
    string? SerialDeviceId = null,
    string? LastKnownPort = null);

public sealed record ManagedLinkdOptions(
    string TransportPlugin = "sik_serial",
    int BaudRate = 57600,
    string? SerialDeviceId = null,
    string? LastKnownPort = null,
    string? RadioProfileKey = null,
    string? WireProfilePath = null);

/// <summary>Validated front-end input for creating or editing a saved connection.</summary>
public sealed record ConnectionMutationRequest(
    string Name,
    string Target,
    ManagedConnectionMode Mode = ManagedConnectionMode.Direct,
    bool AutoConnect = false,
    bool AutoReconnect = true,
    string? Description = null,
    ManagedMavlinkOptions? Mavlink = null,
    ManagedLinkdOptions? Linkd = null,
    string? Id = null);

/// <summary>Ephemeral credentials for one Direct Logos connection attempt.</summary>
public sealed record ConnectionCredentialInput(string? ApiKey = null, string? BearerToken = null)
{
    public bool HasSecrets => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(BearerToken);
}

public sealed record ManagedConnectionSnapshot(
    string Id,
    string Name,
    string Target,
    ManagedConnectionMode Mode,
    ManagedConnectionState State,
    bool AutoConnect,
    bool AutoReconnect,
    string? Description,
    ManagedMavlinkOptions? Mavlink,
    ManagedLinkdOptions? Linkd,
    string? RuntimeInstanceId,
    string? RuntimeRole,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastSeen,
    DateTimeOffset? LastAttempt,
    string? LastError);

public sealed record UnitTelemetryObservation(
    ManagedConnectionState State,
    bool Armed,
    string LandedState,
    string Mode,
    double? LatitudeDegrees,
    double? LongitudeDegrees,
    double? AltitudeMslMetres,
    double? AltitudeAglMetres,
    double? VelocityNorthMetresPerSecond,
    double? VelocityEastMetresPerSecond,
    double? VelocityDownMetresPerSecond,
    double? HeadingDegrees,
    bool IsStale,
    DateTimeOffset? ObservedAt,
    double? BatteryRemainingPercent = null,
    double? BatteryVoltageVolts = null,
    DateTimeOffset? BatteryObservedAt = null,
    double? GimbalPitchDegrees = null,
    double? GimbalYawDegrees = null,
    double? GimbalRollDegrees = null,
    double? CameraZoomPercent = null,
    bool? CameraRecordingVideo = null);

public sealed record UnitDiagnosticsObservation(
    string OverallStatus,
    string Summary,
    string ArmReadiness,
    string ArmReadinessDetail,
    string NavigationReadiness,
    string NavigationReadinessDetail,
    string TelemetryStatus,
    string TelemetryDetail,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    DateTimeOffset? ObservedAt,
    string FirmwareVersion = "Not reported");

public sealed record UnitLinkObservation(
    string ConnectionId,
    string Name,
    string State,
    bool Connected,
    bool IsStale,
    double? RssiDbm,
    double? SnrDb,
    double? Quality,
    double? PacketLoss,
    double? LatencyMilliseconds,
    DateTimeOffset ObservedAt);

public sealed record UnitConnectionObservation(
    string ConnectionId,
    string Name,
    string Backend,
    string Target,
    ManagedConnectionState State,
    bool IsCommandAuthority,
    bool IsTelemetryAuthority,
    bool IsDiagnosticsAuthority,
    DateTimeOffset? LastSeen);

public sealed record UnitActionObservation(string? CurrentAction, string? QueuedAction);

/// <summary>Read-only, reconciled unit projection for GUI and CLI consumers.</summary>
public sealed record UnitObservationSnapshot(
    string Id,
    string Name,
    IReadOnlyList<string> ConnectionIds,
    string? LogosInstanceId,
    string? TeamId,
    string VehicleClass,
    string Domain,
    string ProfileKey,
    ManagedConnectionState State,
    string Readiness,
    string Lifecycle,
    string ArmState,
    string Health,
    IReadOnlyList<string> CapabilityKeys,
    DateTimeOffset? LastSeen,
    bool IsGhost,
    string? CommandAuthorityVehicleId,
    string? TelemetryAuthorityVehicleId,
    string? DiagnosticsAuthorityVehicleId,
    UnitTelemetryObservation? Telemetry,
    UnitDiagnosticsObservation? Diagnostics,
    IReadOnlyList<UnitLinkObservation> Links,
    UnitActionObservation Actions,
    IReadOnlyList<UnitConnectionObservation>? AssociatedConnections = null,
    double? DistanceFromOperatorMetres = null,
    bool CanAcceptOperatorCommands = true)
{
    public string? GhostProfileId { get; init; }
    public string? GhostProfileName { get; init; }
    public string? GhostProfileModel { get; init; }
    public GhostSimulationStats? GhostSimulationStats { get; init; }
}

public interface IConnectionManagementWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<ManagedConnectionSnapshot> Connections { get; }
    bool TryGet(string connectionId, out ManagedConnectionSnapshot? connection);
    Task<ManagedConnectionSnapshot> CreateAsync(ConnectionMutationRequest request, CancellationToken cancellationToken = default);
    Task<ManagedConnectionSnapshot> UpdateAsync(string connectionId, ConnectionMutationRequest request, CancellationToken cancellationToken = default);
    Task RemoveAsync(string connectionId, CancellationToken cancellationToken = default);
    Task ConnectAsync(string connectionId, ConnectionCredentialInput? credentials = null, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default);
    Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default);
}

public interface IUnitObservationWorkflow
{
    event EventHandler? Changed;
    IReadOnlyList<UnitObservationSnapshot> Units { get; }
    bool TryGet(string unitId, out UnitObservationSnapshot? unit);
}

/// <summary>Explicit connection supervision for non-GUI hosts.</summary>
public interface IConnectionRuntimeLifecycle : IAsyncDisposable
{
    Task StartAsync(bool autoConnect, string? connectionId = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
