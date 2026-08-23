using System.Text.Json;
using RobotCommand.Models;
using Xunit;

namespace RobotCommand.Tests;

public sealed class TaskBehaviourCompositionTests
{
    private static readonly string[] expected = new[] { "route-alpha", "zone-alpha" };

    [Fact]
    public void Apply_FreezesExactInstalledVersionParametersAndResolvedGeometry()
    {
        var task = Draft() with { GeometryIds = ["route-alpha"] };
        var choice = ReadyChoice(["zone-alpha"]);

        var updated = TaskBehaviourComposition.Apply(
            task,
            choice,
            "{\"altitude_metres\":35,\"confirm\":true}");

        Assert.Equal("survey/search", updated.BehaviourId);
        Assert.Equal("2.1.0", updated.BehaviourVersion);
        Assert.Equal("survey/search", updated.PackageId);
        Assert.Equal("connection-1", updated.ConnectionId);
        Assert.Equal(expected, updated.GeometryIds ?? []);
        Assert.Equal(PlanValidationState.NotValidated, updated.ValidationState);
        Assert.Contains("validate the task again", updated.ValidationSummary, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(updated.ParametersJson);
        Assert.Equal(35, json.RootElement.GetProperty("altitude_metres").GetInt32());
        Assert.True(json.RootElement.GetProperty("confirm").GetBoolean());
    }

    [Fact]
    public void Apply_RejectsRemoteProjection()
    {
        var task = Draft() with { IsLocalDraft = false };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskBehaviourComposition.Apply(task, ReadyChoice([]), "{}"));

        Assert.Contains("local task drafts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_RejectsUnreadyBindings()
    {
        var choice = ReadyChoice([]);
        choice = choice with
        {
            Bindings = choice.Bindings with
            {
                Available = false,
                Summary = "Required geometry is unavailable."
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TaskBehaviourComposition.Apply(Draft(), choice, "{}"));

        Assert.Contains("geometry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OperationalTaskRecord Draft() => new(
        "task-alpha",
        "Search sector alpha",
        "Draft",
        MissionId: "mission-alpha",
        ConnectionId: "connection-1",
        Objective: "Search the assigned sector.",
        TaskType: "search",
        ParametersJson: "{}",
        IsLocalDraft: true,
        GeometryIds: []);

    private static TaskBehaviourChoice ReadyChoice(IReadOnlyList<string> geometryIds)
    {
        var identity = new BehaviourPackageIdentity("survey/search", "2.1.0");
        var remote = new RemoteBehaviourPackageRecord(
            "connection-1",
            identity,
            "Search",
            "Search a registered area.",
            "Stable",
            "Stable",
            new string('a', 64),
            [],
            [],
            [],
            DateTimeOffset.UtcNow);
        var compatibility = new BehaviourCompatibilityAssessment(
            true,
            [],
            true,
            "Compatible");
        var deployment = new BehaviourDeploymentRecord(
            "connection-1",
            identity,
            BehaviourDeploymentStatus.Matching,
            "Installed",
            null,
            remote.ContentSha256,
            null,
            DateTimeOffset.UtcNow,
            compatibility);
        var entry = new BehaviourWorkspaceEntry(
            identity,
            remote.DisplayName,
            remote.Description,
            null,
            remote,
            deployment,
            compatibility);
        var package = BehaviourBindingPackageOption.FromRemote(remote);
        var readiness = new BehaviourGeometryReadiness(
            "connection-1",
            identity.BehaviourId,
            identity.Version!,
            [],
            geometryIds,
            [],
            []);
        var bindings = new BehaviourBindingWorkspaceSnapshot(
            "connection-1",
            package,
            true,
            true,
            "Required geometry is ready.",
            readiness,
            [],
            [],
            DateTimeOffset.UtcNow);
        return new TaskBehaviourChoice(entry, bindings);
    }
}
