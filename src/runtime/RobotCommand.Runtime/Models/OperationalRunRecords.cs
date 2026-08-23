namespace RobotCommand.Models;

public sealed record BehaviourGeometryRequirement(
    string SlotId,
    string KindHint,
    bool RequiredRegistration,
    bool RequireObjectAtStart,
    bool AllowEmptyGeometry,
    string ExpectedPolicyKind,
    string? DefaultGeometryId,
    string Description)
{
    public bool RequiresResolvedGeometry =>
        (RequiredRegistration || RequireObjectAtStart) && !AllowEmptyGeometry;

    public bool RequiresExternalGeometry =>
        RequiresResolvedGeometry && string.IsNullOrWhiteSpace(DefaultGeometryId);
}

public sealed record BehaviourPackageOption(
    string BehaviourId,
    string Version,
    string DisplayName,
    string Description,
    string Status,
    string Channel,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ProvidedCapabilities,
    IReadOnlyList<BehaviourGeometryRequirement> GeometrySlots,
    DateTimeOffset? UpdatedAt = null,
    BehaviourGeometryReadiness? GeometryReadiness = null,
    BehaviourParameterSchema? ParameterSchema = null)
{
    public string Key => $"{BehaviourId}@{Version}";

    public string Label => string.IsNullOrWhiteSpace(Version)
        ? DisplayName
        : $"{DisplayName} {Version}";

    public bool SupportsQuickRun =>
        GeometrySlots.All(slot => !slot.RequiresResolvedGeometry) ||
        GeometryReadiness?.Ready == true;

    public string GeometryBindingSummary
    {
        get
        {
            if (GeometrySlots.Count == 0) return "No geometry required";
            if (GeometryReadiness is null) return "Geometry bindings not inspected";
            return GeometryReadiness.Ready
                ? $"{GeometryReadiness.GeometryIds.Count} geometry object(s) ready"
                : string.Join(" ", GeometryReadiness.Blockers);
        }
    }
}

public sealed record OperationalRunRequest(
    string ConnectionId,
    string VehicleId,
    string BehaviourId,
    string BehaviourVersion,
    string Objective,
    string ParametersJson = "{}",
    string TaskType = "quick-run",
    string Priority = "Normal",
    string PolicyId = "",
    string? MissionName = null,
    string? TaskName = null,
    bool RejectOnWarnings = false);

public enum OperationalRunStage
{
    Draft,
    Prepared,
    PublishingMission,
    PublishingTask,
    ValidatingAssignment,
    AssigningTask,
    StartingMission,
    StartingTask,
    Running,
    Rejected,
    Failed,
    Cancelled
}

public sealed record OperationalRunPreparation(
    string OperationId,
    OperationalRunRequest Request,
    BehaviourPackageOption Behaviour,
    string MissionId,
    string TaskId,
    DocumentValidationResult MissionValidation,
    DocumentValidationResult TaskValidation,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    OperationalRunStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    BehaviourGeometryReadiness? GeometryReadiness = null,
    string BehaviourPackageSha256 = "",
    string BehaviourBindingFingerprint = "")
{
    public bool CanLaunch =>
        Stage == OperationalRunStage.Prepared &&
        MissionValidation.IsValid &&
        TaskValidation.IsValid &&
        Blockers.Count == 0 &&
        DateTimeOffset.UtcNow < ExpiresAt;
}

public sealed record OperationalRunStepResult(
    string Step,
    bool Accepted,
    OperationalCommandState State,
    string Message,
    string? ExecutionId = null,
    string? LifecycleState = null);

public sealed record OperationalRunResult(
    string OperationId,
    string MissionId,
    string TaskId,
    OperationalRunStage Stage,
    bool Accepted,
    string Message,
    IReadOnlyList<OperationalRunStepResult> Steps,
    string? MissionExecutionId = null,
    string? TaskExecutionId = null,
    bool CleanupAttempted = false,
    string? CleanupMessage = null)
{
    public static OperationalRunResult Rejected(
        OperationalRunPreparation preparation,
        string message)
        => new(
            preparation.OperationId,
            preparation.MissionId,
            preparation.TaskId,
            OperationalRunStage.Rejected,
            false,
            message,
            []);
}
