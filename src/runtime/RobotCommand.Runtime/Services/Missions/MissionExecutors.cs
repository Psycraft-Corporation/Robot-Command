using RobotCommand.Core;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Simulation;

namespace RobotCommand.Services.Missions;

/// <summary>Backend-neutral mission execution boundary. Runtime owns protocol details.</summary>
public interface IFlightMissionExecutor
{
    string ExecutorKind { get; }
    bool Supports(UnitObservationSnapshot target);
    Task<FlightMissionExecutorResult> UploadAsync(
        string connectionId,
        string vehicleId,
        FlightMissionExecutionArtifact artifact,
        IReadOnlyList<MavlinkMissionItem>? wireItems,
        CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> StartAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> PauseAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> ResumeAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> PrepareResumeAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, int resumeItemIndex, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> RemoveAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> CompleteAsync(string connectionId, string vehicleId, FlightMissionEndAction endAction = FlightMissionEndAction.Hold, CancellationToken cancellationToken = default);
    bool TryGetProgress(string connectionId, string vehicleId, out FlightMissionExecutorProgress progress);
}

public sealed class Px4MissionExecutor(IMavlinkConnectionRegistry connections) : IFlightMissionExecutor
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Connection, string Vehicle), UploadedMission> _uploadedMissions = new();
    public string ExecutorKind => "PX4 MAVLink";
    public bool Supports(UnitObservationSnapshot target) =>
        !target.IsGhost && target.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase) &&
        target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase);

    public async Task<FlightMissionExecutorResult> UploadAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, CancellationToken cancellationToken = default)
    {
        if (wireItems is null) return new(false, "PX4 mission compilation produced no MAVLink items.", "PX4_ITEMS_MISSING");
        var client = Client(connectionId);
        var result = await client.UploadMissionAsync(vehicleId, wireItems, cancellationToken);
        if (result.Succeeded) _uploadedMissions[(connectionId, vehicleId)] = new(wireItems);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "PX4_UPLOAD_FAILED");
    }
    public async Task<FlightMissionExecutorResult> StartAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => await SetModeAsync(connectionId, vehicleId, false, true, cancellationToken);
    public async Task<FlightMissionExecutorResult> PauseAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => await SetModeAsync(connectionId, vehicleId, true, false, cancellationToken);
    public async Task<FlightMissionExecutorResult> ResumeAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => await SetModeAsync(connectionId, vehicleId, false, false, cancellationToken);
    public async Task<FlightMissionExecutorResult> PrepareResumeAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, int resumeItemIndex, CancellationToken cancellationToken = default)
    {
        if (wireItems is null) return new(false, "PX4 mission compilation produced no MAVLink items.", "PX4_ITEMS_MISSING");
        var upload = await UploadAsync(connectionId, vehicleId, artifact, wireItems, cancellationToken);
        if (!upload.Succeeded) return upload;
        var result = await Client(connectionId).SetMissionCurrentAsync(vehicleId, (ushort)Math.Clamp(resumeItemIndex, 0, wireItems.Count - 1), cancellationToken);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "PX4_RESUME_POSITION_REJECTED");
    }
    public async Task<FlightMissionExecutorResult> RemoveAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var result = await Client(connectionId).ClearMissionAsync(vehicleId, cancellationToken);
        if (result.Succeeded) _uploadedMissions.TryRemove((connectionId, vehicleId), out _);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "PX4_MISSION_REMOVE_FAILED");
    }
    public async Task<FlightMissionExecutorResult> CompleteAsync(string connectionId, string vehicleId, FlightMissionEndAction endAction = FlightMissionEndAction.Hold, CancellationToken cancellationToken = default)
    {
        if (endAction == FlightMissionEndAction.ReturnToLaunch)
        {
            var result = await Client(connectionId).SetReturnToLaunchAsync(vehicleId, cancellationToken);
            return new(result.Succeeded, result.Summary, result.Succeeded ? null : "PX4_RTL_AFTER_MISSION_FAILED");
        }

        return await SetModeAsync(connectionId, vehicleId, true, false, cancellationToken);
    }
    public bool TryGetProgress(string connectionId, string vehicleId, out FlightMissionExecutorProgress progress)
    {
        progress = default!;
        if (!connections.TryGet(connectionId, out var connection) || connection is not IMavlinkMissionClient client ||
            !client.TryGetMissionState(vehicleId, out var missionState)) return false;
        var uploaded = _uploadedMissions.TryGetValue((connectionId, vehicleId), out var metadata) ? metadata : new([]);
        var itemCount = uploaded.Items.Count;
        var index = missionState.CurrentItemIndex ?? missionState.LastReachedItemIndex;
        var finalItemReached = missionState.LastReachedItemIndex is { } reached && itemCount > 0 && reached >= itemCount - 1;
        var finalLandingObserved = !missionState.IsArmed && string.Equals(missionState.LandedState, "Landed", StringComparison.OrdinalIgnoreCase);
        var finalEndFinished = !uploaded.FinalItemIsLand && !uploaded.FinalItemIsRtl ||
            ((uploaded.FinalItemIsLand || uploaded.FinalItemIsRtl) && finalLandingObserved);
        var state = finalItemReached && finalEndFinished
            ? FlightMissionExecutionState.Completed
            : FlightMissionExecutionState.Running;
        var postLanding = state == FlightMissionExecutionState.Completed && uploaded.FinalItemIsLand && finalEndFinished;
        progress = new(state, index, itemCount, state == FlightMissionExecutionState.Completed ? "PX4 mission completed; vehicle is being held safely." : "PX4 mission progress", UpdatedStep(index), PostLandingDecisionAvailable: postLanding, ResumeItemIndex: postLanding ? FindResumeIndex(uploaded.Items) : null);
        return true;
        string? UpdatedStep(int? _) => null;
    }
    private static int FindResumeIndex(IReadOnlyList<MavlinkMissionItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
            if (items[index].Command is not MavlinkCommandIds.NavLand and not MavlinkCommandIds.NavReturnToLaunch)
                return index;
        return 0;
    }
    private async Task<FlightMissionExecutorResult> SetModeAsync(string connectionId, string vehicleId, bool paused, bool restartFromBeginning, CancellationToken token)
    {
        var result = await Client(connectionId).SetMissionModeAsync(vehicleId, paused, restartFromBeginning, cancellationToken: token);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "PX4_MODE_REJECTED");
    }
    private IMavlinkMissionClient Client(string connectionId)
        => connections.TryGet(connectionId, out var connection) && connection is IMavlinkMissionClient client
            ? client
            : throw new InvalidOperationException("The selected PX4 MAVLink connection is unavailable.");

    private sealed record UploadedMission(IReadOnlyList<MavlinkMissionItem> Items)
    {
        public bool FinalItemIsLand => Items.Count > 0 && Items[^1].Command == MavlinkCommandIds.NavLand;
        public bool FinalItemIsRtl => Items.Count > 0 && Items[^1].Command == MavlinkCommandIds.NavReturnToLaunch;
    }
}

