using RobotCommand.Models;

namespace RobotCommand.ViewModels;

public sealed record GeometryLibraryItemViewModel(
    GeometryDocument Document,
    GeometryValidationResult Validation,
    GeometryDeploymentRecord? Deployment,
    int LibraryIssueCount)
{
    public string GeometryId => Document.GeometryId;

    public string DisplayName => Document.DisplayName;

    public string Description => Document.Description;

    public string Kind => Document.Kind.ToString();

    public string Frame => Document.Frame.ToString();

    public string ValidationState => Validation.State.ToString();

    public string DeploymentState => Deployment?.Status.ToString() ?? "Not compared";

    public string ShapeSummary => Document.Kind switch
    {
        GeometryDocumentKind.PointOfInterest => $"{Document.Points.Count} point",
        GeometryDocumentKind.WaypointSequence => $"{Document.Points.Count} waypoint(s)",
        GeometryDocumentKind.Zone => $"{Document.Rings.Count} ring(s)",
        _ => "Unknown shape"
    };

    public string StatusSummary =>
        $"{Kind} · {ValidationState} · {DeploymentState}";
}

public sealed record GeometryRemoteItemViewModel(
    RemoteGeometryRecord Record,
    GeometryDeploymentRecord? Deployment)
{
    public string GeometryId => Record.GeometryId;

    public string DisplayName => Record.DisplayName;

    public string Kind => Record.Kind.ToString();

    public string Frame => Record.Frame.ToString();

    public string DeploymentState => Deployment?.Status.ToString() ?? "Remote only";

    public string ShapeSummary => Record.Kind switch
    {
        GeometryDocumentKind.PointOfInterest => $"{Record.PointCount} point",
        GeometryDocumentKind.WaypointSequence => $"{Record.PointCount} waypoint(s)",
        GeometryDocumentKind.Zone => $"{Record.RingCount} ring(s)",
        _ => "Unknown shape"
    };

    public string StatusSummary => $"{Kind} · {Frame} · {DeploymentState}";
}
