using System.Globalization;
using Avalonia.Data.Converters;
using RobotCommand.Core;
using RobotCommand.Localization;

namespace RobotCommand.ViewModels;

public sealed class FlightMissionCameraActionDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            FlightMissionCameraActionKind kind => LocalizationService.Current.Get($"FlightMissionCameraAction{kind}"),
            FlightMissionCameraMode mode => LocalizationService.Current.Get($"FlightMissionCameraMode{mode}"),
            FlightMissionGimbalFrame frame => LocalizationService.Current.Get($"FlightMissionGimbalFrame{frame}"),
            _ => value?.ToString() ?? string.Empty
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FlightMissionCameraActionSummaryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FlightMissionCameraAction action) return string.Empty;
        var localization = LocalizationService.Current;
        var label = localization.Get($"FlightMissionCameraAction{action.Kind}");
        var detail = action.Kind switch
        {
            FlightMissionCameraActionKind.PhotoByTime when action.IntervalSeconds is { } interval => $"{interval:0.#} s",
            FlightMissionCameraActionKind.PhotoByDistance when action.DistanceMetres is { } distance => $"{distance:0.#} m",
            FlightMissionCameraActionKind.CameraMode when action.CameraMode is { } mode => localization.Get($"FlightMissionCameraMode{mode}"),
            FlightMissionCameraActionKind.RegionOfInterest when action.RegionOfInterest is { } roi => $"{roi.LatitudeDegrees:0.#####}, {roi.LongitudeDegrees:0.#####}",
            FlightMissionCameraActionKind.Gimbal => string.Join(", ", new[]
            {
                action.GimbalPitchDegrees is { } pitch ? $"P {pitch:0.#}°" : null,
                action.GimbalYawDegrees is { } yaw ? $"Y {yaw:0.#}°" : null,
                action.GimbalRollDegrees is { } roll ? $"R {roll:0.#}°" : null
            }.Where(item => item is not null)),
            _ => string.Empty
        };
        var camera = string.IsNullOrWhiteSpace(action.CameraName) ? string.Empty : $" · {action.CameraName}";
        return string.IsNullOrWhiteSpace(detail) ? $"{label}{camera}" : $"{label} · {detail}{camera}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
