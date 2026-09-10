using RobotCommand.Models;
using RobotCommand.Services.Mavlink;

namespace RobotCommand.Services.Operations;

public static class OperatorControlRules
{
    /// <summary>
    /// Evaluates the common readiness required before a controller can acquire
    /// a manual session.  It deliberately shares the connection and telemetry
    /// predicates used by every operator operation, rather than comparing the
    /// backend-specific display values of VehicleRecord.Health.
    /// </summary>
    public static ManualControlReadiness EvaluateManualControl(
        VehicleRecord vehicle,
        VehicleTelemetryRecord? telemetry,
        ConnectionRecord? connection,
        VehicleDiagnosticsSnapshot? diagnostics = null,
        string? backend = null,
        string? mode = null)
    {
        var findings = new List<OperatorPreflightFinding>();
        EvaluateConnectionAndTelemetry(vehicle, telemetry, connection, findings);

        var capabilities = vehicle.CapabilityKeys ?? Array.Empty<string>();
        var normalized = capabilities.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (capabilities.Count > 0 && !normalized.Contains("operator_control") &&
            !normalized.Contains("vehicle_control") && !normalized.Contains("manual_control") &&
            !normalized.Contains("operational_control"))
        {
            findings.Add(Block("MANUAL_CONTROL_NOT_ADVERTISED",
                "The vehicle does not advertise operator or manual control support."));
        }

        if (string.Equals(backend, "ArduPilot", StringComparison.OrdinalIgnoreCase))
        {
            var modeClass = mode switch
            {
                "Stabilize" or "Acro" or "AltHold" or "Sport" => ManualControlModeClass.GpsIndependent,
                "Loiter" or "PosHold" => ManualControlModeClass.PositionAssisted,
                _ => ManualControlModeClass.Unsupported
            };
            if (modeClass == ManualControlModeClass.Unsupported)
            {
                findings.Add(Block("ARDUPILOT_MANUAL_MODE_UNSUPPORTED",
                    $"ArduPilot manual control is unavailable in {mode ?? "the current mode"}. Use Stabilize, Acro, AltHold, Sport, Loiter, or PosHold."));
            }
            else
            {
                EvaluateDiagnosticsForArduPilotManual(modeClass, diagnostics, findings);
            }
        }
        else
        {
            // Position-assisted stick control needs the same navigation
            // readiness as navigation commands, without requiring an airborne
            // target or a fabricated command parameter.
            EvaluateDiagnostics(OperatorCommandKind.SetHeading, diagnostics, findings);
        }

        if (findings.Count == 0)
        {
            findings.Add(new OperatorPreflightFinding(
                "MANUAL_CONTROL_PREFLIGHT_OK",
                OperatorPreflightSeverity.Info,
                "Connection, telemetry, capability, and navigation checks passed."));
        }

        return new ManualControlReadiness(
            !findings.Any(item => item.Severity == OperatorPreflightSeverity.Blocking),
            findings);
    }

    private static void EvaluateDiagnosticsForArduPilotManual(
        ManualControlModeClass modeClass,
        VehicleDiagnosticsSnapshot? diagnostics,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (diagnostics is null) return;
        foreach (var failure in diagnostics.Checks.Where(check =>
                     check.State == VehicleDiagnosticCheckState.Failed &&
                     check.AffectedOperations?.Contains(OperatorCommandKind.SetHeading) == true))
        {
            var gpsEvidence = failure.Code.Contains("GPS", StringComparison.OrdinalIgnoreCase) ||
                              failure.Code.Contains("GLOBAL_POSITION", StringComparison.OrdinalIgnoreCase) ||
                              failure.Name.Contains("position", StringComparison.OrdinalIgnoreCase);
            var preArm = failure.Code.Contains("PREARM", StringComparison.OrdinalIgnoreCase) ||
                         failure.Code.Contains("STATUSTEXT", StringComparison.OrdinalIgnoreCase) &&
                         failure.Detail.Contains("PreArm:", StringComparison.OrdinalIgnoreCase);
            if (modeClass == ManualControlModeClass.GpsIndependent && (gpsEvidence || preArm)) continue;
            findings.Add(Block(failure.Code, failure.Detail));
        }
    }

