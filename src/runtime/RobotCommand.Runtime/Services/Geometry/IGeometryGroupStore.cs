using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public interface IGeometryGroupStore
{
    event EventHandler? Changed;

    IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; }

    IReadOnlyList<string> GetGroups(string geometryId);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(string name, CancellationToken cancellationToken = default);
    Task RenameAsync(string name, string replacement, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
    Task AssignAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default);
    Task RemoveAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default);
    Task ExportSetAsync(string path, string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default);
    Task ImportSetAsync(string path, bool replace, CancellationToken cancellationToken = default);
}