public sealed class GhostMissionExecutor(IGhostUnitService ghosts, IFormationLockWorkflow formation) : IFlightMissionExecutor
{
    public string ExecutorKind => "Ghost simulator";
    public bool Supports(UnitObservationSnapshot target) => target.IsGhost && target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase);
    public Task<FlightMissionExecutorResult> UploadAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, CancellationToken cancellationToken = default)
        => ghosts.PrepareMissionAsync(vehicleId, artifact, cancellationToken);
    public async Task<FlightMissionExecutorResult> StartAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        await formation.HandleIndependentOperationAsync(vehicleId, OperatorWorkflowCommandKind.Hold, cancellationToken);
        return await ghosts.StartMissionAsync(vehicleId, cancellationToken);
    }
    public Task<FlightMissionExecutorResult> PauseAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => ghosts.PauseMissionAsync(vehicleId, cancellationToken);
    public Task<FlightMissionExecutorResult> ResumeAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => ghosts.ResumeMissionAsync(vehicleId, cancellationToken);
    public Task<FlightMissionExecutorResult> PrepareResumeAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, int resumeItemIndex, CancellationToken cancellationToken = default)
        => ghosts.PrepareMissionForResumeAsync(vehicleId, artifact, resumeItemIndex, cancellationToken);
    public Task<FlightMissionExecutorResult> RemoveAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => ghosts.ClearMissionAsync(vehicleId, cancellationToken);
    public Task<FlightMissionExecutorResult> CompleteAsync(string connectionId, string vehicleId, FlightMissionEndAction endAction = FlightMissionEndAction.Hold, CancellationToken cancellationToken = default)
        => Task.FromResult(new FlightMissionExecutorResult(true, "Ghost mission completed; vehicle is holding safely."));
    public bool TryGetProgress(string connectionId, string vehicleId, out FlightMissionExecutorProgress progress)
        => ghosts.TryGetMissionProgress(vehicleId, out progress);
}

