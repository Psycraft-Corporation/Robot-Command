using RobotCommand.Core;

namespace RobotCommand.Services.Mavlink;

public sealed record MavlinkMissionItem(
    ushort Sequence,
    ushort Command,
    byte Frame,
    int LatitudeE7,
    int LongitudeE7,
    float AltitudeMetres,
    float Param1 = 0,
    float Param2 = 0,
    float Param3 = 0,
    float Param4 = float.NaN,
    bool Current = false,
    byte MissionType = 0,
    // MAVLink mission commands use x/y/z as command parameters 5/6/7 when
    // they are not geographic commands. Keep the geographic fields above for
    // existing callers and expose explicit raw values for camera/gimbal items.
    int? RawX = null,
    int? RawY = null,
    float? RawZ = null);

public sealed record MavlinkMissionTransferResult(bool Succeeded, string Summary, IReadOnlyList<MavlinkMissionItem> Items, int? CurrentItemIndex = null);

/// <summary>Current onboard mission state reported by a MAVLink vehicle.</summary>
public sealed record MavlinkMissionState(
    int? CurrentItemIndex,
    int? LastReachedItemIndex,
    DateTimeOffset UpdatedAt,
    bool IsArmed,
    string LandedState,
    string Mode,
    byte? MissionState = null);

public interface IMavlinkMissionClient
{
    Task<MavlinkMissionTransferResult> UploadMissionAsync(string vehicleId, IReadOnlyList<MavlinkMissionItem> items, CancellationToken cancellationToken = default);
    Task<MavlinkMissionTransferResult> DownloadMissionAsync(string vehicleId, CancellationToken cancellationToken = default);
    /// <param name="restartFromBeginning">When starting mission mode, reset the current item to zero. Resume must pass false.</param>
    /// <param name="sendMissionStartCommand">For ArduPilot, send MAV_CMD_MISSION_START after AUTO confirmation.</param>
    Task<MavlinkMissionTransferResult> SetMissionModeAsync(string vehicleId, bool paused, bool restartFromBeginning = true, bool sendMissionStartCommand = false, CancellationToken cancellationToken = default);
    Task<MavlinkMissionTransferResult> SetReturnToLaunchAsync(string vehicleId, CancellationToken cancellationToken = default);
    bool TryGetMissionProgress(string vehicleId, out int? currentItemIndex, out DateTimeOffset updatedAt);
    bool TryGetMissionState(string vehicleId, out MavlinkMissionState state);
    Task<MavlinkMissionTransferResult> SetMissionCurrentAsync(string vehicleId, ushort missionItemIndex, CancellationToken cancellationToken = default);
    Task<MavlinkMissionTransferResult> ClearMissionAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<MavlinkMissionTransferResult> UploadFenceAsync(string vehicleId, IReadOnlyList<MavlinkMissionItem> items, CancellationToken cancellationToken = default);
    Task<MavlinkMissionTransferResult> DownloadFenceAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<MavlinkMissionTransferResult> ClearFenceAsync(string vehicleId, CancellationToken cancellationToken = default);
}
