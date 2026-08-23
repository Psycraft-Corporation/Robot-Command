using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public interface IGeometryGateway
{
    bool IsAvailable { get; }

    string AvailabilityMessage { get; }

    Task<IReadOnlyList<RemoteGeometryRecord>> ListAsync(
        string connectionId,
        GeometryQuery query,
        CancellationToken cancellationToken = default);

    Task<RemoteGeometryObject?> GetAsync(
        string connectionId,
        string geometryId,
        bool refresh = false,
        CancellationToken cancellationToken = default);

    Task<GeometryValidationResult> ValidateAsync(
        string connectionId,
        GeometryDocument document,
        bool checkUpdateCompatibility = false,
        CancellationToken cancellationToken = default);

    Task<GeometryCommandResult> CreateAsync(
        GeometryCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<GeometryCommandResult> UpdateAsync(
        GeometryUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<GeometryCommandResult> DeleteAsync(
        GeometryDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<GeometryRegistrySnapshot> GetRegistryStatusAsync(
        string connectionId,
        bool includeDetails = true,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<GeometryRegistryEvent> WatchAsync(
        string connectionId,
        GeometryQuery query,
        CancellationToken cancellationToken = default);
}
