namespace RobotCommand.Models;

public enum MapNavigationRequestKind
{
    CenterOn,
    FitVehicles
}

public sealed record MapNavigationRequest(
    MapNavigationRequestKind Kind,
    MapViewportSnapshot? Viewport = null,
    IReadOnlyList<string>? VehicleIds = null);

public sealed record MapFollowState(
    IReadOnlyList<string> VehicleIds,
    long Revision)
{
    public bool IsGroup => VehicleIds.Count > 1;

    public string Label => IsGroup
        ? $"{VehicleIds.Count} units"
        : "unit";
}