public sealed class ArduPilotMissionExecutor(IMavlinkConnectionRegistry connections) : IFlightMissionExecutor
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Connection, string Vehicle), UploadedMission> _uploadedMissions = new();

    public string ExecutorKind => "ArduPilot MAVLink";

    public bool Supports(UnitObservationSnapshot target)
        => !target.IsGhost && target.ProfileKey.Contains("ardupilot", StringComparison.OrdinalIgnoreCase) &&
           target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase);

    public async Task<FlightMissionExecutorResult> UploadAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, CancellationToken cancellationToken = default)
    {
        if (wireItems is null)
            return new(false, "ArduPilot mission compilation produced no MAVLink items.", "ARDUPILOT_ITEMS_MISSING");
        var result = await Client(connectionId).UploadMissionAsync(vehicleId, wireItems, cancellationToken);
        if (result.Succeeded)
            _uploadedMissions[(connectionId, vehicleId)] = new(wireItems);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "ARDUPILOT_UPLOAD_FAILED", result.Items.Count);
    }

    public async Task<FlightMissionExecutorResult> StartAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        if (Client(connectionId).TryGetMissionState(vehicleId, out var state) && !state.IsArmed)
            return new(false, "Arm the ArduPilot vehicle before starting the mission.", "ARDUPILOT_NOT_ARMED");
        return await SetModeAsync(connectionId, vehicleId, paused: false, restartFromBeginning: true, sendMissionStartCommand: true, cancellationToken);
    }

    public Task<FlightMissionExecutorResult> PauseAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => SetModeAsync(connectionId, vehicleId, paused: true, restartFromBeginning: false, sendMissionStartCommand: false, cancellationToken);

    public Task<FlightMissionExecutorResult> ResumeAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
        => SetModeAsync(connectionId, vehicleId, paused: false, restartFromBeginning: false, sendMissionStartCommand: false, cancellationToken);

    public async Task<FlightMissionExecutorResult> PrepareResumeAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, int resumeItemIndex, CancellationToken cancellationToken = default)
    {
        if (wireItems is null)
            return new(false, "ArduPilot mission compilation produced no MAVLink items.", "ARDUPILOT_ITEMS_MISSING");
        var upload = await UploadAsync(connectionId, vehicleId, artifact, wireItems, cancellationToken);
        if (!upload.Succeeded) return upload;
        var result = await Client(connectionId).SetMissionCurrentAsync(vehicleId, (ushort)Math.Clamp(resumeItemIndex, 0, wireItems.Count - 1), cancellationToken);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "ARDUPILOT_RESUME_POSITION_REJECTED");
    }

    public async Task<FlightMissionExecutorResult> RemoveAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var result = await Client(connectionId).ClearMissionAsync(vehicleId, cancellationToken);
        if (result.Succeeded) _uploadedMissions.TryRemove((connectionId, vehicleId), out _);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "ARDUPILOT_MISSION_REMOVE_FAILED");
    }

    public async Task<FlightMissionExecutorResult> CompleteAsync(string connectionId, string vehicleId, FlightMissionEndAction endAction = FlightMissionEndAction.Hold, CancellationToken cancellationToken = default)
    {
        if (endAction == FlightMissionEndAction.ReturnToLaunch)
        {
            var result = await Client(connectionId).SetReturnToLaunchAsync(vehicleId, cancellationToken);
            return new(result.Succeeded, result.Summary, result.Succeeded ? null : "ARDUPILOT_RTL_AFTER_MISSION_FAILED");
        }
        return await PauseAsync(connectionId, vehicleId, cancellationToken);
    }

    public bool TryGetProgress(string connectionId, string vehicleId, out FlightMissionExecutorProgress progress)
    {
        progress = default!;
        if (!connections.TryGet(connectionId, out var connection) || connection is not IMavlinkMissionClient client ||
            !client.TryGetMissionState(vehicleId, out var state)) return false;

        var uploaded = _uploadedMissions.TryGetValue((connectionId, vehicleId), out var metadata) ? metadata : new([]);
        var itemCount = uploaded.Items.Count;
        var index = state.CurrentItemIndex ?? state.LastReachedItemIndex;
        var finalItemReached = state.LastReachedItemIndex is { } reached && itemCount > 0 && reached >= itemCount - 1;
        var finalLandingObserved = !state.IsArmed && string.Equals(state.LandedState, "Landed", StringComparison.OrdinalIgnoreCase);
        var finalEndFinished = !uploaded.FinalItemIsLand && !uploaded.FinalItemIsRtl ||
            ((uploaded.FinalItemIsLand || uploaded.FinalItemIsRtl) && finalLandingObserved);
        var completed = finalItemReached && finalEndFinished;

        if (completed)
        {
            var postLanding = uploaded.FinalItemIsLand;
            progress = new(FlightMissionExecutionState.Completed, index, itemCount,
                "ArduPilot mission completed; vehicle is holding safely.",
                PostLandingDecisionAvailable: postLanding,
                ResumeItemIndex: postLanding ? FindResumeIndex(uploaded.Items) : null);
            return true;
        }

        var mode = state.Mode ?? string.Empty;
        // Robot Command's frontend Hold maps to ArduCopter Brake for mission
        // pause. Treat that native mode as a real paused state so the
        // operator can continue from the preserved current item instead of
        // incorrectly turning a deliberate pause into an interruption.
        var isPaused = mode.Equals("Brake", StringComparison.OrdinalIgnoreCase);
        var modeAllowed = mode.Equals("AUTO", StringComparison.OrdinalIgnoreCase) ||
                          mode.Equals("Loiter", StringComparison.OrdinalIgnoreCase) ||
                          isPaused ||
                          (uploaded.FinalItemIsRtl && mode.Equals("RTL", StringComparison.OrdinalIgnoreCase));
        var executionState = !modeAllowed
            ? FlightMissionExecutionState.Interrupted
            : isPaused ? FlightMissionExecutionState.Paused : FlightMissionExecutionState.Running;
        var summary = executionState switch
        {
            FlightMissionExecutionState.Paused => "ArduPilot mission paused in Brake.",
            FlightMissionExecutionState.Running => $"ArduPilot mission progress ({mode}).",
            _ => $"ArduPilot mission interrupted because the vehicle is in {(string.IsNullOrWhiteSpace(mode) ? "an unknown mode" : mode)}."
        };
        progress = new(executionState, index, itemCount, summary);
        return true;

        static int FindResumeIndex(IReadOnlyList<MavlinkMissionItem> items)
        {
            for (var index = items.Count - 1; index >= 0; index--)
                if (items[index].Command is not MavlinkCommandIds.NavLand and not MavlinkCommandIds.NavReturnToLaunch)
                    return index;
            return 0;
        }
    }

    private async Task<FlightMissionExecutorResult> SetModeAsync(string connectionId, string vehicleId, bool paused, bool restartFromBeginning, bool sendMissionStartCommand, CancellationToken cancellationToken)
    {
        var result = await Client(connectionId).SetMissionModeAsync(vehicleId, paused, restartFromBeginning, sendMissionStartCommand, cancellationToken);
        return new(result.Succeeded, result.Summary, result.Succeeded ? null : "ARDUPILOT_MODE_REJECTED");
    }

    private IMavlinkMissionClient Client(string connectionId)
        => connections.TryGet(connectionId, out var connection) && connection is IMavlinkMissionClient client
            ? client
            : throw new InvalidOperationException("The selected ArduPilot MAVLink connection is unavailable.");

    private sealed record UploadedMission(IReadOnlyList<MavlinkMissionItem> Items)
    {
        public bool FinalItemIsLand => Items.Count > 0 && Items[^1].Command == MavlinkCommandIds.NavLand;
        public bool FinalItemIsRtl => Items.Count > 0 && Items[^1].Command == MavlinkCommandIds.NavReturnToLaunch;
    }
}

