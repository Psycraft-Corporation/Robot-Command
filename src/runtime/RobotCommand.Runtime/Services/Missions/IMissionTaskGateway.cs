using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public interface IMissionTaskGateway
{
    bool IsAvailable { get; }

    string AvailabilityMessage { get; }

    Task<IReadOnlyList<MissionRecord>> ListMissionsAsync(
        string connectionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationalTaskRecord>> ListTasksAsync(
        string connectionId,
        string? missionId = null,
        CancellationToken cancellationToken = default);

    Task<DocumentValidationResult> ValidateMissionAsync(
        string connectionId,
        MissionRecord mission,
        CancellationToken cancellationToken = default);

    Task<DocumentValidationResult> ValidateTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> CreateMissionAsync(
        string connectionId,
        MissionRecord mission,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> CreateMissionAsync(
        MissionPublicationRequest request,
        CancellationToken cancellationToken = default)
        => CreateMissionAsync(
            request.ConnectionId,
            request.Mission,
            request.ValidateOnCreate,
            request.AllowReplaceDraft,
            cancellationToken);

    Task<GatewayCommandResult> CreateTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> CreateTaskAsync(
        TaskPublicationRequest request,
        CancellationToken cancellationToken = default)
        => CreateTaskAsync(
            request.ConnectionId,
            request.Task,
            request.ValidateOnCreate,
            request.AllowReplaceDraft,
            cancellationToken);

    Task<GatewayCommandResult> AssignTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> AssignTaskAsync(
        TaskAssignmentRequest request,
        CancellationToken cancellationToken = default)
        => AssignTaskAsync(
            request.ConnectionId,
            request.Task,
            request.ValidateOnAssign,
            cancellationToken);

    Task<PreparedOperationGatewayResult> PrepareMissionOperationAsync(
        MissionOperationPreparationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PreparedOperationGatewayResult.Rejected(
            "The mission gateway does not expose prepared lifecycle interventions."));

    Task<PreparedOperationGatewayResult> PrepareTaskOperationAsync(
        TaskOperationPreparationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PreparedOperationGatewayResult.Rejected(
            "The task gateway does not expose prepared lifecycle interventions."));

    Task<GatewayCommandResult> ExecuteMissionCommandAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<GatewayCommandResult> ExecuteTaskCommandAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken = default);
}
