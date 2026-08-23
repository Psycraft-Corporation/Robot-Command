namespace RobotCommand.Models;

public sealed class ConnectionMetric
{
    public ConnectionMetric(string title, string value, string detail = "", string valueColor = "#FFFFFF")
    {
        Title = title;
        Value = value;
        Detail = detail;
        ValueColor = valueColor;
    }

    public string Title { get; }
    public string Value { get; }
    public string Detail { get; }
    public string ValueColor { get; }
}
