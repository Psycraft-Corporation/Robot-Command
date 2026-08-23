namespace RobotCommand.Models;

public sealed class MissionPackageDocument
{
    public string SchemaVersion { get; set; } = "logos.mission.v1";
    public string MissionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Objective { get; set; } = "";
    public string Priority { get; set; } = "Normal";
    public string PolicyId { get; set; } = "";
    public string? TeamId { get; set; }
    public string? VehicleId { get; set; }
    public string? ConnectionId { get; set; }
    public List<string> GeometryIds { get; set; } = [];
    public List<string> RequiredCapabilities { get; set; } = [];
    public string PayloadJson { get; set; } = "";
    public List<TaskPackageDocument> Tasks { get; set; } = [];
}

public sealed class TaskPackageDocument
{
    public string SchemaVersion { get; set; } = "logos.task.v1";
    public string TaskId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MissionId { get; set; }
    public string? TeamId { get; set; }
    public string? ConnectionId { get; set; }
    public string? AssignedVehicleId { get; set; }
    public string? AssignedMemberId { get; set; }
    public string? AssignedLogosInstanceId { get; set; }
    public string Objective { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string Priority { get; set; } = "Normal";
    public string BehaviourId { get; set; } = "";
    public string BehaviourVersion { get; set; } = "";
    public string PackageId { get; set; } = "";
    public List<string> GeometryIds { get; set; } = [];
    public string ParametersJson { get; set; } = "";
}

public sealed record DocumentValidationResult(
    PlanValidationState State,
    string Summary,
    IReadOnlyList<string> Issues)
{
    public bool IsValid => State is PlanValidationState.Valid or PlanValidationState.Warning;
}
