namespace RobotCommand.Services.Maps;

public readonly record struct MapFollowCameraUpdate(double CenterX, double CenterY);

/// <summary>
/// Filters a geographic follow target before it is applied to the map camera.
/// The deadband prevents normal telemetry noise from moving the entire map,
/// while the zoom-dependent cadence avoids needless tile movement at wide
/// views. Prediction is based on the filtered translation, never raw fixes.
/// </summary>
public sealed class MapFollowCameraFilter
{
    private long _revision = -1;
    private DateTimeOffset _lastSampleAt;
    private double _filteredX;
    private double _filteredY;
    private double _cameraX;
    private double _cameraY;
    private double _velocityX;
    private double _velocityY;

    public void Reset()
    {
        _revision = -1;
        _lastSampleAt = default;
        _filteredX = 0;
        _filteredY = 0;
        _cameraX = 0;
        _cameraY = 0;
        _velocityX = 0;
        _velocityY = 0;
    }

    public bool TryUpdate(
        long revision,
        double targetX,
        double targetY,
        double resolution,
        DateTimeOffset now,
        out MapFollowCameraUpdate update)
    {
        update = default;
        if (!double.IsFinite(targetX) ||
            !double.IsFinite(targetY) ||
            !double.IsFinite(resolution) ||
            resolution <= 0)
        {
            return false;
        }

        if (_revision != revision)
        {
            _revision = revision;
            _lastSampleAt = now;
            _filteredX = targetX;
            _filteredY = targetY;
            _cameraX = targetX;
            _cameraY = targetY;
            _velocityX = 0;
            _velocityY = 0;
            return false;
        }

        var elapsed = (now - _lastSampleAt).TotalSeconds;
        var interval = UpdateInterval(resolution).TotalSeconds;
        if (elapsed < interval)
        {
            return false;
        }

        elapsed = Math.Clamp(elapsed, interval, 1.0);
        _lastSampleAt = now;

        var deltaX = targetX - _filteredX;
        var deltaY = targetY - _filteredY;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        var deadband = TranslationDeadband(resolution);
        if (distance <= deadband)
        {
            // Decay any old velocity while stationary. A noisy fix inside the
            // deadband must never keep pulling the camera around.
            var decay = Math.Exp(-elapsed / 0.35);
            _velocityX *= decay;
            _velocityY *= decay;
            return false;
        }

        // Remove the deadband from the observed displacement. This keeps the
        // filter continuous when genuine motion finally leaves the noise area.
        var effectiveDistance = distance - deadband;
        var measuredX = _filteredX + (deltaX * effectiveDistance / distance);
        var measuredY = _filteredY + (deltaY * effectiveDistance / distance);
        var priorFilteredX = _filteredX;
        var priorFilteredY = _filteredY;
        var observationResponse = ObservationResponseSeconds(resolution);
        var observationAlpha = 1 - Math.Exp(-elapsed / observationResponse);
        _filteredX += (measuredX - _filteredX) * observationAlpha;
        _filteredY += (measuredY - _filteredY) * observationAlpha;

        var measuredVelocityX = (_filteredX - priorFilteredX) / elapsed;
        var measuredVelocityY = (_filteredY - priorFilteredY) / elapsed;
        var velocityAlpha = 1 - Math.Exp(-elapsed / 0.45);
        _velocityX += (measuredVelocityX - _velocityX) * velocityAlpha;
        _velocityY += (measuredVelocityY - _velocityY) * velocityAlpha;

        var predictionSeconds = PredictionSeconds(resolution);
        var leadX = _velocityX * predictionSeconds;
        var leadY = _velocityY * predictionSeconds;
        var leadDistance = Math.Sqrt((leadX * leadX) + (leadY * leadY));
        var maximumLead = Math.Max(resolution * 2.0, 1.0);
        if (leadDistance > maximumLead)
        {
            leadX *= maximumLead / leadDistance;
            leadY *= maximumLead / leadDistance;
        }

        var desiredX = _filteredX + leadX;
        var desiredY = _filteredY + leadY;
        var cameraResponse = CameraResponseSeconds(resolution);
        var cameraAlpha = 1 - Math.Exp(-elapsed / cameraResponse);
        var nextX = _cameraX + ((desiredX - _cameraX) * cameraAlpha);
        var nextY = _cameraY + ((desiredY - _cameraY) * cameraAlpha);
        var cameraDistance = Math.Sqrt(
            ((nextX - _cameraX) * (nextX - _cameraX)) +
            ((nextY - _cameraY) * (nextY - _cameraY)));

        // Avoid issuing sub-pixel navigator changes. Mapsui may still redraw
        // tiles for these even though they are visually meaningless.
        if (cameraDistance < Math.Max(resolution * 0.08, 0.05))
        {
            return false;
        }

        _cameraX = nextX;
        _cameraY = nextY;
        update = new MapFollowCameraUpdate(nextX, nextY);
        return true;
    }

    public static TimeSpan UpdateInterval(double resolution)
        => resolution switch
        {
            <= 2 => TimeSpan.FromMilliseconds(33),
            <= 5 => TimeSpan.FromMilliseconds(50),
            <= 15 => TimeSpan.FromMilliseconds(125),
            <= 50 => TimeSpan.FromMilliseconds(250),
            _ => TimeSpan.FromMilliseconds(500)
        };

    public static double TranslationDeadband(double resolution)
    {
        var pixels = resolution switch
        {
            <= 5 => 0.5,
            <= 20 => 0.65,
            _ => 0.8
        };
        return Math.Max(0.5, resolution * pixels);
    }

    private static double ObservationResponseSeconds(double resolution)
        => resolution switch
        {
            <= 5 => 0.18,
            <= 20 => 0.35,
            <= 50 => 0.6,
            _ => 0.9
        };

    private static double CameraResponseSeconds(double resolution)
        => resolution switch
        {
            <= 5 => 0.22,
            <= 20 => 0.4,
            <= 50 => 0.65,
            _ => 0.85
        };

    private static double PredictionSeconds(double resolution)
        => resolution switch
        {
            <= 5 => 0.3,
            <= 20 => 0.22,
            _ => 0.12
        };
}
