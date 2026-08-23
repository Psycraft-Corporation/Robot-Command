namespace RobotCommand.Models;

public sealed record MapVehicleSelectionRequest(
    string VehicleId,
    bool Extend,
    bool Range);

public sealed record MapVehicleBoxSelectionRequest(
    IReadOnlyList<string> VehicleIds,
    bool Extend,
    bool Range);

/// <summary>Map-originated selection request for persisted local geometry.</summary>
public sealed record MapGeometrySelectionRequest(
    string GeometryId,
    bool Extend,
    bool Range);
