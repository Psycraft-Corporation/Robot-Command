using RobotCommand.Models;

namespace RobotCommand.ViewModels;

public sealed record GeometryVertexRowViewModel(
    int Index,
    GeometryDocumentPoint Point)
{
    public int Number => Index + 1;

    public string LongitudeText => Point.LongitudeDegrees.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    public string LatitudeText => Point.LatitudeDegrees.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    public string AltitudeText => Point.AltitudeMetres.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public string Summary => $"{Number}. {LatitudeText}, {LongitudeText} · {AltitudeText} m";
}
