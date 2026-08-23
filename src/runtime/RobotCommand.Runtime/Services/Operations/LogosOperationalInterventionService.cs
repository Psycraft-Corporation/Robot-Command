using RobotCommand.Models;
using RobotCommand.Services.Missions;

namespace RobotCommand.Services.Operations;

public sealed class LogosOperationalInterventionService : IOperationalInterventionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IMissionTaskGateway _gateway;
    private readonly IMissionTaskWorkspaceService _workspace;
    private readonly IOperationalSupervisionService _supervision;
    private OperationalInterventionPreparation? _preparation;
    private OperationalInterventionResult? _lastResult;

    public LogosOperationalInterventionService(
        IMissionTaskGateway gateway,
        IMissionTaskWorkspaceService workspace,
        IOperationalSupervisionService supervision)
    {
        _gateway = gateway;
        _workspace = workspace;
        _supervision = supervision;
    }

    public event EventHandler? Changed;

    public OperationalInterventionPreparation? Preparation => Volatile.Read(ref _preparation);

    public OperationalInterventionResult? LastResult => Volatile.Read(ref _lastResult);

    public async Task<OperationalInterventionPreparation> PrepareAsync(
        OperationalInterventionKind kind,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _supervision.Snapshot;
            if (!OperationalInterventionRules.IsApplicable(kind, snapshot, out var applicabilityReason))
            {
                throw new InvalidOperationException(applicabilityReason);
            }

            var source = snapshot.Target!;
            var target = new OperationalInterventionTarget(
                source.ConnectionId,
                source.VehicleId,
                source.VehicleName,
                source.LogosInstanceId,
                source.MissionId,
                snapshot.Mission?.MissionExecutionId ?? source.MissionExecutionId,
                source.TaskId,
                snapshot.Task?.TaskExecutionId ?? source.TaskExecutionId,
                DateTimeOffset.UtcNow);
            var effectiveReason = string.IsNullOrWhiteSpace(reason)
                ? $"Operator requested {OperationalInterventionRules.DisplayName(kind).ToLowerInvariant()} from Robot Command."
                : reason.Trim();
            var operationId = $"intervention-{Guid.NewGuid():N}";
            var identity = WorkspaceCommandIdentity.Create(operationId, $"{CommandName(kind)}.prepare");
            var gatewayResult = OperationalInterventionRules.IsTaskOperation(kind)
                ? await _gateway.PrepareTaskOperationAsync(
                    new TaskOperationPreparationRequest(
                        target.ConnectionId,
                        kind,
                        target.TaskId,
                        target.TaskExecutionId,
                        effectiveReason,
                        OperationalInterventionRules.IsEmergency(kind),
                        0,
                        identity,
                        BuildPolicyContext(target)),
                    cancellationToken)
                : await _gateway.PrepareMissionOperationAsync(
                    new MissionOperationPreparationRequest(
                        target.ConnectionId,
                        kind,
                        target.MissionId,
                        target.MissionExecutionId,
                        effectiveReason,
                        OperationalInterventionRules.IsEmergency(kind),
                        0,
                        identity,
                        BuildPolicyContext(target)),
                    cancellationToken);

            var blockers = BuildBlockers(gatewayResult, target, kind);
            var reference = gatewayResult.Reference
                ?? new PreparedOperationReference(string.Empty, string.Empty);
            var preparedTarget = gatewayResult.Target
                ?? new PreparedOperationTargetSnapshot(
                    target.LogosInstanceId,
                    target.VehicleId,
                    null,
                    target.MissionId,
                    target.MissionExecutionId,
                    target.TaskId,
                    target.TaskExecutionId,
                    0);
            var stage = gatewayResult.Accepted && blockers.Count == 0
                ? OperationalInterventionStage.Prepared
                : OperationalInterventionStage.Rejected;
            var preparation = new OperationalInterventionPreparation(
                operationId,
                kind,
                OperationalInterventionRules.DisplayName(kind),
                target,
                effectiveReason,
                OperationalInterventionRules.IsEmergency(kind),
                gatewayResult.Destructive || OperationalInterventionRules.IsDestructive(kind),
                OperationalInterventionRules.ConfirmationPhrase(kind, target),
                reference,
                preparedTarget,
                gatewayResult.AuthorizationDecision,
                gatewayResult.Readiness,
                gatewayResult.Warnings,
                blockers,
                stage,
                gatewayResult.PreparedAt,
                gatewayResult.ExpiresAt);
            Volatile.Write(ref _preparation, preparation);
            Volatile.Write(ref _lastResult, null);
            RaiseChanged();
            return preparation;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationalInterventionResult> ExecuteAsync(
        string preparationId,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var preparation = Preparation;
            if (preparation is null ||
                !string.Equals(preparation.OperationId, preparationId, StringComparison.Ordinal))
            {
                return PublishResult(new OperationalInterventionResult(
                    preparationId,
                    preparation?.Kind ?? OperationalInterventionKind.CancelMission,
                    OperationalInterventionStage.Rejected,
                    false,
                    "The intervention preparation is no longer active.",
                    OperationalCommandState.Rejected,
                    CompletedAt: DateTimeOffset.UtcNow));
            }

            if (DateTimeOffset.UtcNow >= preparation.ExpiresAt)
            {
                Volatile.Write(ref _preparation, preparation with { Stage = OperationalInterventionStage.Expired });
                RaiseChanged();
                return PublishResult(new OperationalInterventionResult(
                    preparation.OperationId,
                    preparation.Kind,
                    OperationalInterventionStage.Expired,
                    false,
                    "The Logos confirmation token expired. Prepare the intervention again.",
                    OperationalCommandState.Rejected,
                    CompletedAt: DateTimeOffset.UtcNow));
            }

            if (preparation.RequiresTypedConfirmation &&
                !string.Equals(
                    confirmationText?.Trim(),
                    preparation.ConfirmationPhrase,
                    StringComparison.Ordinal))
            {
                return PublishResult(new OperationalInterventionResult(
                    preparation.OperationId,
                    preparation.Kind,
                    OperationalInterventionStage.Rejected,
                    false,
                    $"Type '{preparation.ConfirmationPhrase}' exactly to execute this intervention.",
                    OperationalCommandState.Rejected,
                    CompletedAt: DateTimeOffset.UtcNow));
            }

            var snapshot = _supervision.Snapshot;
            if (!OperationalInterventionRules.IsSameTarget(preparation.Target, snapshot))
            {
                return PublishResult(new OperationalInterventionResult(
                    preparation.OperationId,
                    preparation.Kind,
                    OperationalInterventionStage.Rejected,
                    false,
                    "The supervised mission, task, or execution identity changed after preparation.",
                    OperationalCommandState.Rejected,
                    CompletedAt: DateTimeOffset.UtcNow));
            }

            if (!OperationalInterventionRules.IsApplicable(preparation.Kind, snapshot, out var stateReason))
            {
                return PublishResult(new OperationalInterventionResult(
                    preparation.OperationId,
                    preparation.Kind,
                    OperationalInterventionStage.Rejected,
                    false,
                    stateReason,
                    OperationalCommandState.Rejected,
                    CompletedAt: DateTimeOffset.UtcNow));
            }

            Volatile.Write(ref _preparation, preparation with { Stage = OperationalInterventionStage.Executing });
            RaiseChanged();
            var identity = WorkspaceCommandIdentity.Create(
                preparation.OperationId,
                $"{CommandName(preparation.Kind)}.execute");
            GatewayCommandResult gatewayResult;
            if (OperationalInterventionRules.IsTaskOperation(preparation.Kind))
            {
                gatewayResult = await _workspace.ExecuteTaskCommandAsync(
                    new TaskCommandRequest(
                        preparation.Target.ConnectionId,
                        preparation.Target.TaskId,
                        CommandName(preparation.Kind),
                        preparation.Target.TaskExecutionId,
                        Reason: preparation.Reason,
                        Emergency: preparation.Emergency,
                        RequestId: identity.RequestId,
                        CorrelationId: identity.CorrelationId,
                        IdempotencyKey: identity.IdempotencyKey,
                        Preparation: preparation.Reference),
                    cancellationToken);
            }
            else
            {
                gatewayResult = await _workspace.ExecuteMissionCommandAsync(
                    new MissionCommandRequest(
                        preparation.Target.ConnectionId,
                        preparation.Target.MissionId,
                        CommandName(preparation.Kind),
                        preparation.Target.MissionExecutionId,
                        preparation.Reason,
                        preparation.Emergency,
                        identity.RequestId,
                        identity.CorrelationId,
                        identity.IdempotencyKey,
                        preparation.Reference),
                    cancellationToken);
            }

            var stage = gatewayResult.Accepted
                ? OperationalInterventionStage.Accepted
                : gatewayResult.State == OperationalCommandState.Failed
                    ? OperationalInterventionStage.Failed
                    : OperationalInterventionStage.Rejected;
            Volatile.Write(ref _preparation, preparation with { Stage = stage });
            RaiseChanged();
            return PublishResult(new OperationalInterventionResult(
                preparation.OperationId,
                preparation.Kind,
                stage,
                gatewayResult.Accepted,
                gatewayResult.Message,
                gatewayResult.State,
                gatewayResult.ExecutionId,
                gatewayResult.LifecycleState,
                DateTimeOffset.UtcNow));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DiscardAsync(
        string? preparationId = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Preparation;
            if (current is null ||
                !string.IsNullOrWhiteSpace(preparationId) &&
                !string.Equals(preparationId, current.OperationId, StringComparison.Ordinal))
            {
                return;
            }

            Volatile.Write(ref _preparation, null);
            RaiseChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperationalInterventionResult PublishResult(OperationalInterventionResult result)
    {
        Volatile.Write(ref _lastResult, result);
        RaiseChanged();
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildPolicyContext(
        OperationalInterventionTarget target)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client.surface"] = "robot-command.inspector",
            ["vehicle.name"] = target.VehicleName,
            ["mission.id"] = target.MissionId,
            ["task.id"] = target.TaskId
        };

    private static IReadOnlyList<string> BuildBlockers(
        PreparedOperationGatewayResult gateway,
        OperationalInterventionTarget target,
        OperationalInterventionKind kind)
    {
        var blockers = new List<string>();
        if (!gateway.Accepted)
        {
            blockers.Add(gateway.Message);
        }
        if (!gateway.AuthorizationAllowed)
        {
            blockers.Add($"Authorization: {gateway.AuthorizationDecision}");
        }
        if (string.Equals(gateway.Readiness, "NotReady", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("Logos reported that the intervention is not ready to execute.");
        }
        if (gateway.Target is { } prepared)
        {
            var expectedId = OperationalInterventionRules.IsTaskOperation(kind)
                ? target.TaskId
                : target.MissionId;
            var actualId = OperationalInterventionRules.IsTaskOperation(kind)
                ? prepared.TaskId
                : prepared.MissionId;
            if (!string.IsNullOrWhiteSpace(actualId) &&
                !string.Equals(expectedId, actualId, StringComparison.Ordinal))
            {
                blockers.Add("Logos prepared a different mission or task target than Robot Command requested.");
            }
        }
        return blockers
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string CommandName(OperationalInterventionKind kind)
        => kind switch
        {
            OperationalInterventionKind.PauseMission => "pause",
            OperationalInterventionKind.ResumeMission => "resume",
            OperationalInterventionKind.EndMission => "end",
            OperationalInterventionKind.CancelMission => "cancel",
            OperationalInterventionKind.AbortMission => "abort",
            OperationalInterventionKind.CancelTask => "cancel",
            OperationalInterventionKind.AbortTask => "abort",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
