using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface ISavedMapViewRepository
{
    event EventHandler? Changed;

    IReadOnlyList<SavedMapView> Views { get; }

    bool TryGet(string id, out SavedMapView? view);

    Task UpsertAsync(SavedMapView view, CancellationToken cancellationToken = default);

    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    Task ImportAsync(
        IEnumerable<SavedMapView> views,
        CancellationToken cancellationToken = default);
}
