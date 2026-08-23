using RobotCommand.Models;

namespace RobotCommand.Services.ManualControl;

public interface IManualControlService
{
    IReadOnlyList<ManualInputDevice> Devices { get; }
    ManualControlProfile Profile { get; }
    IReadOnlyList<ManualControlProfileRecord> Profiles { get; }
    string ActiveProfileId { get; }
    ManualControlSessionSnapshot Snapshot { get; }
    ManualInputReading? LatestReading { get; }
    string? SelectedDeviceId { get; }
    event EventHandler? Changed;
    Task SetSelectedDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(ManualControlProfile profile, CancellationToken cancellationToken = default);
    Task<ManualControlProfileRecord> CreateProfileAsync(string name, ManualControlProfile? profile = null, CancellationToken cancellationToken = default);
    Task<ManualControlProfileRecord> SelectProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<ManualControlProfileRecord> UpdateProfileAsync(string profileId, string? name, ManualControlProfile? profile, CancellationToken cancellationToken = default);
    Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default);
    Task<bool> TakeControlAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string reason = "Operator released manual control.", CancellationToken cancellationToken = default);
    void OnApplicationFocusChanged(bool focused);
}
