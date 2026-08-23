using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Read-only installed behaviour inventory. Remote create, update, validation,
/// and removal commands intentionally remain outside this code drop.
/// </summary>
public interface IBehaviourRemotePackageSource
{
    Task<IReadOnlyList<RemoteBehaviourPackageRecord>> ListAsync(
        string connectionId,
        bool refresh = false,
        CancellationToken cancellationToken = default);
}
