using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public interface IBehaviourGeometryBindingService
{
    bool IsAvailable { get; }

    string AvailabilityMessage { get; }

    Task<IReadOnlyList<BehaviourGeometryBindingRecord>> ListAsync(
        string connectionId,
        string behaviourId,
        string version,
        bool checkObjectExists = true,
        CancellationToken cancellationToken = default);

    Task<BehaviourGeometryReadiness> AssessAsync(
        string connectionId,
        BehaviourPackageOption package,
        bool refreshGeometry = false,
        CancellationToken cancellationToken = default);

    Task<BehaviourGeometryBindingCommandResult> SetAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<BehaviourGeometryBindingCommandResult> DeleteAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default);
}
