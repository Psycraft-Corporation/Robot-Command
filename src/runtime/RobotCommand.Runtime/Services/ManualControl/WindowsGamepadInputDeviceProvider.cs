using Microsoft.Extensions.Hosting;
using RobotCommand.Models;
using Windows.Gaming.Input;

namespace RobotCommand.Services.ManualControl;

/// <summary>Windows Gaming Input adapter. It owns device discovery only; polling stays in the control service.</summary>
public sealed class WindowsGamepadInputDeviceProvider : IManualInputDeviceProvider, IHostedService
{
    private readonly object _gate = new();
    private readonly Dictionary<Gamepad, string> _ids = [];
    private long _nextId;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<ManualInputDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                SynchronizeDevices();
                return _ids.Select(pair => new ManualInputDevice(pair.Value, $"Xbox controller {pair.Value[5..]}", true)).ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Gamepad.GamepadAdded += OnGamepadChanged;
        Gamepad.GamepadRemoved += OnGamepadChanged;
        lock (_gate) SynchronizeDevices();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Gamepad.GamepadAdded -= OnGamepadChanged;
        Gamepad.GamepadRemoved -= OnGamepadChanged;
        lock (_gate) _ids.Clear();
        return Task.CompletedTask;
    }

    public bool TryGetReading(string deviceId, ManualJoystickMapping? joystickMapping, out ManualInputReading reading)
    {
        lock (_gate)
        {
            SynchronizeDevices();
            var pair = _ids.FirstOrDefault(item => string.Equals(item.Value, deviceId, StringComparison.Ordinal));
            if (pair.Key is null)
            {
                reading = default!;
                return false;
            }

            var input = pair.Key.GetCurrentReading();
            var buttons = input.Buttons;
            reading = new ManualInputReading(
                deviceId, input.LeftThumbstickX, input.LeftThumbstickY, input.RightThumbstickX, input.RightThumbstickY,
                input.LeftTrigger, input.RightTrigger,
                buttons.HasFlag(GamepadButtons.LeftShoulder),
                buttons.HasFlag(GamepadButtons.RightShoulder),
                buttons.HasFlag(GamepadButtons.A), buttons.HasFlag(GamepadButtons.B), buttons.HasFlag(GamepadButtons.X),
                buttons.HasFlag(GamepadButtons.Y), buttons.HasFlag(GamepadButtons.Menu), DateTimeOffset.UtcNow);
            return true;
        }
    }

    private void OnGamepadChanged(object? sender, Gamepad e)
    {
        lock (_gate) SynchronizeDevices();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SynchronizeDevices()
    {
        var current = Gamepad.Gamepads.ToHashSet();
        foreach (var gamepad in current)
            if (!_ids.ContainsKey(gamepad)) _ids.Add(gamepad, $"xbox-{Interlocked.Increment(ref _nextId)}");
        foreach (var missing in _ids.Keys.Where(item => !current.Contains(item)).ToArray()) _ids.Remove(missing);
    }
}
