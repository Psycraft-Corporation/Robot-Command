using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public interface IGeometryDocumentStore
{
    event EventHandler? Changed;

    string RootPath { get; }

    string DocumentsPath { get; }

    string StagingPath { get; }

    IReadOnlyList<GeometryDocument> Documents { get; }

    IReadOnlyList<GeometryLibraryIssue> Issues { get; }

    bool TryGet(string geometryId, out GeometryDocument? document);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<GeometryDocument> UpsertAsync(
        GeometryDocument document,
        CancellationToken cancellationToken = default);

    Task<GeometryDocument> ImportAsync(
        string path,
        bool allowReplace = false,
        CancellationToken cancellationToken = default);

    Task ExportAsync(
        string geometryId,
        string path,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string geometryId,
        CancellationToken cancellationToken = default);
}
