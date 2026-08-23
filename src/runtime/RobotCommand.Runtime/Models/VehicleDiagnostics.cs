namespace RobotCommand.Models;

public enum VehicleDiagnosticStatus
{
    Unknown,
    Ready,
    Limited,
    Blocked,
    Stale,
    Offline
}

public enum VehicleDiagnosticCheckState
{
    Passed,
    Warning,
    Failed,
    Unknown,
    NotApplicable
}

public enum VehicleDiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Critical,
    Emergency
}

public sealed record VehicleDiagnosticCheck(
    string Code,
    string Category,
    string Name,
    VehicleDiagnosticCheckState State,
    string Detail,
    IReadOnlyList<OperatorCommandKind>? AffectedOperations = null)
{
    public string StatusSymbol => State switch
    {
        VehicleDiagnosticCheckState.Passed => "\u2713",
        VehicleDiagnosticCheckState.Warning => "!",
        VehicleDiagnosticCheckState.Failed => "\u2715",
        VehicleDiagnosticCheckState.NotApplicable => "\u2014",
        _ => "?"
    };

    public string StatusBrush => State switch
    {
        VehicleDiagnosticCheckState.Passed => "#32D583",
        VehicleDiagnosticCheckState.Warning => "#F5C451",
        VehicleDiagnosticCheckState.Failed => "#F05252",
        VehicleDiagnosticCheckState.NotApplicable => "#8A96A8",
        _ => "#8A96A8"
    };
}

public sealed record VehicleDiagnosticMessage(
    string Id,
    DateTimeOffset Timestamp,
    VehicleDiagnosticSeverity Severity,
    string Text,
    string Source = "Vehicle");

public sealed record VehicleDiagnosticsSnapshot(
    string Id,
    string VehicleId,
    string ConnectionId,
    string Backend,
    VehicleDiagnosticStatus OverallStatus,
    string Summary,
    VehicleDiagnosticStatus ArmReadiness,
    string ArmReadinessDetail,
    VehicleDiagnosticStatus NavigationReadiness,
    string NavigationReadinessDetail,
    VehicleDiagnosticStatus TelemetryStatus,
    string TelemetryDetail,
    IReadOnlyList<VehicleDiagnosticCheck> Checks,
    IReadOnlyList<VehicleDiagnosticMessage> RecentMessages,
    DateTimeOffset ObservedAt,
    byte? SystemId = null,
    byte? ComponentId = null,
    string Version = "Not reported",
    string Mode = "Unknown",
    bool Armed = false,
    string LandedState = "Unknown",
    double? BatteryRemainingPercent = null,
    double? BatteryVoltageVolts = null)
{
    public IReadOnlyList<VehicleDiagnosticCheck> Blockers => Checks
        .Where(item => item.State == VehicleDiagnosticCheckState.Failed)
        .ToArray();

    public IReadOnlyList<VehicleDiagnosticCheck> Warnings => Checks
        .Where(item => item.State == VehicleDiagnosticCheckState.Warning)
        .ToArray();
}
