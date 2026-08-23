using RobotCommand.Core;
using RobotCommand.Services.Workflows;
using Xunit;

namespace RobotCommand.Tests;

public sealed class ReviewedOperationWorkflowTests
{
    [Fact]
    public async Task ExecutesReviewedPlanAndRetainsTerminalResult()
    {
        var workflow = new ReviewedOperationWorkflow();
        var plan = workflow.Plan(
            ReviewedOperationKind.SikPair,
            "Pair radios",
            [], ["COM3", "COM4"], "Ready", ["COM3", "COM4"],
            _ => Task.FromResult(new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "Paired", ["Verified"])));

        var result = await workflow.ExecuteAsync(plan.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(plan.Id, result.Id);
        Assert.True(workflow.TryGet(plan.Id, out var observed));
        Assert.Equal(ReviewedOperationState.Succeeded, observed!.State);
    }

    [Fact]
    public async Task BlockingFindingPreventsMutation()
    {
        var executed = false;
        var workflow = new ReviewedOperationWorkflow();
        var plan = workflow.Plan(
            ReviewedOperationKind.Px4ParameterApply,
            "Apply parameters",
            [new("VEHICLE_ARMED", WorkflowFindingSeverity.Blocking, "Vehicle must be disarmed.")], [], "Blocked", ["vehicle"],
            _ => { executed = true; return Task.FromResult(new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "Unexpected", [])); });

        var result = await workflow.ExecuteAsync(plan.Id);

        Assert.False(result.Succeeded);
        Assert.False(executed);
        Assert.Equal(ReviewedOperationState.Unavailable, result.State);
    }

    [Fact]
    public async Task CancelledPlanDoesNotExecute()
    {
        var workflow = new ReviewedOperationWorkflow();
        var plan = workflow.Plan(ReviewedOperationKind.MissionPublish, "Publish", [], [], "Ready", ["mission"],
            _ => Task.FromResult(new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "Published", [])));

        await workflow.CancelAsync(plan.Id, "Operator cancelled.");
        var result = await workflow.ExecuteAsync(plan.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(ReviewedOperationState.Cancelled, result.State);
    }
}
