using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Thin Logos Autonomy API boundary. It transfers the package's raw manifest,
/// tree XML, and geometry JSON without interpreting BehaviorTree.CPP nodes or
/// ports. Higher-level assessment and post-command verification live in the
/// deployment service.
/// </summary>
public interface IBehaviourPackageGateway
{
    Task<BehaviourPackageValidationResult> ValidateAsync(
        BehaviourPackageGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageGatewayMutationResult> CreateAsync(
        BehaviourPackageGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageGatewayMutationResult> UpdateAsync(
        BehaviourPackageGatewayRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageGatewayMutationResult> DeleteAsync(
        BehaviourPackageDeleteGatewayRequest request,
        CancellationToken cancellationToken = default);
}
