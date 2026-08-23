using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public interface IBehaviourWorkspaceService
{
    event EventHandler? Changed;

    IReadOnlyList<LocalBehaviourPackageRecord> LocalPackages { get; }

    IReadOnlyList<BehaviourPackageLibraryIssue> LocalIssues { get; }

    BehaviourWorkspaceSnapshot GetSnapshot(string connectionId);

    Task<BehaviourWorkspaceSnapshot> RefreshAsync(
        string connectionId,
        BehaviourCompatibilityTarget? compatibilityTarget = null,
        bool refreshLocal = false,
        bool refreshRemote = false,
        CancellationToken cancellationToken = default);
}
