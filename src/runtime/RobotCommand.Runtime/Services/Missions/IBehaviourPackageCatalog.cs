using RobotCommand.Models;

namespace RobotCommand.Services.Missions;

public interface IBehaviourPackageCatalog
{
    Task<IReadOnlyList<BehaviourPackageOption>> ListAsync(
        string connectionId,
        bool refresh = false,
        CancellationToken cancellationToken = default);

    Task<BehaviourPackageOption> GetRequiredAsync(
        string connectionId,
        string behaviourId,
        string? version = null,
        bool refresh = false,
        CancellationToken cancellationToken = default);
}
