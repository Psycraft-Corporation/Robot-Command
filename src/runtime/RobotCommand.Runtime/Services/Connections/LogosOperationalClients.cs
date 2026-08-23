using Logos.Api.V1;

namespace RobotCommand.Services.Connections;

/// <summary>
/// One generated-client set for one connected Logos endpoint.
/// Application gateways should reuse these clients rather than creating their
/// own channels or bypassing Logos through vehicle-specific protocols.
/// </summary>
public sealed record LogosOperationalClients(
    SystemService.SystemServiceClient System,
    MissionService.MissionServiceClient Missions,
    TaskService.TaskServiceClient Tasks,
    AutonomyService.AutonomyServiceClient Autonomy,
    GeometryService.GeometryServiceClient Geometry,
    PolicyService.PolicyServiceClient Policy,
    SensorsService.SensorsServiceClient Sensors)
{
    /// <summary>
    /// Policy-governed vehicle intervention client introduced by
    /// vehicle_operations.proto. Kept as an init property so existing tests and
    /// adapters that construct the original client set remain source-compatible.
    /// </summary>
    public VehicleOperationsService.VehicleOperationsServiceClient? VehicleOperations { get; init; }
}
