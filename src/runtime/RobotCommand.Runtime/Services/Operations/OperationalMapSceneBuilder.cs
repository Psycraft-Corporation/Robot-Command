using RobotCommand.Models;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;

namespace RobotCommand.Services.Operations;

public sealed class OperationalMapSceneBuilder(
    IUnitDefinitionService? reconciliation = null,
    IEntityStore<string, CameraSourceRecord>? cameraSources = null) : IOperationalMapSceneBuilder
{
    public MapVehicleMotionSnapshot BuildMotion(
        IReadOnlyList<VehicleRecord> vehicles,
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        MapFrameKind frame,
        DateTimeOffset capturedAt)
    {
        if (frame == MapFrameKind.Unknown)
            return MapVehicleMotionSnapshot.Empty;

        var displayVehicles = reconciliation?.ProjectVehicles(vehicles) ?? vehicles;
        var localConnectionId = frame is MapFrameKind.LocalEnu or MapFrameKind.LocalNed
            ? ChooseLocalScopeConnection(null, telemetry, [], frame)
            : null;
        var samples = new List<MapVehicleMotionSample>();
        foreach (var vehicle in displayVehicles)
        {
            var sample = SelectTelemetry(telemetry, vehicle.Id, localConnectionId);
            if (!TryGetVehicleMotion(sample, frame, out var x, out var y, out var vx, out var vy, out var vz))
                continue;

            samples.Add(new MapVehicleMotionSample(
                vehicle.Id,
                frame,
                x,
                y,
                sample?.HeadingDegrees,
                vx,
                vy,
                vz,
                sample?.State ?? vehicle.State,
                sample?.AirframeMode ?? string.Empty,
                sample?.LandedState ?? string.Empty,
                sample?.IsStale ?? true,
                sample?.ObservedAt ?? capturedAt,
                GimbalPitchDegrees: sample?.GimbalPitchDegrees,
                GimbalYawDegrees: sample?.GimbalYawDegrees,
                GimbalYawInEarthFrame: sample?.GimbalYawInEarthFrame));
        }

        return new MapVehicleMotionSnapshot(frame, samples, capturedAt);
    }

    public OperationalMapScene Build(
        IReadOnlyList<VehicleRecord> vehicles,
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        IReadOnlyList<GeometryOverlayRecord> geometries,
        string? selectedVehicleId,
        MapViewportMode viewportMode,
        bool geometryVisible,
        bool policyVisible = true,
        IReadOnlySet<string>? highlightedGeometryIds = null,
        IReadOnlySet<string>? selectedVehicleIds = null,
        bool missionPreviewVisible = true)
    {
        var displayVehicles = reconciliation?.ProjectVehicles(vehicles) ?? vehicles;
        var selectedTelemetry = SelectTelemetry(telemetry, selectedVehicleId);
        var frame = ChooseFrame(selectedTelemetry, telemetry, geometries);
        if (frame == MapFrameKind.Unknown)
        {
            return OperationalMapScene.Empty with
            {
                SelectedVehicleId = selectedVehicleId,
                ViewportMode = viewportMode,
                GeometryVisible = geometryVisible,
                PolicyVisible = policyVisible
            };
        }

        var localScopeConnectionId = frame is MapFrameKind.LocalEnu or MapFrameKind.LocalNed
            ? ChooseLocalScopeConnection(selectedTelemetry, telemetry, geometries, frame)
            : null;
        var allowedGeometryConnections = localScopeConnectionId is null
            ? null
            : new HashSet<string>([localScopeConnectionId], StringComparer.Ordinal);

        var vehicleVisuals = displayVehicles
            .Where(vehicle =>
                localScopeConnectionId is null ||
                vehicle.ConnectionIds.Contains(localScopeConnectionId, StringComparer.Ordinal))
            .Select(vehicle =>
            {
                var sample = SelectTelemetry(telemetry, vehicle.Id, localScopeConnectionId);
                if (!TryGetVehiclePoint(sample, frame, out var x, out var y))
                {
                    return null;
                }

                return new MapVehicleVisual(
                    vehicle.Id,
                    vehicle.Name,
                    x,
                    y,
                    sample?.HeadingDegrees,
                    sample?.State ?? vehicle.State,
                    selectedVehicleIds?.Contains(vehicle.Id) == true || vehicle.Id == selectedVehicleId,
                    vehicle.IsGhost)
                {
                    CameraCone = CameraConeFor(vehicle),
                    GimbalPitchDegrees = sample?.GimbalPitchDegrees,
                    GimbalYawDegrees = sample?.GimbalYawDegrees,
                    GimbalYawInEarthFrame = sample?.GimbalYawInEarthFrame
                };
            })
            .Where(item => item is not null)
            .Cast<MapVehicleVisual>()
            .OrderByDescending(item => item.Selected)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var geometryVisuals = geometries
            .Where(item => item.Frame == frame)
            .Where(item =>
                allowedGeometryConnections is null ||
                allowedGeometryConnections.Contains(item.ConnectionId))
            .Select(item => ToVisual(item, highlightedGeometryIds))
            .Where(item => item.IsPolicy
                ? policyVisible
                : IsMissionPreview(item)
                    ? missionPreviewVisible
                    : geometryVisible)
            .Where(item =>
                item.Points.Count > 0 ||
                item.Rings.Any(ring => ring.Count > 0))
            .OrderByDescending(item => item.Highlighted)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OperationalMapScene(
            frame,
            FrameLabel(frame),
            vehicleVisuals,
            geometryVisuals,
            selectedVehicleId,
            viewportMode,
            geometryVisible)
        {
            PolicyVisible = policyVisible
        };
    }

    private static bool IsMissionPreview(MapGeometryVisual geometry)
        => geometry.Kind.StartsWith("FlightMissionPreview", StringComparison.OrdinalIgnoreCase);

    private VehicleTelemetryRecord? SelectTelemetry(
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        string? vehicleId,
        string? requiredConnectionId = null)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            return null;
        }

        var sourceVehicleId = reconciliation?.ResolveTelemetrySource(vehicleId) ?? vehicleId;
        return telemetry
            .Where(item => item.VehicleId == sourceVehicleId)
            .Where(item =>
                requiredConnectionId is null ||
                item.ConnectionId == requiredConnectionId)
            .OrderByDescending(item => StateRank(item.State))
            .ThenByDescending(item => item.ObservedAt)
            .FirstOrDefault();
    }

    private static MapFrameKind ChooseFrame(
        VehicleTelemetryRecord? selected,
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        IReadOnlyList<GeometryOverlayRecord> geometries)
    {
        if (HasGlobal(selected))
        {
            return MapFrameKind.GlobalWgs84;
        }

        if (HasLocal(selected))
        {
            return MapFrameKind.LocalNed;
        }

        if (telemetry.Any(HasGlobal) || geometries.Any(item => item.Frame == MapFrameKind.GlobalWgs84))
        {
            return MapFrameKind.GlobalWgs84;
        }

        if (telemetry.Any(HasLocal))
        {
            return MapFrameKind.LocalNed;
        }

        if (geometries.Any(item => item.Frame == MapFrameKind.LocalEnu))
        {
            return MapFrameKind.LocalEnu;
        }

        if (geometries.Any(item => item.Frame == MapFrameKind.LocalNed))
        {
            return MapFrameKind.LocalNed;
        }

        return MapFrameKind.Unknown;
    }

    private static string? ChooseLocalScopeConnection(
        VehicleTelemetryRecord? selected,
        IReadOnlyList<VehicleTelemetryRecord> telemetry,
        IReadOnlyList<GeometryOverlayRecord> geometries,
        MapFrameKind frame)
    {
        if (selected is not null && HasLocal(selected))
        {
            return selected.ConnectionId;
        }

        var telemetryConnection = telemetry
            .Where(HasLocal)
            .OrderByDescending(item => StateRank(item.State))
            .ThenByDescending(item => item.ObservedAt)
            .Select(item => item.ConnectionId)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(telemetryConnection))
        {
            return telemetryConnection;
        }

        return geometries
            .Where(item => item.Frame == frame)
            .Select(item => item.ConnectionId)
            .FirstOrDefault();
    }

    private static bool TryGetVehiclePoint(
        VehicleTelemetryRecord? telemetry,
        MapFrameKind frame,
        out double x,
        out double y)
    {
        x = 0;
        y = 0;
        if (telemetry is null)
        {
            return false;
        }

        if (frame == MapFrameKind.GlobalWgs84 && HasGlobal(telemetry))
        {
            x = telemetry.LongitudeDegrees!.Value;
            y = telemetry.LatitudeDegrees!.Value;
            return true;
        }

        if (frame is MapFrameKind.LocalEnu or MapFrameKind.LocalNed && HasLocal(telemetry))
        {
            x = telemetry.LocalEastMetres!.Value;
            y = telemetry.LocalNorthMetres!.Value;
            return true;
        }

        return false;
    }

    private static bool TryGetVehicleMotion(
        VehicleTelemetryRecord? telemetry,
        MapFrameKind frame,
        out double x,
        out double y,
        out double? velocityX,
        out double? velocityY,
        out double? velocityZ)
    {
        x = 0;
        y = 0;
        velocityX = null;
        velocityY = null;
        velocityZ = telemetry?.VelocityDownMetresPerSecond is { } down ? -down : null;
        if (telemetry is null)
            return false;

        if (frame == MapFrameKind.GlobalWgs84 && HasGlobal(telemetry))
        {
            x = telemetry.LongitudeDegrees!.Value;
            y = telemetry.LatitudeDegrees!.Value;
            var latitudeRadians = y * Math.PI / 180d;
            var metresPerDegreeLatitude = 6378137d * Math.PI / 180d;
            var metresPerDegreeLongitude = Math.Max(1d, metresPerDegreeLatitude * Math.Cos(latitudeRadians));
            velocityX = telemetry.VelocityEastMetresPerSecond / metresPerDegreeLongitude;
            velocityY = telemetry.VelocityNorthMetresPerSecond / metresPerDegreeLatitude;
            return true;
        }

        if (frame is MapFrameKind.LocalEnu or MapFrameKind.LocalNed && HasLocal(telemetry))
        {
            x = telemetry.LocalEastMetres!.Value;
            y = telemetry.LocalNorthMetres!.Value;
            velocityX = telemetry.VelocityEastMetresPerSecond;
            velocityY = telemetry.VelocityNorthMetresPerSecond;
            return true;
        }

        return false;
    }

    private static MapGeometryVisual ToVisual(
        GeometryOverlayRecord item,
        IReadOnlySet<string>? highlightedGeometryIds)
        => new(
            item.GeometryId,
            item.Name,
            item.Kind,
            item.Closed,
            item.Points.Select(point => Normalize(point, item.Frame)).ToArray(),
            item.Rings
                .Select(ring => (IReadOnlyList<OperationalPoint>)ring
                    .Select(point => Normalize(point, item.Frame))
                    .ToArray())
                .ToArray(),
            item.PolicyConstraint)
        {
            ConnectionId = item.ConnectionId,
            PolicyKind = item.PolicyKind,
            Highlighted = highlightedGeometryIds?.Contains(item.GeometryId) == true ||
                          highlightedGeometryIds?.Contains(item.Id) == true
        };

    private static OperationalPoint Normalize(OperationalPoint point, MapFrameKind frame)
        => frame switch
        {
            MapFrameKind.LocalNed => new OperationalPoint(point.Y, point.X, -point.Z),
            _ => point
        };

    private static bool HasGlobal(VehicleTelemetryRecord? item)
        => item?.LatitudeDegrees is not null && item.LongitudeDegrees is not null;

    private static bool HasLocal(VehicleTelemetryRecord? item)
        => item?.LocalNorthMetres is not null && item.LocalEastMetres is not null;

    private MapCameraConeVisual? CameraConeFor(VehicleRecord vehicle)
    {
        if (vehicle.IsGhost ||
            (!vehicle.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase) &&
             !vehicle.ProfileKey.Contains("ardupilot", StringComparison.OrdinalIgnoreCase)) ||
            !vehicle.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var capabilityReported = vehicle.CapabilityKeys?.Any(IsCameraCapability) == true;
        var sourceReported = cameraSources?.Items.Any(source =>
            vehicle.ConnectionIds.Contains(source.ConnectionId, StringComparer.Ordinal) &&
            source.State is (AvailabilityState.Online or AvailabilityState.Degraded) &&
            (source.Active || source.Fresh || source.HasImage)) == true;
        return capabilityReported || sourceReported ? new MapCameraConeVisual() : null;
    }

    private static bool IsCameraCapability(string key)
        => key.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("gimbal", StringComparison.OrdinalIgnoreCase);

    private static string FrameLabel(MapFrameKind frame)
        => frame switch
        {
            MapFrameKind.GlobalWgs84 => "Global WGS84 · native offline map",
            MapFrameKind.LocalEnu => "Local ENU",
            MapFrameKind.LocalNed => "Local NED (displayed East/North)",
            _ => "Unknown frame"
        };

    private static int StateRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 4,
            AvailabilityState.Degraded => 3,
            AvailabilityState.Stale => 2,
            AvailabilityState.Offline => 1,
            _ => 0
        };
}
