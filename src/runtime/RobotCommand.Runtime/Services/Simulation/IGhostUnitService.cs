using RobotCommand.Core;
using RobotCommand.Models;

namespace RobotCommand.Services.Simulation;

/// <summary>Absolute target assigned by the Team formation controller.</summary>
public sealed record GhostFormationTarget(
    string LockId,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeAglMetres,
    double VelocityNorthMetresPerSecond = 0d,
    double VelocityEastMetresPerSecond = 0d,
    double VelocityUpMetresPerSecond = 0d,
    bool IsEntryTransition = false);

public interface IGhostUnitService : IAsyncDisposable
{
    IReadOnlyList<string> GhostVehicleIds { get; }

    bool IsGhostConnection(string connectionId);

    bool IsGhostVehicle(string vehicleId);

    async Task<VehicleRecord> CreateAsync(
        string profileId,
        MapViewportSnapshot? viewport = null,
        CancellationToken cancellationToken = default)
        => await CreateAsync(viewport, cancellationToken).ConfigureAwait(false);

    async Task<VehicleRecord> CreateAsync(
        string profileId,
        MapViewportSnapshot? viewport,
        double headingDegrees,
        CancellationToken cancellationToken = default)
        => await CreateAsync(viewport, headingDegrees, cancellationToken).ConfigureAwait(false);

    Task<VehicleRecord> CreateAsync(
        MapViewportSnapshot? viewport = null,
        CancellationToken cancellationToken = default);

    Task<VehicleRecord> CreateAsync(
        MapViewportSnapshot? viewport,
        double headingDegrees,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string vehicleId, CancellationToken cancellationToken = default);

    Task DeleteByConnectionAsync(string connectionId, CancellationToken cancellationToken = default);

    OperatorPolicyEvaluation EvaluatePolicy(OperatorPolicyRequest request);

    Task<OperatorCommandPreparationResult> PrepareAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<OperatorCommandResult> ExecuteAsync(
        OperatorCommandRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Captures Ghost motion for a manual-control session and neutralizes any active Ghost operation.</summary>
    Task<bool> BeginManualControlAsync(string vehicleId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Updates the latest desired body-frame motion. The physics loop owns the actual state mutation.</summary>
    void UpdateManualControl(string vehicleId, string sessionId, ManualControlSetpoint setpoint);

    Task EndManualControlAsync(string vehicleId, string sessionId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Assigns an absolute target that the Ghost physics loop follows at 60 Hz.</summary>
    Task SetFormationTargetAsync(string vehicleId, GhostFormationTarget target, CancellationToken cancellationToken = default);

    /// <summary>Removes formation ownership. Hold stops residual motion safely.</summary>
    Task ClearFormationTargetAsync(string vehicleId, string lockId, bool hold, CancellationToken cancellationToken = default);

    /// <summary>Applies a session-local target-neutral fence to a Ghost.</summary>
    Task ApplyFenceAsync(string vehicleId, FenceDocument fence, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("This Ghost service does not implement local fences."));
    Task<FenceDocument?> DownloadFenceAsync(string vehicleId, CancellationToken cancellationToken = default)
        => Task.FromException<FenceDocument?>(new NotSupportedException("This Ghost service does not implement local fences."));
    Task ClearFenceAsync(string vehicleId, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("This Ghost service does not implement local fences."));

    Task<FlightMissionExecutorResult> PrepareMissionAsync(string vehicleId, FlightMissionExecutionArtifact artifact, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> StartMissionAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> PauseMissionAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> ResumeMissionAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> PrepareMissionForResumeAsync(string vehicleId, FlightMissionExecutionArtifact artifact, int resumeItemIndex, CancellationToken cancellationToken = default);
    Task<FlightMissionExecutorResult> ClearMissionAsync(string vehicleId, CancellationToken cancellationToken = default);
    bool TryGetMissionProgress(string vehicleId, out FlightMissionExecutorProgress progress);
}
