using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Logos.Api.V1;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

internal static class LogosLiveRecordMapper
{
    public static VehicleTelemetryRecord? ToTelemetryRecord(
        string connectionId,
        string? fallbackLogosInstanceId,
        string? fallbackVehicleId,
        VehicleTelemetry? telemetry,
        VehicleOdometry? odometryOverride = null)
    {
        if (telemetry is null && odometryOverride is null)
        {
            return null;
        }

        var odometry = odometryOverride ?? telemetry?.Odometry;
        var vehicleId = FirstNonEmpty(telemetry?.VehicleId, odometry?.VehicleId, fallbackVehicleId);
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            return null;
        }

        var logosInstanceId = FirstNonEmpty(
            telemetry?.LogosInstanceId,
            odometry?.LogosInstanceId,
            fallbackLogosInstanceId);
        var observedAt = TimestampOrNow(
            telemetry?.ObservedAt ?? odometry?.ObservedAt);

        double? latitude = null;
        double? longitude = null;
        double? altitudeMsl = null;
        double? localNorth = null;
        double? localEast = null;
        double? localDown = null;

        ApplyPoint(
            odometry?.GlobalPosition,
            ref latitude,
            ref longitude,
            ref altitudeMsl,
            ref localNorth,
            ref localEast,
            ref localDown);
        ApplyPoint(
            odometry?.LocalPosition,
            ref latitude,
            ref longitude,
            ref altitudeMsl,
            ref localNorth,
            ref localEast,
            ref localDown);

        double? velocityNorth = null;
        double? velocityEast = null;
        double? velocityDown = null;
        ApplyVector(
            odometry?.LocalVelocity,
            ref velocityNorth,
            ref velocityEast,
            ref velocityDown);

        var flightState = telemetry?.FlightState;
        var freshness = telemetry?.Freshness;
        var stale = odometry?.Stale == true ||
                    freshness?.OdometryStale == true ||
                    freshness?.StatusStale == true ||
                    freshness?.GlobalPositionStale == true;
        var health = telemetry?.Health.ToString() ?? "Unknown";
        var state = stale
            ? AvailabilityState.Stale
            : MapHealth(health);

