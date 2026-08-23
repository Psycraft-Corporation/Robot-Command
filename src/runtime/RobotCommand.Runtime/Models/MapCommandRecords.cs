namespace RobotCommand.Models;

/// <summary>A global map location selected for an operator command.</summary>
public sealed record MapCommandTarget(double LatitudeDegrees, double LongitudeDegrees);

public sealed record MapAssemblyRequest(
    string FormationId,
    MapCommandTarget Start,
    MapCommandTarget End);

/// <summary>Transient map geometry describing a formation preview.</summary>
public sealed record FormationPreviewPath(
    IReadOnlyList<MapCommandTarget> Points,
    bool Closed = false);

public sealed record MapGoToTargetVisual(
    string VehicleId,
    double LatitudeDegrees,
    double LongitudeDegrees)
{
    public bool Selected { get; init; }
    public bool PreviewOnly { get; init; }
}

/// <summary>Stable virtual position for a locked local Team formation.</summary>
public sealed record MapTeamPositionVisual(
    string TeamId,
    string TeamName,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double? TargetLatitudeDegrees,
    double? TargetLongitudeDegrees,
    bool Moving,
    IReadOnlyList<MapTeamMemberTargetVisual>? MemberTargets = null,
    bool Transforming = false);

public sealed record MapTeamMemberTargetVisual(
    string UnitId,
    double LatitudeDegrees,
    double LongitudeDegrees);
