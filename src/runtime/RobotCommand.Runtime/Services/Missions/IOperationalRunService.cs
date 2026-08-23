using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public interface IOperationalRunService
{
    event EventHandler? Changed;

    IReadOnlyList<OperationalRunPreparation> Preparations { get; }

    Task<IReadOnlyList<BehaviourPackageOption>> ListCompatibleBehavioursAsync(
        string connectionId,
        string vehicleId,
        bool refresh = false,
        CancellationToken cancellationToken = default);

    Task<OperationalRunPreparation> PrepareAsync(
        OperationalRunRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationalRunResult> LaunchAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task DiscardAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}