        return new VehicleTelemetryRecord(
            $"{connectionId}:{vehicleId}",
            vehicleId,
            connectionId,
            EmptyToNull(logosInstanceId),
            state,
            flightState?.Armed ?? false,
            flightState?.LandedState.ToString() ?? "Unknown",
            flightState?.AirframeMode.ToString() ?? "Unknown",
            EmptyToFallback(flightState?.AdapterStateLabel, "Unknown"),
            health,
            telemetry?.Readiness.ToString() ?? "Unknown",
            latitude,
            longitude,
            altitudeMsl,
            odometry is null ? null : odometry.AltitudeAglM,
            localNorth,
            localEast,
            localDown,
            velocityNorth,
            velocityEast,
            velocityDown,
            odometry?.Attitude is null
                ? null
                : RadiansToDegrees(odometry.Attitude.YawRad),
            stale,
            EmptyToFallback(telemetry?.Code ?? odometry?.Code, "Unknown"),
            telemetry?.Message ?? odometry?.Message ?? string.Empty,
            observedAt);
    }

    public static LinkRecord? ToLinkRecord(
        string connectionId,
        string? logosInstanceId,
        LinkDescriptor? descriptor,
        LinkStatus? status)
    {
        var linkId = FirstNonEmpty(descriptor?.LinkId, status?.LinkId);
        if (string.IsNullOrWhiteSpace(linkId))
        {
            return null;
        }

        var peer = status?.Peer;
        var quality = status?.Quality;
        var observedAt = TimestampOrNow(status?.ObservedAt ?? status?.LastSeenAt);

        return new LinkRecord(
            $"{connectionId}:{linkId}",
            linkId,
            connectionId,
            EmptyToNull(logosInstanceId),
            EmptyToFallback(descriptor?.DisplayName, linkId),
            descriptor?.Kind.ToString() ?? "Unknown",
            descriptor?.Direction.ToString() ?? "Unknown",
            status?.State.ToString() ?? "Unknown",
            status?.Health.ToString() ?? "Unknown",
            status?.Readiness.ToString() ?? "Unknown",
            status?.Connected ?? false,
            status?.Stale ?? false,
            EmptyToNull(peer?.PeerId),
            EmptyToNull(peer?.PeerKind),
            EmptyToNull(peer?.DisplayName),
            quality is null ? null : quality.RssiDbm,
            quality is null ? null : quality.SnrDb,
            quality is null ? null : quality.Quality,
            quality is null ? null : quality.PacketLoss,
            quality is null ? null : quality.LatencyMs,
            EmptyToFallback(status?.Code, "Unknown"),
            status?.Message ?? string.Empty,
            observedAt);
    }

    public static ConsoleEventRecord? ToEventRecord(
        string connectionId,
        string? logosInstanceId,
        LogosEvent? item)
    {
        if (item is null)
        {
            return null;
        }

        var occurredAt = TimestampOrNow(item.OccurredAt ?? item.RecordedAt);
        var source = FirstNonEmpty(item.Actor?.Component, item.Domain.ToString(), "Logos");
        var subjectId = FirstNonEmpty(
            item.Subject?.ResourceId,
            item.Subject?.VehicleId,
            item.Subject?.MissionId,
            item.Subject?.TaskId,
            item.Subject?.TeamId,
            item.Subject?.LinkId,
            item.Subject?.LogosInstanceId);
        var id = string.IsNullOrWhiteSpace(item.EventId)
            ? CreateFallbackEventId(connectionId, item, occurredAt, subjectId)
            : $"{connectionId}:{item.EventId}";

        return new ConsoleEventRecord(
            id,
            occurredAt,
            item.Severity.ToString(),
            source,
            EmptyToFallback(item.Message, EmptyToFallback(item.Code, item.Kind.ToString())),
            connectionId,
            EmptyToNull(FirstNonEmpty(item.Subject?.LogosInstanceId, logosInstanceId)),
            item.Domain.ToString(),
            item.Kind.ToString(),
            EmptyToNull(item.Code),
            EmptyToNull(subjectId));
    }


    public static GeometryOverlayRecord? ToGeometryOverlayRecord(
        string connectionId,
        string? logosInstanceId,
        GeometryObject? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.GeometryId))
        {
            return null;
        }

        var frame = MapGeometryFrame(item.FrameScope, item.FrameId);
        var points = item.Points
            .Select(point => new OperationalPoint(point.X, point.Y, point.Z))
            .ToArray();
        var rings = item.Rings
            .Select(ring => (IReadOnlyList<OperationalPoint>)ring.Points
                .Select(point => new OperationalPoint(point.X, point.Y, point.Z))
                .ToArray())
            .ToArray();

        return new GeometryOverlayRecord(
            $"{connectionId}:{item.GeometryId}",
            item.GeometryId,
            connectionId,
            EmptyToNull(logosInstanceId),
            EmptyToFallback(item.DisplayName, item.GeometryId),
            item.Kind.ToString(),
            frame,
            item.Closed,
            points,
            rings,
            EmptyToFallback(item.Policy?.Kind, "none"),
            EmptyToFallback(item.Policy?.Constraint, "none"),
            TimestampOrNow(item.UpdatedAt));
    }

    public static PerceptionTrackRecord? ToPerceptionTrackRecord(
        string connectionId,
        string? logosInstanceId,
        Track2D? item,
        Timestamp? observedAt = null)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.TrackId))
        {
            return null;
        }

        return new PerceptionTrackRecord(
            $"{connectionId}:{item.TrackId}",
            item.TrackId,
            connectionId,
            EmptyToNull(logosInstanceId),
            EmptyToFallback(item.ClassId, "unknown"),
            item.Confidence,
            item.Box?.CenterX ?? 0,
            item.Box?.CenterY ?? 0,
            item.Box?.SizeX ?? 0,
            item.Box?.SizeY ?? 0,
            item.AgeFrames,
            item.HitCount,
            item.MissCount,
            EmptyToNull(item.Image?.SourceId),
            EmptyToNull(item.Image?.FrameId),
            TimestampOrNow(observedAt ?? item.Provenance?.ObservedAt ?? item.Image?.ImageTimestamp));
    }

    public static CameraSourceRecord? ToCameraSourceRecord(
        string connectionId,
        string? logosInstanceId,
        CameraSourceDescriptor? descriptor,
        CameraSourceStatus? status,
        CameraMuxStatus? muxStatus = null)
    {
        var sourceId = FirstNonEmpty(descriptor?.CameraSourceId, status?.CameraSourceId);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        var intrinsics = status?.Intrinsics ?? descriptor?.Intrinsics;
        var availability = status is null
            ? AvailabilityState.Unknown
            : MapAvailability(status.Availability.ToString(), status.Fresh);
        return new CameraSourceRecord(
            $"{connectionId}:{sourceId}",
            sourceId,
            connectionId,
            EmptyToNull(logosInstanceId),
            EmptyToFallback(descriptor?.DisplayName, sourceId),
            descriptor?.Kind.ToString() ?? status?.Kind.ToString() ?? "Unknown",
            availability,
            status?.Health.ToString() ?? "Unknown",
            status?.Readiness.ToString() ?? "Unknown",
            status?.Active == true || string.Equals(muxStatus?.ActiveCameraSourceId, sourceId, StringComparison.Ordinal),
            status?.Fresh ?? false,
            status?.HasImage ?? false,
            status?.FrameRateHz ?? 0,
            status?.LatencyMs ?? 0,
            intrinsics?.Width ?? 0,
            intrinsics?.Height ?? 0,
            EmptyToFallback(status?.FrameId ?? descriptor?.FrameId, "Unknown"),
            EmptyToFallback(status?.Code, "Unknown"),
            status?.Message ?? string.Empty,
            TimestampOrNow(status?.ObservedAt ?? status?.LastFrameAt));
    }

    public static CameraStreamRecord? ToCameraStreamRecord(
        string connectionId,
        string? logosInstanceId,
        VideoStreamSession? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.StreamId))
        {
            return null;
        }

        return new CameraStreamRecord(
            $"{connectionId}:{session.StreamId}",
            session.StreamId,
            session.CameraSourceId,
            connectionId,
            EmptyToNull(logosInstanceId),
            session.Protocol.ToString(),
            session.State.ToString(),
            session.StreamUrl ?? string.Empty,
            session.NegotiationPayload ?? string.Empty,
            session.Codec ?? string.Empty,
            session.Width,
            session.Height,
            session.FrameRateHz,
            session.BitrateKbps,
            TimestampOrNull(session.OpenedAt),
            TimestampOrNull(session.ExpiresAt),
            EmptyToFallback(session.Code, "Unknown"),
            session.Message ?? string.Empty,
            DateTimeOffset.UtcNow);
    }

    private static void ApplyPoint(
        SpatialPointSample? sample,
        ref double? latitude,
        ref double? longitude,
        ref double? altitudeMsl,
        ref double? localNorth,
        ref double? localEast,
        ref double? localDown)
    {
        if (sample?.Point is null)
        {
            return;
        }

        switch (sample.FrameScope)
        {
            case GeometryFrameScope.GlobalWgs84:
                longitude = sample.Point.X;
                latitude = sample.Point.Y;
                altitudeMsl = sample.Point.Z;
                break;
            case GeometryFrameScope.LocalNed:
                localNorth = sample.Point.X;
                localEast = sample.Point.Y;
                localDown = sample.Point.Z;
                break;
            case GeometryFrameScope.LocalEnu:
                localEast = sample.Point.X;
                localNorth = sample.Point.Y;
                localDown = -sample.Point.Z;
                break;
        }
    }

    private static void ApplyVector(
        SpatialVectorSample? sample,
        ref double? north,
        ref double? east,
        ref double? down)
    {
        if (sample?.Vector is null)
        {
            return;
        }

        switch (sample.FrameScope)
        {
            case GeometryFrameScope.LocalNed:
                north = sample.Vector.X;
                east = sample.Vector.Y;
                down = sample.Vector.Z;
                break;
            case GeometryFrameScope.LocalEnu:
                east = sample.Vector.X;
                north = sample.Vector.Y;
                down = -sample.Vector.Z;
                break;
        }
    }


    private static MapFrameKind MapGeometryFrame(
        GeometryFrameScope scope,
        string? frameId)
    {
        var mapped = scope switch
        {
            GeometryFrameScope.GlobalWgs84 => MapFrameKind.GlobalWgs84,
            GeometryFrameScope.LocalEnu => MapFrameKind.LocalEnu,
            GeometryFrameScope.LocalNed => MapFrameKind.LocalNed,
            _ => MapFrameKind.Unknown
        };
        if (mapped != MapFrameKind.Unknown || string.IsNullOrWhiteSpace(frameId))
        {
            return mapped;
        }

        var normalized = frameId.Trim().Replace('-', '_').ToLowerInvariant();
        if (normalized.Contains("wgs84", StringComparison.Ordinal) ||
            normalized.Contains("global", StringComparison.Ordinal))
        {
            return MapFrameKind.GlobalWgs84;
        }

        if (normalized.Contains("enu", StringComparison.Ordinal))
        {
            return MapFrameKind.LocalEnu;
        }

        if (normalized.Contains("ned", StringComparison.Ordinal))
        {
            return MapFrameKind.LocalNed;
        }

        return MapFrameKind.Unknown;
    }

    private static AvailabilityState MapAvailability(string availability, bool fresh)
    {
        if (availability.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
            availability.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            availability.Contains("Offline", StringComparison.OrdinalIgnoreCase))
        {
            return AvailabilityState.Offline;
        }

        if (availability.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
        {
            return AvailabilityState.Degraded;
        }

        if (availability.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            availability.Contains("Unspecified", StringComparison.OrdinalIgnoreCase))
        {
            return fresh ? AvailabilityState.Online : AvailabilityState.Unknown;
        }

        return fresh ? AvailabilityState.Online : AvailabilityState.Stale;
    }

    private static AvailabilityState MapHealth(string health)
    {
        if (health.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            health.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) ||
            health.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return AvailabilityState.Degraded;
        }

        if (health.Contains("Degraded", StringComparison.OrdinalIgnoreCase) ||
            health.Contains("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return AvailabilityState.Degraded;
        }

        return AvailabilityState.Online;
    }

    private static DateTimeOffset? TimestampOrNull(Timestamp? timestamp)
    {
        if (timestamp is null || (timestamp.Seconds == 0 && timestamp.Nanos == 0))
        {
            return null;
        }

        return timestamp.ToDateTimeOffset();
    }

    private static DateTimeOffset TimestampOrNow(Timestamp? timestamp)
    {
        if (timestamp is null || (timestamp.Seconds == 0 && timestamp.Nanos == 0))
        {
            return DateTimeOffset.UtcNow;
        }

        return timestamp.ToDateTimeOffset();
    }

    private static string CreateFallbackEventId(
        string connectionId,
        LogosEvent item,
        DateTimeOffset occurredAt,
        string subjectId)
    {
        var source = string.Join('|',
            connectionId,
            occurredAt.ToString("O"),
            item.Domain,
            item.Kind,
            item.Code,
            subjectId,
            item.Message);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"{connectionId}:generated:{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static double RadiansToDegrees(double radians)
        => radians * (180d / Math.PI);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string EmptyToFallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
