using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IVehicleTrackHistory
{
    void Record(IEnumerable<VehicleTelemetryRecord> telemetry, DateTimeOffset now);

    IReadOnlyList<MapVehicleTrailVisual> BuildTrails(
        IReadOnlyList<VehicleRecord> vehicles,
        string? selectedVehicleId);

    IReadOnlyList<MapVehicleTrailVisual> BuildTrailsForSelectedVehicles(
        IReadOnlyList<VehicleRecord> vehicles,
        IReadOnlySet<string> selectedVehicleIds);

    void Clear(string? vehicleId = null);
}
