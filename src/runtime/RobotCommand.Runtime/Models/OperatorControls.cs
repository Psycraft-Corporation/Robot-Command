namespace RobotCommand.Models;

public enum OperatorCommandKind
{
    Arm,
    Disarm,
    Hold,
    Takeoff,
    GoTo,
    Land,
    Recover,
    ChangeAltitude,
    SetHeading,
    CapturePhoto,
    StartVideo,
    StopVideo,
    CenterGimbal,
    NadirGimbal,
    SetGimbal
}

public enum OperatorCommandSafety
{
    Routine,
    Elevated,
    Critical
}

public enum OperatorControlAvailability
{
    Ready,
    Warning,
    Blocked,
    Unavailable
}

public enum OperatorPreflightSeverity
{
    Info,
    Warning,
    Blocking
}

public sealed record OperatorPreflightFinding(
    string Code,
    OperatorPreflightSeverity Severity,
    string Message,
    string Source = "Robot Command");

public sealed record OperatorPolicyRequest(
    string Action,
    string VehicleId,
    string? LogosInstanceId,
    string CorrelationId,
    string ContextJson);

public sealed record OperatorPolicyFinding(
    string Code,
    string Severity,
    string Message,
    string RecommendedAction = "");

public sealed record OperatorPolicyEvaluation(
    bool Evaluated,
    bool Allowed,
    string Decision,
    string Summary,
    IReadOnlyList<OperatorPolicyFinding> Findings)
{
    public static OperatorPolicyEvaluation Unavailable(string message) => new(
        false,
        false,
        "Unavailable",
        message,
        Array.Empty<OperatorPolicyFinding>());
}

public sealed record OperatorCommandTarget(
    string ConnectionId,
    string VehicleId,
    string? LogosInstanceId,
    DateTimeOffset CapturedAt);

public enum OperatorGoToTargetKind
{
    GlobalWgs84,
    LocalNed
}

public enum OperatorAltitudeTargetKind
{
    AltitudeAmsl,
    AltitudeAgl,
    RelativeDelta
}

public enum OperatorHeadingTargetKind
{
    AbsoluteHeading,
    RelativeYaw
}

/// <summary>
/// Typed parameters for bounded vehicle operations. Only the fields applicable
/// to the selected command are sent to Logos.
/// </summary>
public sealed record OperatorCommandParameters(
    double? TakeoffAltitudeAglMetres = null,
    OperatorGoToTargetKind? GoToTargetKind = null,
    double? GoToLatitudeDegrees = null,
    double? GoToLongitudeDegrees = null,
    double? GoToAltitudeAmslMetres = null,
    double? GoToNorthMetres = null,
    double? GoToEastMetres = null,
    double? GoToDownMetres = null,
    double? GoToYawDegrees = null,
    double? GoToAcceptanceRadiusMetres = null,
    OperatorAltitudeTargetKind? AltitudeTargetKind = null,
    double? AltitudeAmslMetres = null,
    double? AltitudeAglMetres = null,
    double? AltitudeRelativeDeltaMetres = null,
    OperatorHeadingTargetKind? HeadingTargetKind = null,
    double? HeadingDegrees = null,
    double? RelativeYawDegrees = null,
    bool AirborneDisarmConfirmed = false,
    double? GimbalPitchDegrees = null,
    double? GimbalYawDegrees = null,
    double? GimbalRollDegrees = null,
    double? GimbalZoomPercent = null,
    bool GimbalEarthFrame = false)
{
    public static OperatorCommandParameters None { get; } = new();

    public static OperatorCommandParameters GlobalGoTo(
        double? latitudeDegrees,
        double? longitudeDegrees,
        double? altitudeAmslMetres,
        double? acceptanceRadiusMetres)
        => new(
            GoToTargetKind: OperatorGoToTargetKind.GlobalWgs84,
            GoToLatitudeDegrees: latitudeDegrees,
            GoToLongitudeDegrees: longitudeDegrees,
            GoToAltitudeAmslMetres: altitudeAmslMetres,
            GoToAcceptanceRadiusMetres: acceptanceRadiusMetres);

    public static OperatorCommandParameters LocalGoTo(
        double? northMetres,
        double? eastMetres,
        double? downMetres,
        double? yawDegrees,
        double? acceptanceRadiusMetres)
        => new(
            GoToTargetKind: OperatorGoToTargetKind.LocalNed,
            GoToNorthMetres: northMetres,
            GoToEastMetres: eastMetres,
            GoToDownMetres: downMetres,
            GoToYawDegrees: yawDegrees,
            GoToAcceptanceRadiusMetres: acceptanceRadiusMetres);

    public static OperatorCommandParameters ChangeAltitudeAmsl(double? altitudeMetres)
        => new(
            AltitudeTargetKind: OperatorAltitudeTargetKind.AltitudeAmsl,
            AltitudeAmslMetres: altitudeMetres);

    public static OperatorCommandParameters ChangeAltitudeAgl(double? altitudeMetres)
        => new(
            AltitudeTargetKind: OperatorAltitudeTargetKind.AltitudeAgl,
            AltitudeAglMetres: altitudeMetres);

    public static OperatorCommandParameters ChangeAltitudeRelative(double? deltaMetres)
        => new(
            AltitudeTargetKind: OperatorAltitudeTargetKind.RelativeDelta,
            AltitudeRelativeDeltaMetres: deltaMetres);

    public static OperatorCommandParameters AbsoluteHeading(double? headingDegrees)
        => new(
            HeadingTargetKind: OperatorHeadingTargetKind.AbsoluteHeading,
            HeadingDegrees: headingDegrees);

    public static OperatorCommandParameters RelativeYaw(double? relativeYawDegrees)
        => new(
            HeadingTargetKind: OperatorHeadingTargetKind.RelativeYaw,
            RelativeYawDegrees: relativeYawDegrees);
}

