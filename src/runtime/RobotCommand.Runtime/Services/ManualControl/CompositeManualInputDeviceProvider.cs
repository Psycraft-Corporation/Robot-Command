using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

/// <summary>Combines XInput-style gamepads and generic Windows raw joysticks into one safe input catalogue.</summary>
public sealed class CompositeManualInputDeviceProvider : IManualInputDeviceProvider
{
    private readonly IReadOnlyList<IManualInputDeviceProvider> _providers;

    public CompositeManualInputDeviceProvider(
        WindowsGamepadInputDeviceProvider gamepads,
        WindowsRawJoystickInputDeviceProvider joysticks)
    {
        _providers = [gamepads, joysticks];
        foreach (var provider in _providers) provider.DevicesChanged += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? DevicesChanged;
    public IReadOnlyList<ManualInputDevice> Devices => _providers.SelectMany(item => item.Devices).ToArray();

    public bool TryGetReading(string deviceId, ManualJoystickMapping? joystickMapping, out ManualInputReading reading)
    {
        foreach (var provider in _providers)
            if (provider.TryGetReading(deviceId, joystickMapping, out reading)) return true;
        reading = default!;
        return false;
    }
}
