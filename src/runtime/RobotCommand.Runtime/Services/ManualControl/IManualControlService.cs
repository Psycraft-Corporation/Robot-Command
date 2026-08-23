using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

public interface IManualControlService
{
    IReadOnlyList<ManualInputDevice> Devices { get; }
    ManualControlProfile Profile { get; }
    ManualControlSessionSnapshot Snapshot { get; }
    ManualInputReading? LatestReading { get; }
    string? SelectedDeviceId { get; }
    event EventHandler? Changed;
    Task SetSelectedDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(ManualControlProfile profile, CancellationToken cancellationToken = default);
    Task<bool> TakeControlAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string reason = "Operator released manual control.", CancellationToken cancellationToken = default);
    void OnApplicationFocusChanged(bool focused);
}
