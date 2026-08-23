using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

public interface IBehaviourPackageStore
{
    event EventHandler? Changed;

    string RootPath { get; }

    IReadOnlyList<LocalBehaviourPackageRecord> Packages { get; }

    IReadOnlyList<BehaviourPackageLibraryIssue> Issues { get; }

    bool TryGet(BehaviourPackageIdentity identity, out LocalBehaviourPackageRecord? package);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<BehaviourPackageImportResult> ImportDirectoryAsync(
        string sourceDirectory,
        bool allowReplace = false,
        CancellationToken cancellationToken = default);

    Task ExportDirectoryAsync(
        BehaviourPackageIdentity identity,
        string destinationDirectory,
        bool allowReplace = false,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        BehaviourPackageIdentity identity,
        CancellationToken cancellationToken = default);

    Task SetRemoteBaselineAsync(
        BehaviourPackageIdentity identity,
        string? remoteSha256,
        CancellationToken cancellationToken = default);

    Task SetLogosValidationAsync(
        BehaviourPackageIdentity identity,
        BehaviourPackageValidationResult validation,
        CancellationToken cancellationToken = default);
}
