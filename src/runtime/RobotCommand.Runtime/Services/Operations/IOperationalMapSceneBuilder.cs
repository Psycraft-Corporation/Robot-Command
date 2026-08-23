using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperationalMapSceneBuilder
{
    OperationalMapScene Build(
        IReadOnlyList<VehicleRecord> vehicles,
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        IReadOnlyList<GeometryOverlayRecord> geometries,
        string? selectedVehicleId,
        MapViewportMode viewportMode,
        bool geometryVisible,
        bool policyVisible = true,
        IReadOnlySet<string>? highlightedGeometryIds = null,
        IReadOnlySet<string>? selectedVehicleIds = null);

    MapVehicleMotionSnapshot BuildMotion(
        IReadOnlyList<VehicleRecord> vehicles,
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        MapFrameKind frame,
        DateTimeOffset capturedAt);
}