    public static IReadOnlyList<OperatorPreflightFinding> Evaluate(
        VehicleRecord vehicle,
        VehicleTelemetryRecord? telemetry,
        ConnectionRecord? connection,
        OperatorCommandKind command,
        OperatorCommandParameters? parameters = null,
        VehicleDiagnosticsSnapshot? diagnostics = null)
    {
        var findings = new List<OperatorPreflightFinding>();

        EvaluateConnectionAndTelemetry(vehicle, telemetry, connection, findings);
        EvaluateCapability(vehicle, command, findings);
        if (!IsCameraCommand(command))
        {
            EvaluateDiagnostics(command, diagnostics, findings);
        }
        EvaluateCommandState(vehicle, telemetry, command, parameters, findings);

        if (findings.Count == 0)
        {
            findings.Add(new OperatorPreflightFinding(
                "LOCAL_PREFLIGHT_OK",
                OperatorPreflightSeverity.Info,
                "Local connection, freshness, capability, and vehicle-state checks passed."));
        }

        return findings;
    }

    private static void EvaluateConnectionAndTelemetry(
        VehicleRecord vehicle,
        VehicleTelemetryRecord? telemetry,
        ConnectionRecord? connection,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (connection is null)
        {
            findings.Add(Block("CONNECTION_MISSING", "No Logos connection routes to the selected vehicle."));
        }
        else if (connection.State is AvailabilityState.Offline or AvailabilityState.Faulted)
        {
            findings.Add(Block("CONNECTION_OFFLINE", $"Connection '{connection.Name}' is {connection.State}."));
        }
        else if (connection.State is AvailabilityState.Stale or AvailabilityState.Reconnecting or AvailabilityState.Connecting)
        {
            findings.Add(Warn("CONNECTION_NOT_READY", $"Connection '{connection.Name}' is {connection.State}; command confirmation may be delayed."));
        }
        else if (connection.State == AvailabilityState.Degraded)
        {
            findings.Add(Warn("CONNECTION_DEGRADED", $"Connection '{connection.Name}' is degraded."));
        }

        if (vehicle.State is AvailabilityState.Offline or AvailabilityState.Faulted)
        {
            findings.Add(Block("VEHICLE_NOT_CURRENT", $"Vehicle state is {vehicle.State}."));
        }
        else if (vehicle.State == AvailabilityState.Stale)
        {
            findings.Add(Warn("VEHICLE_NOT_CURRENT", "Vehicle telemetry is delayed; command outcome may be unknown until it recovers."));
        }

        if (telemetry is null)
        {
            findings.Add(Block("TELEMETRY_MISSING", "No current vehicle telemetry is available."));
        }
        else if (telemetry.IsStale || telemetry.State == AvailabilityState.Stale)
        {
            findings.Add(Warn("TELEMETRY_STALE", "Vehicle telemetry is delayed; the flight controller remains authoritative."));
        }
    }

    private static void EvaluateDiagnostics(
        OperatorCommandKind command,
        VehicleDiagnosticsSnapshot? diagnostics,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (diagnostics is null || command is OperatorCommandKind.Hold or OperatorCommandKind.Land or
            OperatorCommandKind.Recover or OperatorCommandKind.Disarm)
        {
            return;
        }

        var relevantFailures = diagnostics.Checks.Where(check =>
            check.State == VehicleDiagnosticCheckState.Failed &&
            check.AffectedOperations?.Contains(command) == true);
        foreach (var failure in relevantFailures)
        {
            findings.Add(Block(failure.Code, failure.Detail));
        }

        var readiness = command == OperatorCommandKind.Arm
            ? diagnostics.ArmReadiness
            : command == OperatorCommandKind.Takeoff
                ? diagnostics.ArmReadiness == VehicleDiagnosticStatus.Ready
                    ? diagnostics.NavigationReadiness
                    : diagnostics.ArmReadiness
                : diagnostics.NavigationReadiness;
        if (readiness is VehicleDiagnosticStatus.Unknown or VehicleDiagnosticStatus.Limited)
        {
            var detail = command == OperatorCommandKind.Arm
                ? diagnostics.ArmReadinessDetail
                : diagnostics.NavigationReadinessDetail;
            findings.Add(Warn("VEHICLE_DIAGNOSTICS_INCOMPLETE", detail));
        }
        else if (readiness is VehicleDiagnosticStatus.Stale or VehicleDiagnosticStatus.Offline)
        {
            findings.Add(Warn("VEHICLE_DIAGNOSTICS_NOT_CURRENT", $"Vehicle diagnostics are {readiness.ToString().ToLowerInvariant()}."));
        }
    }