/// <summary>
/// Server-authoritative preparation produced by VehicleOperationsService.
/// The immutable target and confirmation token must be returned unchanged when
/// the operator executes the command.
/// </summary>
public sealed record PreparedVehicleOperation(
    PreparedOperationReference Reference,
    PreparedOperationTargetSnapshot Target,
    string OperationType,
    string AuthorizationDecision,
    string Readiness,
    IReadOnlyList<string> Warnings,
    DateTimeOffset PreparedAt,
    DateTimeOffset ExpiresAt)
{
    public bool Expired => DateTimeOffset.UtcNow >= ExpiresAt;
}

public sealed record OperatorCommandRequest(
    string CommandId,
    string CorrelationId,
    string IdempotencyKey,
    OperatorCommandKind Command,
    OperatorCommandTarget Target,
    string Reason,
    bool Emergency,
    DateTimeOffset CreatedAt,
    PreparedVehicleOperation? Preparation = null,
    OperatorCommandParameters? Parameters = null);

public sealed record OperatorGatewayStatus(
    bool Available,
    string Message);

/// <summary>
/// Connection-aware eligibility for acquiring a manual-control session. This is
/// intentionally separate from dispatching an operator command: a session may
/// be acquired while the vehicle is disarmed, but it must pass the same common
/// connection, telemetry, capability, and navigation checks as operator work.
/// </summary>
public sealed record ManualControlReadiness(
    bool IsReady,
    IReadOnlyList<OperatorPreflightFinding> Findings)
{
    public string Summary => Findings.FirstOrDefault(item => item.Severity == OperatorPreflightSeverity.Blocking)?.Message
        ?? "Manual control is ready.";
}

public sealed record OperatorCommandPreparationResult(
    bool Accepted,
    string Message,
    PreparedVehicleOperation? Preparation,
    IReadOnlyList<OperatorPreflightFinding> Findings)
{
    public static OperatorCommandPreparationResult Rejected(
        string message,
        string code = "VEHICLE_OPERATION_PREPARE_REJECTED")
        => new(
            false,
            message,
            null,
            [new OperatorPreflightFinding(
                code,
                OperatorPreflightSeverity.Blocking,
                message,
                "Logos VehicleOperationsService")]);
}

public sealed record OperatorCommandResult(
    bool Accepted,
    OperationalCommandState State,
    string Message,
    string? OperationId = null);

public sealed record OperatorCommandPlan(
    string CommandId,
    string CorrelationId,
    string IdempotencyKey,
    OperatorCommandKind Command,
    string DisplayName,
    OperatorCommandSafety Safety,
    OperatorControlAvailability Availability,
    OperatorCommandTarget Target,
    string TargetName,
    string Reason,
    bool Emergency,
    string ConfirmationPhrase,
    IReadOnlyList<OperatorPreflightFinding> Findings,
    OperatorPolicyEvaluation Policy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    PreparedVehicleOperation? Preparation = null,
    OperatorCommandParameters? Parameters = null)
{
    public bool RequiresTypedConfirmation => !string.IsNullOrWhiteSpace(ConfirmationPhrase);

    public bool CanSubmit =>
        (Availability is OperatorControlAvailability.Ready or OperatorControlAvailability.Warning) &&
        Preparation is not null &&
        !Preparation.Expired;
}
