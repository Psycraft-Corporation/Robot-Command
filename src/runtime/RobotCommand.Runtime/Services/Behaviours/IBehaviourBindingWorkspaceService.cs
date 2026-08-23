using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public interface IBehaviourBindingWorkspaceService
{
    event EventHandler? Changed;

    bool IsAvailable { get; }

    string AvailabilityMessage { get; }

    BehaviourBindingWorkspaceSnapshot? GetSnapshot(
        string connectionId,
        BehaviourPackageIdentity identity);

    Task<IReadOnlyList<BehaviourBindingPackageOption>> ListPackagesAsync(
        string connectionId,
        bool refreshPackages = false,
        CancellationToken cancellationToken = default);

    Task<BehaviourBindingWorkspaceSnapshot> InspectAsync(
        string connectionId,
        BehaviourPackageIdentity identity,
        bool refreshPackages = false,
        bool refreshGeometry = false,
        CancellationToken cancellationToken = default);

    Task<BehaviourBindingMutationResult> SetAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourBindingMutationResult> ClearAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default);
}
