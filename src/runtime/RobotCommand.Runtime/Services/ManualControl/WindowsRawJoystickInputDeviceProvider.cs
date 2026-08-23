using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using RobotCommand.Models;
using Windows.Gaming.Input;

namespace RobotCommand.Services.ManualControl;

/// <summary>
/// Windows RawGameController adapter for DirectInput/HID controls such as the
/// It intentionally reports raw axis/button capacity so
/// the selected-device mapping can be corrected without adding a device driver.
/// </summary>
public sealed class WindowsRawJoystickInputDeviceProvider : IManualInputDeviceProvider, IHostedService
{
    private const ushort MicrosoftVendorId = 0x045E;
    private readonly object _gate = new();
    private readonly Dictionary<RawGameController, string> _ids = [];

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<ManualInputDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                SynchronizeDevices();
                return _ids.Select(item => Describe(item.Key, item.Value)).ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RawGameController.RawGameControllerAdded += OnControllerChanged;
        RawGameController.RawGameControllerRemoved += OnControllerChanged;
        lock (_gate) SynchronizeDevices();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        RawGameController.RawGameControllerAdded -= OnControllerChanged;
        RawGameController.RawGameControllerRemoved -= OnControllerChanged;
        lock (_gate) _ids.Clear();
        return Task.CompletedTask;
    }

    public bool TryGetReading(string deviceId, ManualJoystickMapping? joystickMapping, out ManualInputReading reading)
    {
        lock (_gate)
        {
            SynchronizeDevices();
            var controller = _ids.FirstOrDefault(item => string.Equals(item.Value, deviceId, StringComparison.Ordinal)).Key;
            if (controller is null)
            {
                reading = default!;
                return false;
            }

            var axes = new double[controller.AxisCount];
            var buttons = new bool[controller.ButtonCount];
            var switches = new GameControllerSwitchPosition[controller.SwitchCount];
            controller.GetCurrentReading(buttons, switches, axes);
            var mapping = (joystickMapping ?? new ManualJoystickMapping()).Normalize();
            reading = new ManualInputReading(
                deviceId,
                Axis(axes, mapping.YawAxis, mapping.YawInverted),
                Axis(axes, mapping.VerticalAxis, mapping.VerticalInverted),
                Axis(axes, mapping.RightAxis, mapping.RightInverted),
                Axis(axes, mapping.ForwardAxis, mapping.ForwardInverted),
                0,
                0,
                Button(buttons, mapping.DeadmanButton),
                Button(buttons, mapping.ArmButton),
                false,
                Button(buttons, mapping.CancelButton),
                Button(buttons, mapping.ExecuteButton),
                Button(buttons, mapping.TakeoffButton),
                Button(buttons, mapping.ReleaseButton),
                DateTimeOffset.UtcNow,
                axes,
                buttons);
            return true;
        }
    }

    private void OnControllerChanged(object? sender, RawGameController controller)
    {
        lock (_gate) SynchronizeDevices();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SynchronizeDevices()
    {
        var current = RawGameController.RawGameControllers
            .Where(controller => controller.HardwareVendorId != MicrosoftVendorId)
            .ToHashSet();
        foreach (var controller in current)
            if (!_ids.ContainsKey(controller)) _ids.Add(controller, BuildId(controller));
        foreach (var missing in _ids.Keys.Where(item => !current.Contains(item)).ToArray()) _ids.Remove(missing);
    }

    private static ManualInputDevice Describe(RawGameController controller, string id)
    {
        return new ManualInputDevice(id, "Joystick", true, ManualInputDeviceKind.Joystick, controller.AxisCount, controller.ButtonCount);
    }

    private static string BuildId(RawGameController controller)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(controller.NonRoamableId);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        return $"joystick-{controller.HardwareVendorId:X4}-{controller.HardwareProductId:X4}-{hash}";
    }

    private static bool Button(IReadOnlyList<bool> buttons, int index) => index >= 0 && index < buttons.Count && buttons[index];

    private static double Axis(IReadOnlyList<double> axes, int index, bool invert)
    {
        if (index < 0 || index >= axes.Count) return 0;
        // RawGameController axes are normally 0..1. Some HID drivers already
        // expose -1..1, so accept both representations.
        var raw = axes[index];
        var normalized = raw is >= 0 and <= 1 ? (raw * 2) - 1 : Math.Clamp(raw, -1, 1);
        return invert ? -normalized : normalized;
    }
}