    public static OperatorCommandSafety SafetyFor(
        OperatorCommandKind command,
        VehicleTelemetryRecord? telemetry)
        => command switch
        {
            OperatorCommandKind.Hold => OperatorCommandSafety.Routine,
            OperatorCommandKind.Disarm when telemetry?.Armed == true && !IsLanded(telemetry) => OperatorCommandSafety.Critical,
            OperatorCommandKind.Disarm => OperatorCommandSafety.Elevated,
            OperatorCommandKind.Arm => OperatorCommandSafety.Elevated,
            OperatorCommandKind.Takeoff => OperatorCommandSafety.Elevated,
            OperatorCommandKind.GoTo => OperatorCommandSafety.Elevated,
            OperatorCommandKind.ChangeAltitude => OperatorCommandSafety.Elevated,
            OperatorCommandKind.SetHeading => OperatorCommandSafety.Elevated,
            OperatorCommandKind.CapturePhoto or OperatorCommandKind.StartVideo or
            OperatorCommandKind.StopVideo or OperatorCommandKind.CenterGimbal or
            OperatorCommandKind.NadirGimbal or OperatorCommandKind.SetGimbal => OperatorCommandSafety.Routine,
            OperatorCommandKind.Land => OperatorCommandSafety.Elevated,
            OperatorCommandKind.Recover => OperatorCommandSafety.Elevated,
            _ => OperatorCommandSafety.Elevated
        };

    public static string ConfirmationPhrase(
        OperatorCommandKind command,
        string vehicleName,
        bool requireTypedConfirmation)
    {
        if (!requireTypedConfirmation || command == OperatorCommandKind.Hold)
        {
            return string.Empty;
        }

        var verb = command switch
        {
            OperatorCommandKind.Recover => "RETURN HOME",
            OperatorCommandKind.Takeoff => "TAKE OFF",
            OperatorCommandKind.GoTo => "GO TO",
            OperatorCommandKind.ChangeAltitude => "CHANGE ALTITUDE",
            OperatorCommandKind.SetHeading => "SET HEADING",
            _ => command.ToString().ToUpperInvariant()
        };
        return $"{verb} {vehicleName}";
    }

    public static string DisplayName(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.Takeoff => "Take off",
            OperatorCommandKind.GoTo => "Go to",
            OperatorCommandKind.ChangeAltitude => "Change altitude",
            OperatorCommandKind.SetHeading => "Set heading",
            OperatorCommandKind.Recover => "Return home",
            OperatorCommandKind.CapturePhoto => "Capture photo",
            OperatorCommandKind.StartVideo => "Start video",
            OperatorCommandKind.StopVideo => "Stop video",
            OperatorCommandKind.CenterGimbal => "Centre gimbal",
            OperatorCommandKind.NadirGimbal => "Nadir gimbal",
            OperatorCommandKind.SetGimbal => "Set gimbal",
            _ => command.ToString()
        };

    private static void EvaluateCapability(
        VehicleRecord vehicle,
        OperatorCommandKind command,
        ICollection<OperatorPreflightFinding> findings)
    {
        var capabilities = vehicle.CapabilityKeys ?? Array.Empty<string>();
        if (capabilities.Count == 0)
        {
            findings.Add(Warn(
                "CAPABILITIES_UNKNOWN",
                "The vehicle did not advertise operator-control capabilities. Logos must confirm support remotely."));
            return;
        }

        var normalized = capabilities
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);
        var common = normalized.Contains("operator_control") ||
                     normalized.Contains("vehicle_control") ||
                     normalized.Contains("manual_control") ||
                     normalized.Contains("operational_control");
        var specific = CommandAliases(command).Any(normalized.Contains);

