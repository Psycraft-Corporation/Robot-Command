namespace RobotCommand.Services.Serial;

/// <summary>
/// Session-only cache of radio probes. Radio configuration is authoritative only
/// when freshly read from a radio; this cache exists solely to avoid displaying
/// one radio's values while another connection is selected.
/// </summary>
public sealed class SikRadioProbeCache
{
    private readonly Dictionary<string, SikRadioProbeResult> _byDeviceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SikRadioProbeResult> _byPort = new(StringComparer.OrdinalIgnoreCase);

    public void Store(SikRadioProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var device = probe.Device;
        if (!string.IsNullOrWhiteSpace(device.DeviceId))
        {
            _byDeviceId[device.DeviceId] = probe;
        }

        if (!string.IsNullOrWhiteSpace(device.PortName))
        {
            _byPort[device.PortName] = probe;
        }
    }

    public bool TryGet(string? deviceId, string? portName, out SikRadioProbeResult probe)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            // A stable device identity is authoritative. Do not show a probe for
            // a different device that happens to have inherited this COM number.
            return _byDeviceId.TryGetValue(deviceId, out probe!);
        }

        if (!string.IsNullOrWhiteSpace(portName))
        {
            return _byPort.TryGetValue(portName, out probe!);
        }

        probe = null!;
        return false;
    }
}
