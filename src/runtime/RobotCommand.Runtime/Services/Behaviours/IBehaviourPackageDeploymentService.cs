using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public interface IBehaviourPackageDeploymentService
{
    event EventHandler? Changed;

    BehaviourPackageCommandResult? LastResult { get; }

    Task<BehaviourPackageOperationAssessment> AssessAsync(
        BehaviourPackageOperationKind operation,
        string connectionId,
        BehaviourPackageIdentity identity,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageCommandResult> ValidateAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageCommandResult> InstallAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageCommandResult> UpdateAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageCommandResult> RemoveAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default);
}
