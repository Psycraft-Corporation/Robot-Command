using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

public interface IManualInputDeviceProvider
{
    IReadOnlyList<ManualInputDevice> Devices { get; }
    event EventHandler? DevicesChanged;
    bool TryGetReading(string deviceId, ManualJoystickMapping? joystickMapping, out ManualInputReading reading);
}
