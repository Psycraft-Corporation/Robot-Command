using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Read-only installed behaviour inventory. Remote create, update, validation,
/// and removal commands are not exposed by this interface.
/// </summary>
public interface IBehaviourRemotePackageSource
{
    Task<IReadOnlyList<RemoteBehaviourPackageRecord>> ListAsync(
        string connectionId,
        bool refresh = false,
        CancellationToken cancellationToken = default);
}
