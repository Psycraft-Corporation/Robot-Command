using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Missions;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests.ViewModels;

public sealed class QuickRunViewModelTests
{
    [Fact]
    public async Task Initialize_SelectsConnectedVehicleAndCompatibleBehaviour()
    {
        var fixture = new Fixture();

        await fixture.ViewModel.InitializeAsync();

        Assert.Single(fixture.ViewModel.Vehicles);
        Assert.Equal("vehicle-1", fixture.ViewModel.SelectedVehicle?.VehicleId);
        Assert.Single(fixture.ViewModel.Behaviours);
        Assert.Equal("behaviour.hold", fixture.ViewModel.SelectedBehaviour?.BehaviourId);
        Assert.Equal(1, fixture.Runs.ListCalls);
    }

    [Fact]
    public async Task Prepare_BuildsRequestFromCurrentOperatorInputs()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.Objective = "Take off, hold, and land";
        fixture.ViewModel.ParametersJson = "{\"altitudeMetres\":5}";
        fixture.ViewModel.RejectOnWarnings = true;

        fixture.ViewModel.PrepareCommand.Execute(null);
        await fixture.Runs.PrepareObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => fixture.ViewModel.Preparation is not null);

        var request = Assert.IsType<OperationalRunRequest>(fixture.Runs.LastPrepareRequest);
        Assert.Equal("connection-1", request.ConnectionId);
        Assert.Equal("vehicle-1", request.VehicleId);
        Assert.Equal("behaviour.hold", request.BehaviourId);
        Assert.Equal("Take off, hold, and land", request.Objective);
        Assert.Equal("{\r\n  \"altitudeMetres\": 5\r\n}", request.ParametersJson);
        Assert.True(request.RejectOnWarnings);
        Assert.True(fixture.ViewModel.Preparation!.CanLaunch);
    }

    [Fact]
    public async Task Prepare_RejectsNonObjectJsonBeforeCallingService()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.ParametersJson = "[]";

        Assert.False(fixture.ViewModel.PrepareCommand.CanExecute(null));
        Assert.Equal(0, fixture.Runs.PrepareCalls);
        Assert.Null(fixture.ViewModel.Preparation);
    }

    [Fact]
    public async Task Launch_ProjectsStageResultsAndCleanupMessage()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.PrepareCommand.Execute(null);
        await fixture.Runs.PrepareObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => fixture.ViewModel.Preparation is not null);

        fixture.Runs.LaunchResult = new OperationalRunResult(
            "operation-1",
            "mission-1",
            "task-1",
            OperationalRunStage.Failed,
            false,
            "Task start was rejected.",
            [new OperationalRunStepResult(
                "Start task",
                false,
                OperationalCommandState.Rejected,
                "Vehicle was not ready.")],
            CleanupAttempted: true,
            CleanupMessage: "Mission cancelled.");

        fixture.ViewModel.LaunchCommand.Execute(null);
        await fixture.Runs.LaunchObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => fixture.ViewModel.Result is not null);

        Assert.False(fixture.ViewModel.Result!.Accepted);
        Assert.Single(fixture.ViewModel.Steps);
        Assert.Contains("Mission cancelled", fixture.ViewModel.Detail, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected view-model state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Connections.Upsert(new ConnectionRecord(
                "connection-1",
                "Local SITL",
                "http://localhost:50051",
                ConnectionMode.Direct,
                AvailabilityState.Online,
                true,
                "logos-1"));
            Vehicles.Upsert(new VehicleRecord(
                "vehicle-1",
                "Dracula SITL",
                ["connection-1"],
                "logos-1",
                null,
                "Multicopter",
                "Air",
                "sitl",
                AvailabilityState.Online,
                "Ready",
                "Running",
                "Disarmed",
                "Healthy",
                ["flight.hold"]));
            ViewModel = new QuickRunViewModel(
                Runs,
                Vehicles,
                Connections,
                new ImmediateDispatcher());
        }

        public EntityStore<string, VehicleRecord> Vehicles { get; } =
            new(item => item.Id, StringComparer.Ordinal);

        public EntityStore<string, ConnectionRecord> Connections { get; } =
            new(item => item.Id, StringComparer.Ordinal);

        public FakeOperationalRunService Runs { get; } = new();

        public QuickRunViewModel ViewModel { get; }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }


    private sealed class FakeOperationalRunService : IOperationalRunService
    {
        private readonly List<OperationalRunPreparation> _preparations = [];

        public event EventHandler? Changed;

        public IReadOnlyList<OperationalRunPreparation> Preparations => _preparations;

        public int ListCalls { get; private set; }

        public int PrepareCalls { get; private set; }

        public OperationalRunRequest? LastPrepareRequest { get; private set; }

        public TaskCompletionSource<bool> PrepareObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> LaunchObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationalRunResult LaunchResult { get; set; } = new(
            "operation-1",
            "mission-1",
            "task-1",
            OperationalRunStage.Running,
            true,
            "Running",
            []);

        public Task<IReadOnlyList<BehaviourPackageOption>> ListCompatibleBehavioursAsync(
            string connectionId,
            string vehicleId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            IReadOnlyList<BehaviourPackageOption> result =
            [
                new BehaviourPackageOption(
                    "behaviour.hold",
                    "1.0.0",
                    "Takeoff Hold Land",
                    "A bounded SITL behaviour.",
                    "Installed",
                    "stable",
                    ["flight.hold"],
                    [],
                    [])
            ];
            return Task.FromResult(result);
        }

        public Task<OperationalRunPreparation> PrepareAsync(
            OperationalRunRequest request,
            CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            LastPrepareRequest = request;
            var valid = new DocumentValidationResult(PlanValidationState.Valid, "Valid", []);
            var preparation = new OperationalRunPreparation(
                "operation-1",
                request,
                new BehaviourPackageOption(
                    request.BehaviourId,
                    request.BehaviourVersion,
                    "Takeoff Hold Land",
                    "A bounded SITL behaviour.",
                    "Installed",
                    "stable",
                    [],
                    [],
                    []),
                "mission-1",
                "task-1",
                valid,
                valid,
                [],
                [],
                OperationalRunStage.Prepared,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5));
            _preparations.Clear();
            _preparations.Add(preparation);
            Changed?.Invoke(this, EventArgs.Empty);
            PrepareObserved.TrySetResult(true);
            return Task.FromResult(preparation);
        }

        public Task<OperationalRunResult> LaunchAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            if (_preparations.Count > 0)
            {
                _preparations[0] = _preparations[0] with { Stage = LaunchResult.Stage };
                Changed?.Invoke(this, EventArgs.Empty);
            }

            LaunchObserved.TrySetResult(true);
            return Task.FromResult(LaunchResult);
        }

        public Task DiscardAsync(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            _preparations.RemoveAll(item => item.OperationId == operationId);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
