using System.Globalization;
using Avalonia.Data.Converters;
using RobotCommand.Core;
using RobotCommand.Localization;

namespace RobotCommand.ViewModels;

public sealed class MissionStepDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FlightMissionStep step)
        {
            return string.Empty;
        }

        return string.Equals(parameter as string, "context", StringComparison.Ordinal)
            ? LocalizeContext(step, culture)
            : LocalizeKind(step.Kind);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string LocalizeKind(FlightMissionStepKind kind)
    {
        var localization = LocalizationService.Current;
        return kind switch
        {
            FlightMissionStepKind.Takeoff => localization.Get("FlightMissionTakeoff"),
            FlightMissionStepKind.ReturnToLaunch => localization.Get("FlightMissionRtl"),
            FlightMissionStepKind.Land => localization.Get("FlightMissionLand"),
            FlightMissionStepKind.PointOfInterest => localization.Get("FlightMissionPointOfInterest"),
            FlightMissionStepKind.WaypointSequence => localization.Get("FlightMissionWaypointSequence"),
            FlightMissionStepKind.SurveyZone => localization.Get("FlightMissionSurvey"),
            FlightMissionStepKind.CorridorScan => localization.Get("FlightMissionCorridor"),
            FlightMissionStepKind.TimedLoiter => localization.Get("FlightMissionLoiter"),
            FlightMissionStepKind.CameraCaptureIntent => localization.Get("FlightMissionCameraIntent"),
            _ => kind.ToString()
        };
    }

    private static string LocalizeContext(FlightMissionStep step, CultureInfo culture)
    {
        var localization = LocalizationService.Current;
        if (step.NeedsGeometryBinding)
        {
            return step.Kind == FlightMissionStepKind.TimedLoiter && step.LoiterDurationSeconds is { } duration
                ? Format(localization.Get("FlightMissionLoiterGeometryRequired"), duration, culture)
                : localization.Get("FlightMissionGeometryRequired");
        }

        return step.Kind switch
        {
            FlightMissionStepKind.TimedLoiter when step.LoiterDurationSeconds is { } duration
                => Format(localization.Get("FlightMissionLoiterSummary"), duration, culture),
            FlightMissionStepKind.PointOfInterest => localization.Get("FlightMissionPointCount"),
            FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.CorridorScan
                => Format(localization.Get("FlightMissionRoutePointCount"), step.FrozenCoordinates.Count, culture),
            FlightMissionStepKind.SurveyZone
                => Format(localization.Get("FlightMissionZonePointCount"), step.FrozenCoordinates.Count, culture),
            _ => string.Empty
        };
    }

    private static string Format(string format, object value, CultureInfo culture)
        => string.Format(culture, format, value);
}
