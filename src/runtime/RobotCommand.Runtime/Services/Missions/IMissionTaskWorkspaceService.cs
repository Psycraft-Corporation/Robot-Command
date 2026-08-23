using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public interface IMissionTaskWorkspaceService
{
    bool GatewayAvailable { get; }

    string GatewayStatus { get; }

    Task<MissionRecord> ImportMissionAsync(string path, CancellationToken cancellationToken = default);

    Task<OperationalTaskRecord> ImportTaskAsync(string path, CancellationToken cancellationToken = default);

    Task ExportMissionAsync(string missionId, string path, CancellationToken cancellationToken = default);

    Task ExportTaskAsync(string taskId, string path, CancellationToken cancellationToken = default);

    Task<DocumentValidationResult> ValidateMissionAsync(string missionId, CancellationToken cancellationToken = default);

    Task<DocumentValidationResult> ValidateTaskAsync(string taskId, CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> PublishMissionAsync(
        string missionId,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> PublishMissionAsync(
        string missionId,
        WorkspaceCommandIdentity identity,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> PublishTaskAsync(
        string taskId,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> PublishTaskAsync(
        string taskId,
        WorkspaceCommandIdentity identity,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default);

    Task PlanTaskAssignmentAsync(
        string taskId,
        string vehicleId,
        CancellationToken cancellationToken = default);

    Task<DocumentValidationResult> ValidateTaskForVehicleAsync(
        string taskId,
        string vehicleId,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> AssignTaskAsync(
        string taskId,
        string vehicleId,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> AssignTaskAsync(
        string taskId,
        string vehicleId,
        WorkspaceCommandIdentity identity,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default);

    Task RefreshRemoteAsync(string connectionId, CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> ExecuteMissionCommandAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> ExecuteTaskCommandAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken = default);
}
