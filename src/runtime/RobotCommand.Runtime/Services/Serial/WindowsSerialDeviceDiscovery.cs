using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace RobotCommand.Services.Serial;

public sealed partial class WindowsSerialDeviceDiscovery : ISerialDeviceDiscovery
{
    private const string PortsDeviceSelector =
        "System.Devices.ClassGuid:=\"{4d36e978-e325-11ce-bfc1-08002be10318}\"";

    public async Task<IReadOnlyList<SerialDeviceDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var discovered = new List<SerialDeviceDescriptor>();
        try
        {
            var operation = DeviceInformation.FindAllAsync(PortsDeviceSelector);
            var devices = await operation.AsTask(cancellationToken);
            foreach (var device in devices)
            {
                var port = PortRegex().Match(device.Name).Groups[1].Value;
                if (string.IsNullOrWhiteSpace(port))
                {
                    port = PortRegex().Match(device.Id).Groups[1].Value;
                }
                if (string.IsNullOrWhiteSpace(port)) continue;

                var instanceId = device.Id;
                var hardware = $"{instanceId} {device.Id}";
                var vid = VidRegex().Match(hardware).Groups[1].Value;
                var pid = PidRegex().Match(hardware).Groups[1].Value;
                var serial = FtdiSerialRegex().Match(hardware).Groups[1].Value;
                string? manufacturer = null;
                Guid? containerId = null;
                var likelySik =
                    (vid.Equals("0403", StringComparison.OrdinalIgnoreCase) &&
                     pid.Equals("6015", StringComparison.OrdinalIgnoreCase)) ||
                    device.Name.Contains("SiK", StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase);

                discovered.Add(new SerialDeviceDescriptor(
                    instanceId,
                    port.ToUpperInvariant(),
                    device.Name,
                    manufacturer,
                    NullIfEmpty(vid),
                    NullIfEmpty(pid),
                    NullIfEmpty(serial),
                    containerId,
                    likelySik));
            }
        }
        catch (Exception) when (!OperatingSystem.IsWindows()) { }

        var presentPorts = SerialPort.GetPortNames()
            .Select(port => port.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnrichFromPnpRegistry(discovered, presentPorts);

        foreach (var port in presentPorts)
        {
            if (discovered.Any(item => item.PortName.Equals(port, StringComparison.OrdinalIgnoreCase))) continue;
            discovered.Add(new SerialDeviceDescriptor(
                $"serial-port:{port.ToUpperInvariant()}",
                port.ToUpperInvariant(),
                $"Serial port ({port.ToUpperInvariant()})"));
        }

        return discovered.OrderBy(item => PortNumber(item.PortName)).ThenBy(item => item.PortName).ToArray();
    }

    private static void EnrichFromPnpRegistry(
        List<SerialDeviceDescriptor> discovered,
        IReadOnlySet<string> presentPorts)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (root is null) return;
            foreach (var enumeratorName in root.GetSubKeyNames())
            {
                using var enumerator = root.OpenSubKey(enumeratorName);
                if (enumerator is null) continue;
                foreach (var deviceName in enumerator.GetSubKeyNames())
                {
                    using var device = enumerator.OpenSubKey(deviceName);
                    if (device is null) continue;
                    foreach (var instanceName in device.GetSubKeyNames())
                    {
                        using var instance = device.OpenSubKey(instanceName);
                        using var parameters = instance?.OpenSubKey("Device Parameters");
                        var port = parameters?.GetValue("PortName")?.ToString()?.ToUpperInvariant();
                        if (string.IsNullOrWhiteSpace(port) || !presentPorts.Contains(port)) continue;

                        var instanceId = $@"{enumeratorName}\{deviceName}\{instanceName}";
                        var hardware = instanceId;
                        var vid = VidRegex().Match(hardware).Groups[1].Value;
                        var pid = PidRegex().Match(hardware).Groups[1].Value;
                        var serial = ExtractUsbSerial(deviceName, instanceName);
                        var friendlyName = instance?.GetValue("FriendlyName")?.ToString()
                            ?? instance?.GetValue("DeviceDesc")?.ToString()?.Split(';').LastOrDefault()
                            ?? $"Serial port ({port})";
                        var manufacturer = instance?.GetValue("Mfg")?.ToString()?.Split(';').LastOrDefault();
                        var container = Guid.TryParse(instance?.GetValue("ContainerID")?.ToString(), out var parsed)
                            ? parsed
                            : (Guid?)null;
                        var likelySik = IsLikelySik(vid, pid, friendlyName);
                        var descriptor = new SerialDeviceDescriptor(
                            instanceId,
                            port,
                            friendlyName,
                            manufacturer,
                            NullIfEmpty(vid),
                            NullIfEmpty(pid),
                            NullIfEmpty(serial),
                            container,
                            likelySik);
                        var existingIndex = discovered.FindIndex(item =>
                            item.PortName.Equals(port, StringComparison.OrdinalIgnoreCase));
                        if (existingIndex >= 0) discovered[existingIndex] = descriptor;
                        else discovered.Add(descriptor);
                    }
                }
            }
        }
        catch (Exception)
        {
            // A restricted registry view still leaves the plain COM-port fallback.
        }
    }

    private static string? ExtractUsbSerial(string deviceName, string instanceName)
    {
        var ftdi = FtdiDeviceSerialRegex().Match(deviceName).Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(ftdi)) return ftdi;
        return instanceName.Contains('&') ? null : instanceName;
    }

    private static bool IsLikelySik(string vid, string pid, string name)
        => (vid.Equals("0403", StringComparison.OrdinalIgnoreCase) &&
            pid.Equals("6015", StringComparison.OrdinalIgnoreCase)) ||
           name.Contains("SiK", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase);

    public async Task<SerialDeviceDescriptor?> ResolveAsync(
        string? stableDeviceId,
        string? lastKnownPort,
        CancellationToken cancellationToken = default)
    {
        var devices = await DiscoverAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(stableDeviceId))
        {
            var exact = devices.FirstOrDefault(item =>
                item.DeviceId.Equals(stableDeviceId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var serial = FtdiSerialRegex().Match(stableDeviceId).Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(serial))
            {
                var bySerial = devices.FirstOrDefault(item =>
                    item.SerialNumber?.Equals(serial, StringComparison.OrdinalIgnoreCase) == true);
                if (bySerial is not null) return bySerial;
            }
        }
        return string.IsNullOrWhiteSpace(lastKnownPort)
            ? null
            : devices.FirstOrDefault(item => item.PortName.Equals(lastKnownPort, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int PortNumber(string port) => int.TryParse(PortRegex().Match(port).Groups[2].Value, out var value) ? value : int.MaxValue;

    [GeneratedRegex(@"\b((?:COM)(\d+))\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortRegex();
    [GeneratedRegex(@"VID[_+&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidRegex();
    [GeneratedRegex(@"PID[_+&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PidRegex();
    [GeneratedRegex(@"VID_[0-9A-F]{4}\+PID_[0-9A-F]{4}\+([A-Z0-9]+?)(?:A)?(?:\\|#)", RegexOptions.IgnoreCase)]
    private static partial Regex FtdiSerialRegex();
    [GeneratedRegex(@"VID_[0-9A-F]{4}\+PID_[0-9A-F]{4}\+([A-Z0-9]+?)(?:A)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FtdiDeviceSerialRegex();
}