public sealed class LogosMissionExecutor : UnsupportedMissionExecutor
{
    public override string ExecutorKind => "Logos (not available)";
    protected override bool IsTarget(UnitObservationSnapshot target) => target.ProfileKey.Contains("logos", StringComparison.OrdinalIgnoreCase) || target.Domain.Contains("logos", StringComparison.OrdinalIgnoreCase);
}

public abstract class UnsupportedMissionExecutor : IFlightMissionExecutor
{
    public abstract string ExecutorKind { get; }
    protected abstract bool IsTarget(UnitObservationSnapshot target);
    public bool Supports(UnitObservationSnapshot target) => IsTarget(target);
    public Task<FlightMissionExecutorResult> UploadAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, CancellationToken cancellationToken = default)
        => Task.FromResult(new FlightMissionExecutorResult(false, $"{ExecutorKind} mission execution is not implemented yet.", "MISSION_EXECUTOR_UNSUPPORTED"));
    public Task<FlightMissionExecutorResult> StartAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default) => Unsupported();
    public Task<FlightMissionExecutorResult> PauseAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default) => Unsupported();
    public Task<FlightMissionExecutorResult> ResumeAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default) => Unsupported();
    public Task<FlightMissionExecutorResult> PrepareResumeAsync(string connectionId, string vehicleId, FlightMissionExecutionArtifact artifact, IReadOnlyList<MavlinkMissionItem>? wireItems, int resumeItemIndex, CancellationToken cancellationToken = default) => Unsupported();
    public Task<FlightMissionExecutorResult> RemoveAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default) => Unsupported();
    public Task<FlightMissionExecutorResult> CompleteAsync(string connectionId, string vehicleId, FlightMissionEndAction endAction = FlightMissionEndAction.Hold, CancellationToken cancellationToken = default) => Unsupported();
    public bool TryGetProgress(string connectionId, string vehicleId, out FlightMissionExecutorProgress progress) { progress = default!; return false; }
    private Task<FlightMissionExecutorResult> Unsupported() => Task.FromResult(new FlightMissionExecutorResult(false, $"{ExecutorKind} mission execution is not implemented yet.", "MISSION_EXECUTOR_UNSUPPORTED"));
}
