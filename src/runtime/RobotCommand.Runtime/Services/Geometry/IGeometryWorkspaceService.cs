using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public interface IGeometryWorkspaceService
{
    event EventHandler? Changed;

    bool GatewayAvailable { get; }

    string GatewayStatus { get; }

    string LocalLibraryPath { get; }

    IReadOnlyList<GeometryDocument> LocalDocuments { get; }

    IReadOnlyList<GeometryLibraryIssue> LibraryIssues { get; }

    IReadOnlyList<RemoteGeometryRecord> RemoteRecords { get; }

    IReadOnlyList<GeometryRegistrySnapshot> RegistrySnapshots { get; }

    IReadOnlyList<GeometryRegistryWatchState> RegistryWatchStates { get; }

    IReadOnlyList<GeometryDeploymentRecord> Deployments { get; }

    Task RefreshAsync(
        string? connectionId = null,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> CreateLocalDraftAsync(
        GeometryDocumentKind kind,
        string? geometryId = null,
        string? displayName = null,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> ImportLocalAsync(
        string path,
        bool allowReplace = false,
        CancellationToken cancellationToken = default);

    Task ExportLocalAsync(
        string geometryId,
        string path,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> SaveLocalAsync(
        GeometryDocument document,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> DuplicateLocalAsync(
        string geometryId,
        string newGeometryId,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> PullAsync(
        string connectionId,
        string geometryId,
        bool replaceLocal = false,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> PullAsLocalCopyAsync(
        string connectionId,
        string geometryId,
        string newGeometryId,
        CancellationToken cancellationToken = default);

    Task<GeometryOperationAssessment> AssessRemoteOperationAsync(
        string connectionId,
        string geometryId,
        GeometryRemoteOperationKind operation,
        CancellationToken cancellationToken = default);

    Task<GeometryCommandResult> CreateRemoteAsync(
        string connectionId,
        string geometryId,
        CancellationToken cancellationToken = default);

    Task<GeometryCommandResult> UpdateRemoteAsync(
        string connectionId,
        string geometryId,
        CancellationToken cancellationToken = default);

    Task<GeometryCommandResult> DeleteRemoteAsync(
        string connectionId,
        string geometryId,
        CancellationToken cancellationToken = default);

    Task RemoveLocalAsync(
        string geometryId,
        CancellationToken cancellationToken = default);
}
