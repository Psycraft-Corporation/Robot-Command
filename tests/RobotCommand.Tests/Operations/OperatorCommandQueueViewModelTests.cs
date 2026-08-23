using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Workflows;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperatorCommandQueueViewModelTests
{
    [Fact]
    public async Task CancellingLandingRequestsHoldInsteadOfTreatingLandingAsGrounded()
    {
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var gateway = new RecordingOperatorControlService { CommandStore = commands, CompleteHold = true };
        using var workflow = new OperatorCommandWorkflow(gateway, telemetry, commands);

        telemetry.Upsert(new VehicleTelemetryRecord(
            "telemetry-1", "vehicle-1", "connection-1", "logos-1", AvailabilityState.Online,
            true, "Landing", "Auto Land", "PX4", "Healthy", "Ready",
            43, -79, 120, 12, 0, 0, -12, 0, 0, -1, 90, false,
            "PX4", "Landing", DateTimeOffset.UtcNow));

        var queued = await workflow.QueueAsync(new OperatorCommandQueueRequest(
            OperatorWorkflowCommandKind.Land,
            [new OperatorCommandQueueTarget("vehicle-1", OperatorWorkflowParameters.None)],
            "Land"));
        await workflow.ExecuteAsync(queued.BatchId);

        var result = await workflow.CancelActiveAsync(["vehicle-1"]);

        var execution = Assert.Single(result);
        Assert.True(execution.Accepted);
        Assert.Contains("Hold confirmed", execution.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, gateway.HoldExecutionCount);
        Assert.Empty(workflow.ActiveCommands);
    }

    [Fact]
    public async Task QueuedCommandSurvivesSelectionChangeAndExecutesFromTheQueue()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var gateway = new RecordingOperatorControlService();
        using var workflow = new OperatorCommandWorkflow(gateway, telemetry, commands);

        connections.Upsert(new ConnectionRecord(
            "connection-1", "Test", "http://localhost:50051", ConnectionMode.Direct,
            AvailabilityState.Online, false));
        vehicles.Upsert(new VehicleRecord(
            "vehicle-1", "Test unit", ["connection-1"], "logos-1", null,
            "Multicopter", "Air", "test", AvailabilityState.Online,
            "Ready", "Landed", "Disarmed", "Healthy", ["operator_control"]));
        selection.Select(new OperationalSelection(
            SelectionKind.Vehicle, "vehicle-1", "Test unit", "", []));

        var viewModel = new OperatorControlsViewModel(
            workflow, selection, vehicles, telemetry, connections, commands);

        viewModel.PrepareArmCommand.Execute(null);
        await EventuallyAsync(() => viewModel.QueuedPlans.Count == 1);

        Assert.Equal("Available", viewModel.SelectedQueuedAvailability);

        gateway.Availability = OperatorControlAvailability.Blocked;
        await EventuallyAsync(() => viewModel.SelectedQueuedAvailability == "Unavailable: Vehicle is not ready.");
        Assert.False(viewModel.ExecuteQueuedCommandsCommand.CanExecute(null));

        gateway.Availability = OperatorControlAvailability.Ready;
        await EventuallyAsync(() => viewModel.SelectedQueuedAvailability == "Available");
        Assert.True(viewModel.ExecuteQueuedCommandsCommand.CanExecute(null));

        selection.Clear();

        Assert.Single(viewModel.QueuedPlans);
        Assert.Equal("Arm", viewModel.QueuedPlans["vehicle-1"].DisplayName);

        selection.Select(new OperationalSelection(
            SelectionKind.Vehicle, "vehicle-1", "Test unit", "", []));
        Assert.Equal("Available", viewModel.SelectedQueuedAvailability);
        Assert.True(viewModel.ExecuteQueuedCommandsCommand.CanExecute(null));
        viewModel.ExecuteQueuedCommandsCommand.Execute(null);
        await EventuallyAsync(() => viewModel.QueuedPlans.Count == 0);

        Assert.Equal(1, gateway.ExecutionCount);
        Assert.True(viewModel.CancelExecutingCommandsCommand.CanExecute(null));
        viewModel.CancelExecutingCommandsCommand.Execute(null);
        await EventuallyAsync(() => !viewModel.HasExecutingCommand);
        Assert.False(viewModel.CancelExecutingCommandsCommand.CanExecute(null));
    }

    [Fact]
    public async Task RejectedExecutionRemainsVisibleUntilDismissed()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var gateway = new RecordingOperatorControlService
        {
            ExecutionResult = new OperatorCommandResult(
                false,
                OperationalCommandState.Rejected,
                "ARM_REJECTED: PREARM_CHECK_FAILED")
        };
        using var workflow = new OperatorCommandWorkflow(gateway, telemetry, commands);

        connections.Upsert(new ConnectionRecord(
            "connection-1", "Test", "http://localhost:50051", ConnectionMode.Direct,
            AvailabilityState.Online, false));
        vehicles.Upsert(Unit("vehicle-1", "connection-1"));
        selection.Select(new OperationalSelection(
            SelectionKind.Vehicle, "vehicle-1", "Test unit", "", []));

        var viewModel = new OperatorControlsViewModel(
            workflow, selection, vehicles, telemetry, connections, commands);

        viewModel.PrepareArmCommand.Execute(null);
        await EventuallyAsync(() => viewModel.HasQueuedCommand);
        viewModel.ExecuteQueuedCommandsCommand.Execute(null);
        await EventuallyAsync(() => viewModel.HasRejectedCommand);

        Assert.Equal("Command rejected", viewModel.CommandOutcomeTitle);
        Assert.Contains("Prearm check failed", viewModel.RejectedCommandMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.DismissRejectedCommand.CanExecute(null));

        viewModel.DismissRejectedCommand.Execute(null);

        Assert.False(viewModel.HasRejectedCommand);
    }

    [Fact]
    public async Task NoResponseExecutionUsesDistinctOutcomeTitle()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var gateway = new RecordingOperatorControlService
        {
            ExecutionResult = new OperatorCommandResult(
                false,
                OperationalCommandState.TimedOut,
                "Change altitude was sent, but no MAVLink acknowledgement arrived. Monitoring telemetry.")
        };
        using var workflow = new OperatorCommandWorkflow(gateway, telemetry, commands);

        connections.Upsert(new ConnectionRecord(
            "connection-1", "Test", "http://localhost:50051", ConnectionMode.Direct,
            AvailabilityState.Online, false));
        vehicles.Upsert(Unit("vehicle-1", "connection-1"));
        selection.Select(new OperationalSelection(
            SelectionKind.Vehicle, "vehicle-1", "Test unit", "", []));

        var viewModel = new OperatorControlsViewModel(
            workflow, selection, vehicles, telemetry, connections, commands);

        viewModel.PrepareArmCommand.Execute(null);
        await EventuallyAsync(() => viewModel.HasQueuedCommand);
        viewModel.ExecuteQueuedCommandsCommand.Execute(null);
        await EventuallyAsync(() => viewModel.HasRejectedCommand);

        Assert.Equal("No response", viewModel.CommandOutcomeTitle);
        Assert.Contains("no MAVLink acknowledgement", viewModel.RejectedCommandMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormationPreviewTransitionsFromFirstPointToLiveShapeAndClears()
    {
        var vehicles = new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal);
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var connections = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var selection = new SelectionService();
        var gateway = new RecordingOperatorControlService();
        using var workflow = new OperatorCommandWorkflow(gateway, telemetry, commands);

        connections.Upsert(new ConnectionRecord(
            "connection-1", "Test", "http://localhost:50051", ConnectionMode.Direct,
            AvailabilityState.Online, false));
        vehicles.Upsert(Unit("vehicle-1", "connection-1"));
        vehicles.Upsert(Unit("vehicle-2", "connection-1"));
        selection.SetUnitSelection(
        [
            new OperationalSelection(SelectionKind.Vehicle, "vehicle-1", "Vehicle 1", "", []),
            new OperationalSelection(SelectionKind.Vehicle, "vehicle-2", "Vehicle 2", "", [])
        ]);

        var viewModel = new OperatorControlsViewModel(
            workflow,
            selection,
            vehicles,
            telemetry,
            connections,
            commands,
            [new LineFormationProvider()]);
        var changes = 0;
        viewModel.FormationPreviewChanged += (_, _) => changes++;

        viewModel.UpdateFormationStartPreview(new MapCommandTarget(43, -79));

        Assert.Single(viewModel.FormationPreviewTargets);
        Assert.Empty(viewModel.FormationPreviewPaths);

        viewModel.UpdateFormationPreview(new MapAssemblyRequest(
            "line",
            new MapCommandTarget(43, -79),
            new MapCommandTarget(43.001, -78.999)));

        Assert.Equal(2, viewModel.FormationPreviewTargets.Count);
        Assert.Single(viewModel.FormationPreviewPaths);

        viewModel.ClearFormationPreview();

        Assert.Empty(viewModel.FormationPreviewTargets);
        Assert.Empty(viewModel.FormationPreviewPaths);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task WorkflowReplacesOnlyThePriorUnsubmittedCommandForTheSameUnit()
    {
        var telemetry = new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal);
        var commands = new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal);
        var gateway = new RecordingOperatorControlService();
        using var workflow = new OperatorCommandWorkflow(gateway, telemetry, commands);

        var arm = await workflow.QueueAsync(new OperatorCommandQueueRequest(
            OperatorWorkflowCommandKind.Arm,
            [new OperatorCommandQueueTarget("vehicle-1", OperatorWorkflowParameters.None)],
            "First command"));
        var hold = await workflow.QueueAsync(new OperatorCommandQueueRequest(
            OperatorWorkflowCommandKind.Hold,
            [new OperatorCommandQueueTarget("vehicle-1", OperatorWorkflowParameters.None)],
            "Replacement command"));

        var queued = Assert.Single(workflow.QueuedCommands);
        Assert.Equal(OperatorWorkflowCommandKind.Hold, queued.Command);
        Assert.Equal(hold.BatchId, queued.BatchId);
        Assert.False(workflow.TryGet(arm.Commands.Single().QueueId, out _));
        Assert.Equal(1, gateway.CancellationCount);
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 500 && !predicate(); i++)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private static VehicleRecord Unit(string id, string connectionId)
        => new(id, id, [connectionId], $"logos-{id}", null,
            "Multicopter", "Air", "test", AvailabilityState.Online,
            "Ready", "Landed", "Disarmed", "Healthy", ["operator_control"]);

    private sealed class RecordingOperatorControlService : IOperatorControlService
    {
        public OperatorGatewayStatus GatewayStatus { get; } = new(true, "Available");
        public int ExecutionCount { get; private set; }
        public int CancellationCount { get; private set; }
        public int HoldExecutionCount { get; private set; }
        public IEntityStore<string, OperationalCommandRecord>? CommandStore { get; set; }
        public bool CompleteHold { get; set; }
        public OperatorControlAvailability Availability { get; set; } = OperatorControlAvailability.Ready;
        public OperatorCommandResult ExecutionResult { get; set; } =
            new(true, OperationalCommandState.Accepted, "Accepted");

        public Task<OperatorCommandPlan> PrepareAsync(
            string vehicleId,
            OperatorCommandKind command,
            string reason,
            CancellationToken cancellationToken = default)
            => PrepareAsync(vehicleId, command, reason, OperatorCommandParameters.None, cancellationToken);

        public Task<OperatorCommandPlan> PrepareAsync(
            string vehicleId,
            OperatorCommandKind command,
            string reason,
            OperatorCommandParameters parameters,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var plan = new OperatorCommandPlan(
                $"command-{Guid.NewGuid():N}",
                "correlation",
                "idempotency",
                command,
                command.ToString(),
                OperatorCommandSafety.Routine,
                Availability,
                new OperatorCommandTarget("connection-1", vehicleId, "logos-1", now),
                "Test unit",
                reason,
                false,
                "",
                Availability == OperatorControlAvailability.Blocked
                    ? [new OperatorPreflightFinding(
                        "NOT_READY",
                        OperatorPreflightSeverity.Blocking,
                        "Vehicle is not ready.")]
                    : [],
                new OperatorPolicyEvaluation(true, true, "Allow", "Allowed", []),
                now,
                now.AddMinutes(1),
                new PreparedVehicleOperation(
                    new PreparedOperationReference("preparation", "token"),
                    new PreparedOperationTargetSnapshot("logos-1", vehicleId, "binding", null, null, null, null, 1),
                    "vehicle.arm", "Allowed", "Ready", [], now, now.AddMinutes(1)),
                parameters);
            CommandStore?.Upsert(new OperationalCommandRecord(
                plan.CommandId, command.ToString(), "Vehicle", vehicleId, "connection-1",
                OperationalCommandState.Draft, plan.DisplayName, "Prepared", plan.CorrelationId,
                now, now, vehicleId, "logos-1", plan.IdempotencyKey));
            return Task.FromResult(plan);
        }

        public Task<OperatorCommandResult> ExecuteAsync(
            OperatorCommandPlan plan,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (plan.Command == OperatorCommandKind.Hold)
            {
                HoldExecutionCount++;
                if (CompleteHold)
                {
                    CommandStore?.Upsert(new OperationalCommandRecord(
                        plan.CommandId, plan.Command.ToString(), "Vehicle", plan.Target.VehicleId,
                        plan.Target.ConnectionId, OperationalCommandState.Succeeded,
                        plan.DisplayName, "Hold confirmed.", plan.CorrelationId,
                        plan.CreatedAt, DateTimeOffset.UtcNow, plan.Target.VehicleId,
                        plan.Target.LogosInstanceId, plan.IdempotencyKey));
                    return Task.FromResult(new OperatorCommandResult(
                        true, OperationalCommandState.Succeeded, "Hold confirmed.", plan.CommandId));
                }
            }
            CommandStore?.Upsert(new OperationalCommandRecord(
                plan.CommandId, plan.Command.ToString(), "Vehicle", plan.Target.VehicleId,
                plan.Target.ConnectionId, ExecutionResult.State, plan.DisplayName,
                ExecutionResult.Message, plan.CorrelationId, plan.CreatedAt,
                DateTimeOffset.UtcNow, plan.Target.VehicleId, plan.Target.LogosInstanceId,
                plan.IdempotencyKey));
            return Task.FromResult(ExecutionResult);
        }

        public Task CancelAsync(
            OperatorCommandPlan plan,
            string message = "Cancelled before submission.",
            CancellationToken cancellationToken = default)
        {
            CancellationCount++;
            return Task.CompletedTask;
        }
    }
}
