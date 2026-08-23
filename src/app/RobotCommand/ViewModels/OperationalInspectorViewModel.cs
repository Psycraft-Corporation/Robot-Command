using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Operations;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class OperationalInspectorViewModel : ObservableObject, IDisposable
{
    private readonly IOperationalInspectionTargetService _targets;
    private readonly IOperationalExecutionTargetResolver _resolver;
    private readonly IOperationalSupervisionService _supervision;
    private readonly IOperationalInterventionService _interventions;
    private readonly ISelectionService _selection;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Timer _expiryTimer;

    private OperationalInspectionResolution _selectionResolution =
        OperationalInspectionResolution.Unresolved(
            OperationalInspectionResolutionState.UnsupportedSelection,
            "No execution target resolved.",
            "Select a mission, task, vehicle, or runtime.");
    private string _status = "Inspector is ready.";
    private string _detail = "Select an operational record or launch Quick Run to begin authoritative supervision.";
    private string _interventionReason = string.Empty;
    private string _interventionConfirmation = string.Empty;
    private bool _followSelection = true;
    private bool _isBusy;
    private bool _isInitialized;
    private long _activeRequestSequence;
    private int _disposed;

    public OperationalInspectorViewModel(
        IOperationalInspectionTargetService targets,
        IOperationalExecutionTargetResolver resolver,
        IOperationalSupervisionService supervision,
        IOperationalInterventionService interventions,
        ISelectionService selection,
        IUiDispatcher dispatcher)
    {
        _targets = targets;
        _resolver = resolver;
        _supervision = supervision;
        _interventions = interventions;
        _selection = selection;
        _dispatcher = dispatcher;

        InspectSelectionCommand = new AsyncRelayCommand(InspectSelectionAsync, CanInspectSelection);
        RefreshTargetCommand = new AsyncRelayCommand(RefreshTargetAsync, () => !IsBusy && CurrentTarget is not null);
        StopWatchingCommand = new AsyncRelayCommand(StopWatchingAsync, () => !IsBusy && IsWatching);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => !IsBusy && HasTarget);
        PreparePauseMissionCommand = InterventionCommand(OperationalInterventionKind.PauseMission);
        PrepareResumeMissionCommand = InterventionCommand(OperationalInterventionKind.ResumeMission);
        PrepareEndMissionCommand = InterventionCommand(OperationalInterventionKind.EndMission);
        PrepareCancelMissionCommand = InterventionCommand(OperationalInterventionKind.CancelMission);
        PrepareAbortMissionCommand = InterventionCommand(OperationalInterventionKind.AbortMission);
        PrepareCancelTaskCommand = InterventionCommand(OperationalInterventionKind.CancelTask);
        PrepareAbortTaskCommand = InterventionCommand(OperationalInterventionKind.AbortTask);
        ExecuteInterventionCommand = new AsyncRelayCommand(ExecuteInterventionAsync, CanExecuteIntervention);
        DiscardInterventionCommand = new AsyncRelayCommand(DiscardInterventionAsync, CanDiscardIntervention);

        _targets.Changed += OnTargetChanged;
        _selection.Changed += OnSelectionChanged;
        _supervision.Changed += OnSupervisionChanged;
        _interventions.Changed += OnInterventionsChanged;
        _expiryTimer = new Timer(OnExpiryTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public OperationalInspectionRequest? InspectionRequest => _targets.Current;

    public OperationalExecutionSnapshot Snapshot => _supervision.Snapshot;

    public OperationalExecutionTarget? CurrentTarget => Snapshot.Target ?? InspectionRequest?.Target;

    public bool HasTarget => CurrentTarget is not null;

    public bool IsWatching =>
        Snapshot.MissionWatch.Active || Snapshot.TaskWatch.Active || Snapshot.AutonomyWatch.Active;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseCommandStates();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public bool FollowSelection
    {
        get => _followSelection;
        set
        {
            if (!SetProperty(ref _followSelection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FollowSelectionSummary));
            if (value && _isInitialized && IsInspectableSelection(_selection.Current))
            {
                _ = InspectSelectionAsync(CancellationToken.None);
            }
        }
    }

    public string FollowSelectionSummary => FollowSelection
        ? "Mission, task, vehicle, and runtime selections automatically retarget the Inspector."
        : "The current execution target remains pinned until changed manually.";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public OperationalInspectionResolution SelectionResolution
    {
        get => _selectionResolution;
        private set
        {
            if (SetProperty(ref _selectionResolution, value))
            {
                OnPropertyChanged(nameof(SelectionResolutionSummary));
                OnPropertyChanged(nameof(SelectionResolutionDetail));
                OnPropertyChanged(nameof(CanResolveSelection));
                RaiseCommandStates();
            }
        }
    }

    public string SelectionResolutionSummary => SelectionResolution.Summary;

    public string SelectionResolutionDetail => SelectionResolution.Detail;

    public bool CanResolveSelection => SelectionResolution.Resolved;

    public string TargetSummary => CurrentTarget is null
        ? "No execution target"
        : $"{CurrentTarget.VehicleName} · mission {CurrentTarget.MissionId} · task {CurrentTarget.TaskId}";

    public string TargetIdentity => CurrentTarget is null
        ? "Select a projected mission, task, vehicle, or runtime."
        : $"{CurrentTarget.ConnectionId} · {CurrentTarget.VehicleId} · {CurrentTarget.LogosInstanceId ?? "unknown Logos instance"}";

    public string TargetSourceSummary => InspectionRequest is null
        ? "No inspection request has been published."
        : $"{InspectionRequest.SourceLabel} · {InspectionRequest.SourceDescription}";

    public string MissionSummary => Snapshot.Mission is not { } mission
        ? "Waiting for mission status."
        : $"{mission.State} · {mission.ProgressText} · {FirstNonEmpty(mission.ActiveStateName, mission.Message, mission.Code, "No detail")}";

    public string MissionDetail => Snapshot.Mission is not { } mission
        ? Snapshot.MissionWatch.Detail
        : $"Health {mission.Health} · readiness {mission.Readiness} · policy {FirstNonEmpty(mission.ActivePolicyId, "default")}";

    public string TaskSummary => Snapshot.Task is not { } task
        ? "Waiting for task status."
        : $"{task.State} · {task.ProgressText} · {FirstNonEmpty(task.Phase, task.ActiveBehaviourState, task.Message, "No detail")}";

    public string TaskDetail => Snapshot.Task is not { } task
        ? Snapshot.TaskWatch.Detail
        : $"Assignment {task.AssignmentState} · behaviour {FirstNonEmpty(task.ActiveBehaviourId, "not active")} · geometry {FirstNonEmpty(task.ActiveGeometryId, "none")}";

    public string AutonomySummary => Snapshot.Autonomy is not { } autonomy
        ? "Waiting for autonomy runtime status."
        : $"{autonomy.State} · {FirstNonEmpty(autonomy.BehaviourId, "No active behaviour")} · {FirstNonEmpty(autonomy.ActiveStateName, autonomy.TreeState, autonomy.Message, "No detail")}";

    public string AutonomyDetail => Snapshot.Autonomy is not { } autonomy
        ? Snapshot.AutonomyWatch.Detail
        : $"Tree {autonomy.TreeState} · ticks {autonomy.TreeTickCount} · outcome {FirstNonEmpty(autonomy.LatestOutcome, "none")}";

    public string WatchSummary =>
        $"Mission {Snapshot.MissionWatch.State} · Task {Snapshot.TaskWatch.State} · Autonomy {Snapshot.AutonomyWatch.State}";

    public string WatchDetail => string.Join(Environment.NewLine,
        $"Mission: {Snapshot.MissionWatch.Summary} — {Snapshot.MissionWatch.Detail}",
        $"Task: {Snapshot.TaskWatch.Summary} — {Snapshot.TaskWatch.Detail}",
        $"Autonomy: {Snapshot.AutonomyWatch.Summary} — {Snapshot.AutonomyWatch.Detail}");

    public IReadOnlyList<BehaviourTreeNodeRuntimeRecord> TreeNodes => Snapshot.TreeNodes;

    public IReadOnlyList<BehaviourTreeNodeRuntimeRecord> AttentionNodes => Snapshot.AttentionNodes;

    public bool HasTreeNodes => TreeNodes.Count > 0;

    public bool HasAttentionNodes => AttentionNodes.Count > 0;

    public string TreeSummary => Snapshot.Autonomy is not { } autonomy
        ? $"{TreeNodes.Count} projected node(s)"
        : $"{TreeNodes.Count} projected node(s) · sequence {autonomy.TreeSequence} · {(autonomy.Ticking ? "ticking" : "not ticking")}";

    public IReadOnlyList<string> BlockingConditions =>
        (Snapshot.Mission?.BlockingConditions ?? [])
        .Concat(Snapshot.Task?.BlockingConditions ?? [])
        .Concat(Snapshot.Autonomy?.BlockingConditions ?? [])
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public bool HasBlockingConditions => BlockingConditions.Count > 0;

    public IReadOnlyList<OperationalRuntimeIssue> RuntimeIssues => Snapshot.Issues;

    public bool HasRuntimeIssues => RuntimeIssues.Count > 0;

    public string InterventionReason
    {
        get => _interventionReason;
        set => SetProperty(ref _interventionReason, value);
    }

    public string InterventionConfirmation
    {
        get => _interventionConfirmation;
        set
        {
            if (SetProperty(ref _interventionConfirmation, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public OperationalInterventionPreparation? InterventionPreparation => _interventions.Preparation;

    public OperationalInterventionResult? InterventionResult => _interventions.LastResult;

    public bool HasInterventionPreparation => InterventionPreparation is not null;

    public bool HasInterventionResult => InterventionResult is not null;

    public bool InterventionRequiresConfirmation =>
        InterventionPreparation?.RequiresTypedConfirmation == true;

    public string InterventionSummary => InterventionPreparation is null
        ? InterventionResult is null
            ? "No intervention prepared."
            : $"{OperationalInterventionRules.DisplayName(InterventionResult.Kind)} · {InterventionResult.Stage}"
        : $"{InterventionPreparation.DisplayName} · {InterventionPreparation.Stage}";

    public string InterventionDetail => InterventionPreparation is null
        ? InterventionResult?.Message ?? "Prepare an intervention against the currently supervised Logos target."
        : $"Authorization {InterventionPreparation.AuthorizationDecision} · readiness {InterventionPreparation.Readiness}.";

    public string InterventionExpiry => InterventionPreparation?.ExpiryText ?? string.Empty;

    public string InterventionConfirmationPrompt => InterventionPreparation is null ||
                                                    !InterventionPreparation.RequiresTypedConfirmation
        ? string.Empty
        : $"Type {InterventionPreparation.ConfirmationPhrase} exactly to confirm.";

    public IReadOnlyList<string> InterventionWarnings => InterventionPreparation?.Warnings ?? [];

    public IReadOnlyList<string> InterventionBlockers => InterventionPreparation?.Blockers ?? [];

    public bool HasInterventionWarnings => InterventionWarnings.Count > 0;

    public bool HasInterventionBlockers => InterventionBlockers.Count > 0;

    public ICommand InspectSelectionCommand { get; }

    public ICommand RefreshTargetCommand { get; }

    public ICommand StopWatchingCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand PreparePauseMissionCommand { get; }

    public ICommand PrepareResumeMissionCommand { get; }

    public ICommand PrepareEndMissionCommand { get; }

    public ICommand PrepareCancelMissionCommand { get; }

    public ICommand PrepareAbortMissionCommand { get; }

    public ICommand PrepareCancelTaskCommand { get; }

    public ICommand PrepareAbortTaskCommand { get; }

    public ICommand ExecuteInterventionCommand { get; }

    public ICommand DiscardInterventionCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _isInitialized = true;
        RefreshSelectionResolution();
        if (_targets.Current is { } request)
        {
            await BeginRequestAsync(request, cancellationToken);
        }
        else if (FollowSelection && SelectionResolution.Resolved)
        {
            await InspectSelectionAsync(cancellationToken);
        }
    }

    private async Task InspectSelectionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        RefreshSelectionResolution();
        var resolution = SelectionResolution;
        if (!resolution.Resolved || resolution.Target is null)
        {
            Status = resolution.Summary;
            Detail = resolution.Detail;
            return;
        }

        var request = _targets.Publish(
            resolution.Target,
            OperationalInspectionSource.Selection,
            resolution.Detail,
            openInspector: false);
        await BeginRequestAsync(request, cancellationToken);
    }

    private async Task RefreshTargetAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var target = CurrentTarget;
        if (target is null)
        {
            return;
        }

        var request = _targets.Publish(
            target,
            OperationalInspectionSource.Manual,
            "Operator refreshed the current Inspector target.",
            openInspector: false);
        await BeginRequestAsync(request, cancellationToken);
    }

    private async Task BeginRequestAsync(
        OperationalInspectionRequest request,
        CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeRequestSequence == request.Sequence && Snapshot.Target is not null)
            {
                return;
            }

            IsBusy = true;
            Status = "Opening authoritative execution supervision...";
            Detail = request.SourceDescription;
            InterventionConfirmation = string.Empty;
            await _interventions.DiscardAsync(cancellationToken: cancellationToken);
            await _supervision.BeginAsync(request.Target, cancellationToken);
            _activeRequestSequence = request.Sequence;
            Status = "Execution supervision active.";
            Detail = $"Following {request.Target.MissionId} / {request.Target.TaskId} through Logos watch APIs.";
            RaiseSnapshotProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Inspector target change cancelled.";
            Detail = "The previous supervision target was left unchanged where possible.";
        }
        catch (Exception ex)
        {
            Status = "Could not inspect the selected execution.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _startGate.Release();
        }
    }

    private async Task StopWatchingAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        IsBusy = true;
        try
        {
            await _supervision.StopAsync(cancellationToken);
            Status = "Execution watches stopped.";
            Detail = "The target remains selected and can be refreshed without resolving it again.";
        }
        finally
        {
            IsBusy = false;
            RaiseSnapshotProperties();
        }
    }

    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        IsBusy = true;
        try
        {
            await _interventions.DiscardAsync(cancellationToken: cancellationToken);
            await _supervision.ClearAsync(cancellationToken);
            _targets.Clear();
            _activeRequestSequence = 0;
            InterventionConfirmation = string.Empty;
            Status = "Inspector cleared.";
            Detail = "Select another projected mission, task, vehicle, or runtime to begin supervision.";
            RaiseSnapshotProperties();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AsyncRelayCommand InterventionCommand(OperationalInterventionKind kind)
        => new(
            token => PrepareInterventionAsync(kind, token),
            () => CanPrepareIntervention(kind));

    private async Task PrepareInterventionAsync(
        OperationalInterventionKind kind,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        IsBusy = true;
        try
        {
            var preparation = await _interventions.PrepareAsync(
                kind,
                InterventionReason,
                cancellationToken);
            InterventionConfirmation = string.Empty;
            Status = preparation.CanExecute
                ? $"{preparation.DisplayName} prepared."
                : $"{preparation.DisplayName} is blocked.";
            Detail = preparation.CanExecute
                ? "Review the authoritative target and confirmation requirements before executing."
                : string.Join(" ", preparation.Blockers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Could not prepare {OperationalInterventionRules.DisplayName(kind).ToLowerInvariant()}.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseInterventionProperties();
        }
    }

    private async Task ExecuteInterventionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var preparation = InterventionPreparation;
        if (preparation is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _interventions.ExecuteAsync(
                preparation.OperationId,
                InterventionConfirmation,
                cancellationToken);
            Status = result.Accepted
                ? $"{OperationalInterventionRules.DisplayName(result.Kind)} accepted by Logos."
                : $"{OperationalInterventionRules.DisplayName(result.Kind)} was not accepted.";
            Detail = result.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseInterventionProperties();
        }
    }

    private async Task DiscardInterventionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _interventions.DiscardAsync(InterventionPreparation?.OperationId, cancellationToken);
        InterventionConfirmation = string.Empty;
        Status = "Intervention preparation discarded.";
        Detail = "No mission or task lifecycle command was submitted.";
        RaiseInterventionProperties();
    }

    private bool CanInspectSelection()
        => !IsBusy && SelectionResolution.Resolved;

    private bool CanPrepareIntervention(OperationalInterventionKind kind)
        => !IsBusy &&
           InterventionPreparation is null &&
           OperationalInterventionRules.IsApplicable(kind, Snapshot, out _);

    private bool CanExecuteIntervention()
    {
        var preparation = InterventionPreparation;
        return !IsBusy &&
               preparation?.CanExecute == true &&
               (!preparation.RequiresTypedConfirmation ||
                string.Equals(
                    InterventionConfirmation.Trim(),
                    preparation.ConfirmationPhrase,
                    StringComparison.Ordinal));
    }

    private bool CanDiscardIntervention()
        => !IsBusy && InterventionPreparation is not null;

    private void RefreshSelectionResolution()
        => SelectionResolution = _resolver.Resolve(_selection.Current);

    private static bool IsInspectableSelection(OperationalSelection selection)
        => selection.Kind is
            SelectionKind.Mission or
            SelectionKind.Task or
            SelectionKind.Vehicle or
            SelectionKind.Runtime;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            RefreshSelectionResolution();
            if (FollowSelection && _isInitialized && SelectionResolution.Resolved)
            {
                _ = InspectSelectionAsync(CancellationToken.None);
            }
        });
    }

    private void OnTargetChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var request = _targets.Current;
        _ = _dispatcher.InvokeAsync(() =>
        {
            RaiseTargetProperties();
            if (_isInitialized && request is not null && request.Sequence != _activeRequestSequence)
            {
                _ = BeginRequestAsync(request, CancellationToken.None);
            }
        });
    }

    private void OnSupervisionChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = _dispatcher.InvokeAsync(RaiseSnapshotProperties);
        }
    }

    private void OnInterventionsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = _dispatcher.InvokeAsync(RaiseInterventionProperties);
        }
    }

    private void RaiseTargetProperties()
    {
        OnPropertyChanged(nameof(InspectionRequest));
        OnPropertyChanged(nameof(CurrentTarget));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(TargetIdentity));
        OnPropertyChanged(nameof(TargetSourceSummary));
        RaiseCommandStates();
    }

    private void RaiseSnapshotProperties()
    {
        OnPropertyChanged(nameof(Snapshot));
        RaiseTargetProperties();
        OnPropertyChanged(nameof(IsWatching));
        OnPropertyChanged(nameof(MissionSummary));
        OnPropertyChanged(nameof(MissionDetail));
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(TaskDetail));
        OnPropertyChanged(nameof(AutonomySummary));
        OnPropertyChanged(nameof(AutonomyDetail));
        OnPropertyChanged(nameof(WatchSummary));
        OnPropertyChanged(nameof(WatchDetail));
        OnPropertyChanged(nameof(TreeNodes));
        OnPropertyChanged(nameof(AttentionNodes));
        OnPropertyChanged(nameof(HasTreeNodes));
        OnPropertyChanged(nameof(HasAttentionNodes));
        OnPropertyChanged(nameof(TreeSummary));
        OnPropertyChanged(nameof(BlockingConditions));
        OnPropertyChanged(nameof(HasBlockingConditions));
        OnPropertyChanged(nameof(RuntimeIssues));
        OnPropertyChanged(nameof(HasRuntimeIssues));
        RaiseCommandStates();
    }

    private void RaiseInterventionProperties()
    {
        OnPropertyChanged(nameof(InterventionPreparation));
        OnPropertyChanged(nameof(InterventionResult));
        OnPropertyChanged(nameof(HasInterventionPreparation));
        OnPropertyChanged(nameof(HasInterventionResult));
        OnPropertyChanged(nameof(InterventionRequiresConfirmation));
        OnPropertyChanged(nameof(InterventionSummary));
        OnPropertyChanged(nameof(InterventionDetail));
        OnPropertyChanged(nameof(InterventionExpiry));
        OnPropertyChanged(nameof(InterventionConfirmationPrompt));
        OnPropertyChanged(nameof(InterventionWarnings));
        OnPropertyChanged(nameof(InterventionBlockers));
        OnPropertyChanged(nameof(HasInterventionWarnings));
        OnPropertyChanged(nameof(HasInterventionBlockers));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (InspectSelectionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshTargetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopWatchingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PreparePauseMissionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareResumeMissionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareEndMissionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareCancelMissionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareAbortMissionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareCancelTaskCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareAbortTaskCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ExecuteInterventionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiscardInterventionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnExpiryTimer(object? state)
    {
        if (Volatile.Read(ref _disposed) == 0 && InterventionPreparation is not null)
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(InterventionExpiry));
                RaiseCommandStates();
            });
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _targets.Changed -= OnTargetChanged;
        _selection.Changed -= OnSelectionChanged;
        _supervision.Changed -= OnSupervisionChanged;
        _interventions.Changed -= OnInterventionsChanged;
        _expiryTimer.Dispose();
        _startGate.Dispose();
    }
}
