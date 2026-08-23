using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class UnavailableGeometryGateway : IGeometryGateway
{
    private readonly string _message;

    public UnavailableGeometryGateway(
        string message = "The packaged Logos SDK does not expose GeometryService operations.")
    {
        _message = message;
    }

    public bool IsAvailable => false;

    public string AvailabilityMessage => _message;

    public Task<IReadOnlyList<RemoteGeometryRecord>> ListAsync(
        string connectionId,
        GeometryQuery query,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteGeometryRecord>>([]);

    public Task<RemoteGeometryObject?> GetAsync(
        string connectionId,
        string geometryId,
        bool refresh = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult<RemoteGeometryObject?>(null);

    public Task<GeometryValidationResult> ValidateAsync(
        string connectionId,
        GeometryDocument document,
        bool checkUpdateCompatibility = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryValidationResult.Unavailable(_message));

    public Task<GeometryCommandResult> CreateAsync(
        GeometryCreateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Rejected());

    public Task<GeometryCommandResult> UpdateAsync(
        GeometryUpdateRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Rejected());

    public Task<GeometryCommandResult> DeleteAsync(
        GeometryDeleteRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Rejected());

    public Task<GeometryRegistrySnapshot> GetRegistryStatusAsync(
        string connectionId,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryRegistrySnapshot.Unknown(connectionId, _message));

    public async IAsyncEnumerable<GeometryRegistryEvent> WatchAsync(
        string connectionId,
        GeometryQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private GeometryCommandResult Rejected()
        => new(false, GeometryCommandState.Rejected, _message);
}
