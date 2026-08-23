namespace RobotCommand.Services.Serial;

public sealed record SikRadioPairingResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<string> Details,
    SikRadioProbeResult? SourceProbe = null,
    SikRadioProbeResult? TargetProbe = null)
{
    public string DisplayText => string.Join(Environment.NewLine, new[] { $"{Code}: {Message}" }.Concat(Details));
}

public interface ISikRadioPairingService
{
    Task<SikRadioPairingResult> PairAsync(
        SerialDeviceDescriptor source,
        SerialDeviceDescriptor target,
        int baudRate = 57600,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies only over-air pairing settings from a directly attached source SiK radio
/// to a directly attached target radio. UART speed, output power and local host
/// settings deliberately remain under manual control.
/// </summary>
public sealed class SikRadioPairingService(ISikRadioConfigurationService radios) : ISikRadioPairingService
{
    private static readonly int[] PairCriticalSettingIds = [2, 3, 5, 6, 8, 9, 10, 13];

    public async Task<SikRadioPairingResult> PairAsync(
        SerialDeviceDescriptor source,
        SerialDeviceDescriptor target,
        int baudRate = 57600,
        CancellationToken cancellationToken = default)
    {
        if (SameRadio(source, target))
        {
            return Failure("SAME_RADIO", "Choose two different directly attached SiK radios.");
        }

        SikRadioProbeResult? sourceProbe = null;
        SikRadioProbeResult? targetProbe = null;
        var activePort = source.PortName;
        try
        {
            sourceProbe = await radios.ProbeAsync(source, baudRate, cancellationToken);
            if (!sourceProbe.Local.Available)
            {
                return Failure("SOURCE_PROBE_FAILED", "The source radio did not return a local SiK configuration.", sourceProbe.Local.Error);
            }

            var missing = PairCriticalSettingIds.Where(id =>
                !sourceProbe.Local.Settings.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                return Failure(
                    "PAIRING_SETTINGS_UNAVAILABLE",
                    "The source radio did not expose every setting needed for safe pairing.",
                    $"Missing: {string.Join(", ", missing.Select(id => $"S{id}"))}.",
                    sourceProbe);
            }

            // Pairing changes only the over-air settings. In particular, S0 is the
            // radio's EEPROM-format/version field and is read-only on RFD SiK; staging
            // a complete settings snapshot tries ATS0=... and aborts the operation.
            // UART speed, power, and all other local-only settings remain untouched.
            var desiredTarget = PairCriticalSettingIds.ToDictionary(
                id => id,
                id => sourceProbe.Local.Settings[id]);
            /* var changed = PairCriticalSettingIds
                .Where(id => targetProbe.Local.Settings[id] != desiredTarget[id])
                .Select(id => $"S{id}: {targetProbe.Local.Settings[id]} → {desiredTarget[id]}")
                .ToArray(); */

            // Keep one uninterrupted target session: ApplyAsync starts by reading
            // the target, then stages only required values and verifies after reboot.
            var changed = Array.Empty<string>();
            activePort = target.PortName;

            if (changed.Length > 0)
            {
                return new SikRadioPairingResult(
                    true,
                    "ALREADY_PAIRED",
                    "The selected radios already have matching air-link settings.",
                    ["No radio settings were changed."],
                    sourceProbe,
                    targetProbe);
            }

            var verifiedTarget = await radios.ApplyAsync(
                new SikRadioApplyRequest(target, baudRate, desiredTarget), cancellationToken);
            if (!verifiedTarget.Local.Available || PairCriticalSettingIds.Any(id =>
                    !verifiedTarget.Local.Settings.TryGetValue(id, out var value) ||
                    value != sourceProbe.Local.Settings[id]))
            {
                return Failure(
                    "PAIRING_VERIFICATION_FAILED",
                    "The target radio rebooted, but its pairing settings could not be verified.",
                    "Reconnect both radios and probe them before attempting MAVLink.",
                    sourceProbe,
                    verifiedTarget);
            }

            return new SikRadioPairingResult(
                true,
                "PAIRED",
                "The target radio now matches the source radio's air-link settings.",
                [
                    $"Source: {source.PortName}.",
                    $"Target: {target.PortName}.",
                    $"Verified air-link settings: {string.Join(", ", desiredTarget.OrderBy(item => item.Key).Select(item => $"S{item.Key}={item.Value}"))}.",
                    "UART speed and transmit power were not changed."
                ],
                sourceProbe,
                verifiedTarget);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("CANCELLED", "Radio pairing was cancelled.", sourceProbe: sourceProbe, targetProbe: targetProbe);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(
                "PORT_ACCESS_DENIED",
                $"{activePort} is already open in another application.",
                "Robot Command releases its own MAVLink connections before pairing. Close QGroundControl, any other Robot Command instance, terminal/serial monitor, then retry.",
                sourceProbe,
                targetProbe);
        }
        catch (IOException ex) when (ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) &&
                                    ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "PORT_ACCESS_DENIED",
                $"{activePort} is already open in another application.",
                "Robot Command releases its own MAVLink connections before pairing. Close QGroundControl, any other Robot Command instance, terminal/serial monitor, then retry.",
                sourceProbe,
                targetProbe);
        }
        catch (Exception ex)
        {
            return Failure(
                "PAIRING_FAILED",
                "The radios were not changed or could not be fully verified.",
                ex.Message,
                sourceProbe,
                targetProbe);
        }
    }

    private static SikRadioPairingResult Failure(
        string code,
        string message,
        string? detail = null,
        SikRadioProbeResult? sourceProbe = null,
        SikRadioProbeResult? targetProbe = null)
        => new(false, code, message, string.IsNullOrWhiteSpace(detail) ? [] : [detail], sourceProbe, targetProbe);

    private static bool SameRadio(SerialDeviceDescriptor source, SerialDeviceDescriptor target)
        => (!string.IsNullOrWhiteSpace(source.DeviceId) &&
            source.DeviceId.Equals(target.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
           source.PortName.Equals(target.PortName, StringComparison.OrdinalIgnoreCase);
}
