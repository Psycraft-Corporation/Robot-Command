using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public sealed class UnavailableMissionTaskGateway : IMissionTaskGateway
{
    private const string Message =
        "No connected Logos operational session currently exposes both MissionService and TaskService. " +
        "Local plans remain usable until a compatible Logos runtime is connected.";

    public bool IsAvailable => false;

    public string AvailabilityMessage => Message;

    public Task<IReadOnlyList<MissionRecord>> ListMissionsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MissionRecord>>([]);

    public Task<IReadOnlyList<OperationalTaskRecord>> ListTasksAsync(
        string connectionId,
        string? missionId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OperationalTaskRecord>>([]);

    public Task<DocumentValidationResult> ValidateMissionAsync(
        string connectionId,
        MissionRecord mission,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableValidation());

    public Task<DocumentValidationResult> ValidateTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableValidation());

    public Task<GatewayCommandResult> CreateMissionAsync(
        string connectionId,
        MissionRecord mission,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableCommand());

    public Task<GatewayCommandResult> CreateTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        bool validateOnCreate = true,
        bool allowReplaceDraft = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableCommand());

    public Task<GatewayCommandResult> AssignTaskAsync(
        string connectionId,
        OperationalTaskRecord task,
        bool validateOnAssign = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableCommand());

    public Task<GatewayCommandResult> ExecuteMissionCommandAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableCommand());

    public Task<GatewayCommandResult> ExecuteTaskCommandAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UnavailableCommand());

    private static DocumentValidationResult UnavailableValidation()
        => new(
            PlanValidationState.Unavailable,
            Message,
            [Message]);

    private static GatewayCommandResult UnavailableCommand()
        => new(
            false,
            OperationalCommandState.Rejected,
            Message);
}
