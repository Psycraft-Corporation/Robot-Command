using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Missions;
using RobotCommand.State;
using Xunit;

namespace RobotCommand.Tests;

public sealed class OperationalRunServiceTests
{
    [Fact]
    public async Task Prepare_BuildsValidatedMissionAndTaskDrafts()
    {
        var fixture = new Fixture();

        var preparation = await fixture.Service.PrepareAsync(CreateRequest());

        Assert.True(preparation.CanLaunch);
        Assert.Equal(OperationalRunStage.Prepared, preparation.Stage);
        Assert.True(fixture.Missions.TryGet(preparation.MissionId, out var mission));
        Assert.Equal("vehicle-1", mission!.AssignedVehicleId);
        Assert.True(fixture.Tasks.TryGet(preparation.TaskId, out var task));
        Assert.Equal("takeoff-hold-land", task!.BehaviourId);
        Assert.Equal("vehicle-1", task.AssignedVehicleId);
    }

    [Fact]
    public async Task Prepare_BlocksBehaviourWithUnboundRequiredGeometry()
    {
        var fixture = new Fixture(new BehaviourPackageOption(
            "search",
            "1.0.0",
            "Search",
            "Search an area",
            "Stable",
            "Stable",
            [],
            [],
            [new BehaviourGeometryRequirement(
                "search-area",
                "zone",
                true,
                true,
                false,
                "search_area",
                null,
                "Area to search")]));

        var preparation = await fixture.Service.PrepareAsync(CreateRequest(
            behaviourId: "search",
            behaviourVersion: "1.0.0"));

        Assert.False(preparation.CanLaunch);
        Assert.Equal(OperationalRunStage.Rejected, preparation.Stage);
        Assert.Contains(preparation.Blockers, item => item.Contains("geometry bindings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_AllowsRequiredGeometryWhenBindingIsReady()
    {
        var behaviour = new BehaviourPackageOption(
            "search",
            "1.0.0",
            "Search",
            "Search an area",
            "Stable",
            "Stable",
            [],
            [],
            [new BehaviourGeometryRequirement(
                "search-area",
                "zone",
                true,
                true,
                false,
                "search_area",
                null,
                "Area to search")]);
        var bindings = new FakeBindingService(new BehaviourGeometryReadiness(
            "connection-1",
            "search",
            "1.0.0",
            [new BehaviourGeometryBindingRecord(
                "connection-1",
                "search",
                "1.0.0",
                "search-area",
                "zone-alpha",
                true,
                true,
                true,
                true,
                false,
                "search_area",
                DateTimeOffset.UtcNow,
                [])],
            ["zone-alpha"],
            [],
            []));
        var fixture = new Fixture(behaviour, bindings);

        var preparation = await fixture.Service.PrepareAsync(CreateRequest("search", "1.0.0"));

        Assert.True(preparation.CanLaunch);
        Assert.Equal(["zone-alpha"], preparation.GeometryReadiness!.GeometryIds);
        Assert.Equal(["zone-alpha"], fixture.Missions.Items.Single().GeometryIds);
        Assert.Equal(["zone-alpha"], fixture.Tasks.Items.Single().GeometryIds);
    }

    private static readonly string[] expected = new[] { "mission.publish", "task.publish", "task.assign", "mission.start", "task.start" };

    [Fact]
    public async Task Launch_ExecutesPublicationAssignmentAndStartInOrder()
    {
        var fixture = new Fixture();
        var preparation = await fixture.Service.PrepareAsync(CreateRequest());

        var result = await fixture.Service.LaunchAsync(preparation.OperationId);

        Assert.True(result.Accepted);
        Assert.Equal(OperationalRunStage.Running, result.Stage);
        Assert.Equal(
            expected,
            fixture.Workspace.CommandSteps);
        Assert.All(fixture.Workspace.CorrelationIds, value => Assert.Equal(preparation.OperationId, value));
        Assert.Equal("mission-execution-1", result.MissionExecutionId);
        Assert.Equal("task-execution-1", result.TaskExecutionId);
    }

    [Fact]
    public async Task Launch_CancelsMissionWhenTaskStartIsRejected()
    {
        var fixture = new Fixture { RejectTaskStart = true };
        var preparation = await fixture.Service.PrepareAsync(CreateRequest());

        var result = await fixture.Service.LaunchAsync(preparation.OperationId);

        Assert.False(result.Accepted);
        Assert.Equal(OperationalRunStage.Failed, result.Stage);
        Assert.True(result.CleanupAttempted);
        Assert.Contains("mission.cleanup.cancel", fixture.Workspace.CommandSteps);
        Assert.Equal(preparation.OperationId, fixture.Workspace.CorrelationIds[^1]);
    }

    private static OperationalRunRequest CreateRequest(
        string behaviourId = "takeoff-hold-land",
        string behaviourVersion = "1.0.0")
        => new(
            "connection-1",
            "vehicle-1",
            behaviourId,
            behaviourVersion,
            "Take off, hold, and land",
            "{\"altitudeMetres\":5}");

    private sealed class Fixture
    {
        public Fixture(
            BehaviourPackageOption? behaviour = null,
            FakeBindingService? geometryBindings = null)
        {
            behaviour ??= new BehaviourPackageOption(
                "takeoff-hold-land",
                "1.0.0",
                "Takeoff, hold, land",
                "Bounded SITL smoke behaviour",
                "Stable",
                "Development",
                ["vehicle.flight-control"],
                [],
                []);
            Behaviours = new FakeBehaviourWorkspace(behaviour);
            Vehicles.Upsert(new VehicleRecord(
                "vehicle-1",
                "Dracula SITL",
                ["connection-1"],
                "logos-1",
                null,
                "Multicopter",
                "Air",
                "dracula-sitl",
                AvailabilityState.Online,
                CapabilityKeys: ["vehicle.flight-control"]));
            Workspace = new FakeWorkspace(Missions, Tasks, Vehicles);
            Bindings = geometryBindings ?? new FakeBindingService(
                behaviour.GeometrySlots.Count == 0
                    ? null
                    : new BehaviourGeometryReadiness(
                        "connection-1",
                        behaviour.BehaviourId,
                        behaviour.Version,
                        [],
                        [],
                        [],
                        [$"Required geometry bindings for '{behaviour.BehaviourId}' are not ready."]));
            Service = new OperationalRunService(
                Behaviours,
                Bindings,
                Workspace,
                Missions,
                Tasks,
                Vehicles,
                NullLogger<OperationalRunService>.Instance);
        }

        public EntityStore<string, MissionRecord> Missions { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, OperationalTaskRecord> Tasks { get; } = new(item => item.Id, StringComparer.Ordinal);
        public EntityStore<string, VehicleRecord> Vehicles { get; } = new(item => item.Id, StringComparer.Ordinal);
        public FakeBehaviourWorkspace Behaviours { get; }
        public FakeBindingService Bindings { get; }
        public FakeWorkspace Workspace { get; }
        public OperationalRunService Service { get; }

        public bool RejectTaskStart
        {
            set => Workspace.RejectTaskStart = value;
        }
    }

    private sealed class FakeBehaviourWorkspace : IBehaviourWorkspaceService
    {
        private readonly BehaviourPackageOption[] _packages;

        public FakeBehaviourWorkspace(params BehaviourPackageOption[] packages)
        {
            _packages = packages;
        }

        public event EventHandler? Changed;
        public IReadOnlyList<LocalBehaviourPackageRecord> LocalPackages => [];
        public IReadOnlyList<BehaviourPackageLibraryIssue> LocalIssues => [];
        public bool LastRefreshRemote { get; private set; }
        public bool InventoryStale { get; set; }
        public BehaviourWorkspaceSnapshot GetSnapshot(string connectionId)
            => BuildSnapshot(connectionId, false);

        public Task<BehaviourWorkspaceSnapshot> RefreshAsync(
            string connectionId,
            BehaviourCompatibilityTarget? compatibilityTarget = null,
            bool refreshLocal = false,
            bool refreshRemote = false,
            CancellationToken cancellationToken = default)
        {
            LastRefreshRemote = refreshRemote;
            return Task.FromResult(BuildSnapshot(connectionId, refreshRemote));
        }

        private BehaviourWorkspaceSnapshot BuildSnapshot(string connectionId, bool refresh)
        {
            var package = _packages[0];
            var remote = new RemoteBehaviourPackageRecord(
                connectionId,
                new BehaviourPackageIdentity(package.BehaviourId, package.Version),
                package.DisplayName,
                package.Description,
                package.Status,
                package.Channel,
                "package-sha",
                package.RequiredCapabilities,
                package.ProvidedCapabilities,
                package.GeometrySlots,
                package.UpdatedAt);
            var inventory = new BehaviourRemoteInventoryState(
                connectionId,
                true,
                InventoryStale,
                InventoryStale ? "Installed inventory is stale." : "Installed inventory loaded.",
                [remote],
                DateTimeOffset.UtcNow);
            var target = BehaviourCompatibilityTarget.Create("vehicle-1", "Dracula SITL", ["vehicle.flight-control"]);
            return new BehaviourWorkspaceSnapshot(
                connectionId,
                inventory,
                [new BehaviourWorkspaceEntry(
                    remote.Identity,
                    remote.DisplayName,
                    remote.Description,
                    null,
                    remote,
                    new BehaviourDeploymentRecord(
                        connectionId,
                        remote.Identity,
                        BehaviourDeploymentStatus.Matching,
                        "Installed",
                        null,
                        remote.ContentSha256,
                        null,
                        DateTimeOffset.UtcNow),
                    BehaviourCompatibilityRules.Evaluate(
                        remote.RequiredCapabilities,
                        null,
                        target.CapabilityKeys,
                        target.VehicleProfileKey))],
                target,
                DateTimeOffset.UtcNow);
        }
    }

    private sealed class FakeBindingService(BehaviourGeometryReadiness? readiness)
        : IBehaviourBindingWorkspaceService
    {
        public bool IsAvailable => true;
        public string AvailabilityMessage => "Available";

        public event EventHandler? Changed;
        public BehaviourBindingWorkspaceSnapshot? GetSnapshot(string connectionId, BehaviourPackageIdentity identity) => null;
        public Task<IReadOnlyList<BehaviourBindingPackageOption>> ListPackagesAsync(string connectionId, bool refreshPackages = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BehaviourBindingPackageOption>>([]);
        public Task<BehaviourBindingWorkspaceSnapshot> InspectAsync(string connectionId, BehaviourPackageIdentity identity, bool refreshPackages = false, bool refreshGeometry = false, CancellationToken cancellationToken = default)
        {
            var package = new BehaviourBindingPackageOption(identity, identity.BehaviourId, string.Empty, "Installed", "Development", [], [], readiness?.Bindings.Select(_ => new BehaviourGeometryRequirement("slot", "zone", true, true, false, "", null, "")).ToArray() ?? []);
            var slots = package.GeometrySlots.Select(requirement => BehaviourBindingSlotAssessment.Create(requirement, readiness?.Bindings.FirstOrDefault())).ToArray();
            var snapshot = new BehaviourBindingWorkspaceSnapshot(connectionId, package, true, true, "Available", readiness ?? new BehaviourGeometryReadiness(connectionId, identity.BehaviourId, identity.Version ?? string.Empty, [], [], [], []), slots, [], DateTimeOffset.UtcNow);
            return Task.FromResult(snapshot);
        }
        public Task<BehaviourBindingMutationResult> SetAsync(BehaviourGeometryBindingCommandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BehaviourBindingMutationResult> ClearAsync(BehaviourGeometryBindingCommandRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeWorkspace : IMissionTaskWorkspaceService
    {
        private readonly IEntityStore<string, MissionRecord> _missions;
        private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
        private readonly IEntityStore<string, VehicleRecord> _vehicles;

        public FakeWorkspace(
            IEntityStore<string, MissionRecord> missions,
            IEntityStore<string, OperationalTaskRecord> tasks,
            IEntityStore<string, VehicleRecord> vehicles)
        {
            _missions = missions;
            _tasks = tasks;
            _vehicles = vehicles;
        }

        public bool GatewayAvailable => true;
        public string GatewayStatus => "Available";
        public bool RejectTaskStart { get; set; }
        public List<string> CommandSteps { get; } = [];
        public List<string> CorrelationIds { get; } = [];

        public Task<MissionRecord> ImportMissionAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationalTaskRecord> ImportTaskAsync(string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ExportMissionAsync(string missionId, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ExportTaskAsync(string taskId, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DocumentValidationResult> ValidateMissionAsync(
            string missionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Valid("Mission valid"));

        public Task<DocumentValidationResult> ValidateTaskAsync(
            string taskId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Valid("Task valid"));

        public Task<GatewayCommandResult> PublishMissionAsync(
            string missionId,
            bool validateOnCreate = true,
            bool allowReplaceDraft = true,
            CancellationToken cancellationToken = default)
            => PublishMissionAsync(
                missionId,
                WorkspaceCommandIdentity.Create($"test-{Guid.NewGuid():N}", "mission.publish"),
                validateOnCreate,
                allowReplaceDraft,
                cancellationToken);

        public Task<GatewayCommandResult> PublishMissionAsync(
            string missionId,
            WorkspaceCommandIdentity identity,
            bool validateOnCreate = true,
            bool allowReplaceDraft = true,
            CancellationToken cancellationToken = default)
        {
            Record("mission.publish", identity.CorrelationId);
            var mission = _missions.Items.Single(item => item.Id == missionId);
            _missions.Upsert(mission with { IsLocalDraft = false, State = "Registered" });
            return Accepted("Mission published");
        }

        public Task<GatewayCommandResult> PublishTaskAsync(
            string taskId,
            bool validateOnCreate = true,
            bool allowReplaceDraft = true,
            CancellationToken cancellationToken = default)
            => PublishTaskAsync(
                taskId,
                WorkspaceCommandIdentity.Create($"test-{Guid.NewGuid():N}", "task.publish"),
                validateOnCreate,
                allowReplaceDraft,
                cancellationToken);

        public Task<GatewayCommandResult> PublishTaskAsync(
            string taskId,
            WorkspaceCommandIdentity identity,
            bool validateOnCreate = true,
            bool allowReplaceDraft = true,
            CancellationToken cancellationToken = default)
        {
            Record("task.publish", identity.CorrelationId);
            var task = _tasks.Items.Single(item => item.Id == taskId);
            _tasks.Upsert(task with { IsLocalDraft = false, State = "Registered" });
            return Accepted("Task published");
        }

        public Task PlanTaskAssignmentAsync(
            string taskId,
            string vehicleId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DocumentValidationResult> ValidateTaskForVehicleAsync(
            string taskId,
            string vehicleId,
            CancellationToken cancellationToken = default)
        {
            var task = _tasks.Items.Single(item => item.Id == taskId);
            var vehicle = _vehicles.Items.Single(item => item.Id == vehicleId);
            _tasks.Upsert(task with
            {
                AssignedVehicleId = vehicle.Id,
                AssignedLogosInstanceId = vehicle.LogosInstanceId,
                ConnectionId = vehicle.ConnectionIds[0],
                AssignmentState = "Validated"
            });
            return Task.FromResult(Valid("Task valid for vehicle"));
        }

        public Task<GatewayCommandResult> AssignTaskAsync(
            string taskId,
            string vehicleId,
            bool validateOnAssign = true,
            CancellationToken cancellationToken = default)
            => AssignTaskAsync(
                taskId,
                vehicleId,
                WorkspaceCommandIdentity.Create($"test-{Guid.NewGuid():N}", "task.assign"),
                validateOnAssign,
                cancellationToken);

        public Task<GatewayCommandResult> AssignTaskAsync(
            string taskId,
            string vehicleId,
            WorkspaceCommandIdentity identity,
            bool validateOnAssign = true,
            CancellationToken cancellationToken = default)
        {
            Record("task.assign", identity.CorrelationId);
            var task = _tasks.Items.Single(item => item.Id == taskId);
            _tasks.Upsert(task with
            {
                AssignmentState = "Assigned",
                TaskExecutionId = "task-execution-1"
            });
            return Accepted("Task assigned", "task-execution-1");
        }

        public Task RefreshRemoteAsync(string connectionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GatewayCommandResult> ExecuteMissionCommandAsync(
            MissionCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            var step = request.Command.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                ? "mission.cleanup.cancel"
                : "mission.start";
            Record(step, request.CorrelationId!);
            return Accepted(
                request.Command.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
                    ? "Mission cancelled"
                    : "Mission started",
                "mission-execution-1");
        }

        public Task<GatewayCommandResult> ExecuteTaskCommandAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Record("task.start", request.CorrelationId!);
            return RejectTaskStart
                ? Task.FromResult(new GatewayCommandResult(
                    false,
                    OperationalCommandState.Rejected,
                    "Task start rejected"))
                : Accepted("Task started", "task-execution-1");
        }

        private void Record(string step, string correlationId)
        {
            CommandSteps.Add(step);
            CorrelationIds.Add(correlationId);
        }

        private static DocumentValidationResult Valid(string summary)
            => new(PlanValidationState.Valid, summary, []);

        private static Task<GatewayCommandResult> Accepted(string message, string? executionId = null)
            => Task.FromResult(new GatewayCommandResult(
                true,
                OperationalCommandState.Accepted,
                message,
                executionId));
    }
}
