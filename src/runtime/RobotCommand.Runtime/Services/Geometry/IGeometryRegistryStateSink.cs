using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

/// <summary>
/// Internal state boundary used by the live registry supervisor. Implementations
/// must never overwrite local geometry documents while applying remote events.
/// </summary>
public interface IGeometryRegistryStateSink
{
    void ReplaceRemoteSnapshot(
        string connectionId,
        IReadOnlyList<RemoteGeometryRecord> records,
        GeometryRegistrySnapshot registry);

    void ApplyRegistryEvent(GeometryRegistryEvent registryEvent);

    void SetRegistryWatchState(
        GeometryRegistryWatchState state,
        bool notify = true);

    void ClearRemoteConnection(
        string connectionId,
        GeometryRegistryWatchState finalState);
}