        if (!common && !specific)
        {
            findings.Add(Block(
                "CAPABILITY_NOT_ADVERTISED",
                $"The vehicle does not advertise support for {DisplayName(command).ToLowerInvariant()}."));
        }
    }

    private static void EvaluateCommandState(
        VehicleRecord vehicle,
        VehicleTelemetryRecord? telemetry,
        OperatorCommandKind command,
        OperatorCommandParameters? parameters,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (telemetry is null)
        {
            return;
        }

        switch (command)
        {
            case OperatorCommandKind.CapturePhoto or
                OperatorCommandKind.StartVideo or
                OperatorCommandKind.StopVideo or
                OperatorCommandKind.CenterGimbal or
                OperatorCommandKind.NadirGimbal or
                OperatorCommandKind.SetGimbal:
                ValidateGimbalParameters(command, parameters, findings);
                break;
            case OperatorCommandKind.Arm:
                if (telemetry.Armed)
                {
                    findings.Add(Block("ALREADY_ARMED", "The vehicle already reports armed."));
                }

                if (IsExplicitlyNotReady(telemetry.Readiness, vehicle.Readiness))
                {
                    findings.Add(Block("VEHICLE_NOT_READY", $"Vehicle readiness is '{telemetry.Readiness}'."));
                }
                break;

            case OperatorCommandKind.Disarm:
                if (!telemetry.Armed)
                {
                    findings.Add(Block("ALREADY_DISARMED", "The vehicle already reports disarmed."));
                }
                else if (!IsLanded(telemetry))
                {
                    if (parameters?.AirborneDisarmConfirmed != true)
                    {
                        findings.Add(Block(
                            "AIRBORNE_DISARM_CONFIRMATION_REQUIRED",
                            "Disarming an airborne vehicle requires explicit operator confirmation."));
                    }
                }
                break;

            case OperatorCommandKind.Hold:
                if (!telemetry.Armed)
                {
                    findings.Add(Block("HOLD_REQUIRES_ACTIVE_VEHICLE", "Hold is unavailable while the vehicle is disarmed."));
                }
                break;

            case OperatorCommandKind.Takeoff:
                if (!IsAirDomain(vehicle.Domain))
                {
                    findings.Add(Block("TAKEOFF_NOT_APPLICABLE", $"Takeoff is not applicable to the '{vehicle.Domain}' domain."));
                }
                else if (!telemetry.Armed)
                {
                    findings.Add(Block("TAKEOFF_REQUIRES_ARMED", "Takeoff requires the vehicle to be armed first."));
                }
                else if (!IsLanded(telemetry))
                {
                    findings.Add(Block("TAKEOFF_REQUIRES_LANDED", "Takeoff is only available while the vehicle reports landed."));
                }

                var altitude = parameters?.TakeoffAltitudeAglMetres;
                if (altitude is null || !double.IsFinite(altitude.Value))
                {
                    findings.Add(Block("TAKEOFF_ALTITUDE_REQUIRED", "Takeoff requires a finite AGL altitude."));
                }
                else if (altitude.Value is < 0.5 or > 500)
                {
                    findings.Add(Block(
                        "TAKEOFF_ALTITUDE_OUT_OF_RANGE",
                        "Takeoff altitude must be between 0.5 m and 500 m AGL; Logos policy may impose a lower limit."));
                }
                break;

            case OperatorCommandKind.GoTo:
                if (!telemetry.Armed)
                {
                    findings.Add(Block("GO_TO_REQUIRES_ARMED", "Go To requires the vehicle to be armed."));
                }
                else if (IsAirDomain(vehicle.Domain) && IsLanded(telemetry))
                {
                    findings.Add(Block("GO_TO_REQUIRES_AIRBORNE", "Go To requires an air-domain vehicle to be airborne."));
                }

                ValidateGoToParameters(parameters, findings);
                break;

            case OperatorCommandKind.ChangeAltitude:
                if (!IsAirDomain(vehicle.Domain))
                {
                    findings.Add(Block(
                        "CHANGE_ALTITUDE_NOT_APPLICABLE",
                        $"Change altitude is not applicable to the '{vehicle.Domain}' domain."));
                }
                else if (!telemetry.Armed)
                {
                    findings.Add(Block("CHANGE_ALTITUDE_REQUIRES_ARMED", "Change altitude requires the vehicle to be armed."));
                }
                else if (IsLanded(telemetry))
                {
                    findings.Add(Block("CHANGE_ALTITUDE_REQUIRES_AIRBORNE", "Change altitude requires the vehicle to be airborne."));
                }

                ValidateAltitudeParameters(parameters, findings);
                break;

            case OperatorCommandKind.SetHeading:
                if (!telemetry.Armed)
                {
                    findings.Add(Block("SET_HEADING_REQUIRES_ARMED", "Set heading requires the vehicle to be armed."));
                }
                else if (IsAirDomain(vehicle.Domain) && IsLanded(telemetry))
                {
                    findings.Add(Block("SET_HEADING_REQUIRES_AIRBORNE", "Set heading requires an air-domain vehicle to be airborne."));
                }

                ValidateHeadingParameters(parameters, findings);
                break;

            case OperatorCommandKind.Land:
                if (!IsAirDomain(vehicle.Domain))
                {
                    findings.Add(Block("LAND_NOT_APPLICABLE", $"Land is not applicable to the '{vehicle.Domain}' domain."));
                }
                else if (!telemetry.Armed)
                {
                    findings.Add(Block("LAND_REQUIRES_ARMED", "Land is unavailable while the vehicle is disarmed."));
                }
                else if (IsLanded(telemetry))
                {
                    findings.Add(Block("ALREADY_LANDED", "The vehicle already reports landed."));
                }
                break;

            case OperatorCommandKind.Recover:
                if (!telemetry.Armed)
                {
                    findings.Add(Block("RECOVER_REQUIRES_ACTIVE_VEHICLE", "Recover / return is unavailable while the vehicle is disarmed."));
                }
                break;
        }
    }

    private static void ValidateGimbalParameters(
        OperatorCommandKind command,
        OperatorCommandParameters? parameters,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (command == OperatorCommandKind.SetGimbal &&
            parameters?.GimbalPitchDegrees is null && parameters?.GimbalYawDegrees is null &&
            parameters?.GimbalRollDegrees is null && parameters?.GimbalZoomPercent is null)
        {
            findings.Add(Block("GIMBAL_TARGET_REQUIRED", "Set gimbal requires at least one angle or zoom value."));
        }

        if (parameters?.GimbalPitchDegrees is { } pitch && (!double.IsFinite(pitch) || pitch is < -90 or > 90))
            findings.Add(Block("GIMBAL_PITCH_INVALID", "Gimbal pitch must be between -90 and 90 degrees."));
        if (parameters?.GimbalYawDegrees is { } yaw && (!double.IsFinite(yaw) || yaw is < -360 or > 360))
            findings.Add(Block("GIMBAL_YAW_INVALID", "Gimbal yaw must be between -360 and 360 degrees."));
        if (parameters?.GimbalRollDegrees is { } roll && (!double.IsFinite(roll) || roll is < -360 or > 360))
            findings.Add(Block("GIMBAL_ROLL_INVALID", "Gimbal roll must be between -360 and 360 degrees."));
        if (parameters?.GimbalZoomPercent is { } zoom && (!double.IsFinite(zoom) || zoom is < 0 or > 100))
            findings.Add(Block("CAMERA_ZOOM_INVALID", "Camera zoom must be between 0 and 100 percent."));
    }

    private static void ValidateGoToParameters(
        OperatorCommandParameters? parameters,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (parameters?.GoToTargetKind is null)
        {
            findings.Add(Block("GO_TO_TARGET_REQUIRED", "Go To requires a global WGS84 or local NED target."));
            return;
        }

        var acceptanceRadius = parameters.GoToAcceptanceRadiusMetres;
        if (!Finite(acceptanceRadius) || acceptanceRadius is < 0.1 or > 1000)
        {
            findings.Add(Block(
                "GO_TO_ACCEPTANCE_RADIUS_INVALID",
                "Go To acceptance radius must be between 0.1 m and 1000 m."));
        }

        if (parameters.GoToTargetKind == OperatorGoToTargetKind.GlobalWgs84)
        {
            if (!Finite(parameters.GoToLatitudeDegrees) || parameters.GoToLatitudeDegrees is < -90 or > 90)
            {
                findings.Add(Block("GO_TO_LATITUDE_INVALID", "Global Go To latitude must be between -90 and 90 degrees."));
            }
            if (!Finite(parameters.GoToLongitudeDegrees) || parameters.GoToLongitudeDegrees is < -180 or > 180)
            {
                findings.Add(Block("GO_TO_LONGITUDE_INVALID", "Global Go To longitude must be between -180 and 180 degrees."));
            }
            if (!Finite(parameters.GoToAltitudeAmslMetres) || parameters.GoToAltitudeAmslMetres is < -500 or > 20000)
            {
                findings.Add(Block(
                    "GO_TO_ALTITUDE_INVALID",
                    "Global Go To altitude must be between -500 m and 20,000 m AMSL."));
            }
            return;
        }

        if (!Finite(parameters.GoToNorthMetres) || Math.Abs(parameters.GoToNorthMetres!.Value) > 100000)
        {
            findings.Add(Block("GO_TO_NORTH_INVALID", "Local Go To north offset must be within 100 km."));
        }
        if (!Finite(parameters.GoToEastMetres) || Math.Abs(parameters.GoToEastMetres!.Value) > 100000)
        {
            findings.Add(Block("GO_TO_EAST_INVALID", "Local Go To east offset must be within 100 km."));
        }
        if (!Finite(parameters.GoToDownMetres) || Math.Abs(parameters.GoToDownMetres!.Value) > 20000)
        {
            findings.Add(Block("GO_TO_DOWN_INVALID", "Local Go To down offset must be within 20 km."));
        }
        if (parameters.GoToYawDegrees is { } yaw && (!double.IsFinite(yaw) || yaw is < -360 or > 360))
        {
            findings.Add(Block("GO_TO_YAW_INVALID", "Local Go To yaw must be between -360 and 360 degrees."));
        }
    }

    private static void ValidateAltitudeParameters(
        OperatorCommandParameters? parameters,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (parameters?.AltitudeTargetKind is null)
        {
            findings.Add(Block(
                "CHANGE_ALTITUDE_TARGET_REQUIRED",
                "Change altitude requires an AMSL, AGL, or relative-delta target."));
            return;
        }

        switch (parameters.AltitudeTargetKind)
        {
            case OperatorAltitudeTargetKind.AltitudeAmsl:
                if (!Finite(parameters.AltitudeAmslMetres) || parameters.AltitudeAmslMetres is < -500 or > 20000)
                {
                    findings.Add(Block(
                        "CHANGE_ALTITUDE_AMSL_INVALID",
                        "Target altitude must be between -500 m and 20,000 m AMSL."));
                }
                break;
            case OperatorAltitudeTargetKind.AltitudeAgl:
                if (!Finite(parameters.AltitudeAglMetres) || parameters.AltitudeAglMetres is < 0.5 or > 5000)
                {
                    findings.Add(Block(
                        "CHANGE_ALTITUDE_AGL_INVALID",
                        "Target altitude must be between 0.5 m and 5,000 m AGL."));
                }
                break;
            case OperatorAltitudeTargetKind.RelativeDelta:
                if (!Finite(parameters.AltitudeRelativeDeltaMetres) ||
                    Math.Abs(parameters.AltitudeRelativeDeltaMetres!.Value) is < 0.1 or > 5000)
                {
                    findings.Add(Block(
                        "CHANGE_ALTITUDE_DELTA_INVALID",
                        "Relative altitude change must be between 0.1 m and 5,000 m in magnitude."));
                }
                break;
        }
    }

    private static void ValidateHeadingParameters(
        OperatorCommandParameters? parameters,
        ICollection<OperatorPreflightFinding> findings)
    {
        if (parameters?.HeadingTargetKind is null)
        {
            findings.Add(Block(
                "SET_HEADING_TARGET_REQUIRED",
                "Set heading requires an absolute heading or relative yaw target."));
            return;
        }

        switch (parameters.HeadingTargetKind)
        {
            case OperatorHeadingTargetKind.AbsoluteHeading:
                if (!Finite(parameters.HeadingDegrees) || parameters.HeadingDegrees is < 0 or >= 360)
                {
                    findings.Add(Block(
                        "SET_HEADING_ABSOLUTE_INVALID",
                        "Absolute heading must be at least 0 degrees and less than 360 degrees."));
                }
                break;
            case OperatorHeadingTargetKind.RelativeYaw:
                if (!Finite(parameters.RelativeYawDegrees) ||
                    Math.Abs(parameters.RelativeYawDegrees!.Value) is < 0.1 or > 360)
                {
                    findings.Add(Block(
                        "SET_HEADING_RELATIVE_INVALID",
                        "Relative yaw must be between 0.1 and 360 degrees in magnitude."));
                }
                break;
        }
    }

    private static bool Finite(double? value) => value is { } actual && double.IsFinite(actual);

    private static bool IsExplicitlyNotReady(params string[] values)
    {
        var known = values.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        return known is not null && !known.Contains("ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLanded(VehicleTelemetryRecord telemetry)
        => telemetry.LandedState.Contains("landed", StringComparison.OrdinalIgnoreCase) ||
           telemetry.LandedState.Contains("ground", StringComparison.OrdinalIgnoreCase);

    private static bool IsAirDomain(string domain)
        => domain.Contains("air", StringComparison.OrdinalIgnoreCase) ||
           domain.Contains("aerial", StringComparison.OrdinalIgnoreCase) ||
           domain.Contains("drone", StringComparison.OrdinalIgnoreCase) ||
           domain.Contains("uav", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CommandAliases(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.CapturePhoto => ["camera_photo", "camera_capture", "camera_capture_photo", "capture_photo", "camera"],
            OperatorCommandKind.StartVideo or OperatorCommandKind.StopVideo => ["camera_video", "camera_start_video", "camera_stop_video", "camera_capture_video", "camera"],
            OperatorCommandKind.CenterGimbal or OperatorCommandKind.NadirGimbal or OperatorCommandKind.SetGimbal => ["gimbal", "camera_gimbal", "gimbal_control"],
            OperatorCommandKind.Arm => ["arm", "vehicle_arm", "operator_arm"],
            OperatorCommandKind.Disarm => ["disarm", "vehicle_disarm", "operator_disarm"],
            OperatorCommandKind.Hold => ["hold", "vehicle_hold", "operator_hold"],
            OperatorCommandKind.Takeoff => ["takeoff", "take_off", "vehicle_takeoff", "operator_takeoff"],
            OperatorCommandKind.GoTo => ["goto", "go_to", "navigate", "vehicle_go_to", "operator_go_to"],
            OperatorCommandKind.ChangeAltitude => ["change_altitude", "set_altitude", "altitude", "vehicle_change_altitude", "operator_change_altitude"],
            OperatorCommandKind.SetHeading => ["set_heading", "heading", "yaw", "turn", "vehicle_set_heading", "operator_set_heading"],
            OperatorCommandKind.Land => ["land", "vehicle_land", "operator_land"],
            OperatorCommandKind.Recover => ["recover", "return", "return_home", "rtl", "vehicle_recover"],
            _ => []
        };

    private static bool IsCameraCommand(OperatorCommandKind command)
        => command is OperatorCommandKind.CapturePhoto or OperatorCommandKind.StartVideo or
            OperatorCommandKind.StopVideo or OperatorCommandKind.CenterGimbal or
            OperatorCommandKind.NadirGimbal or OperatorCommandKind.SetGimbal;

    private static string Normalize(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static OperatorPreflightFinding Warn(string code, string message)
        => new(code, OperatorPreflightSeverity.Warning, message);

    private static OperatorPreflightFinding Block(string code, string message)
        => new(code, OperatorPreflightSeverity.Blocking, message);
}
