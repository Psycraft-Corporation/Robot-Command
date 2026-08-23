using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class TaskBehaviourComposerViewModel : ObservableObject, IDisposable
{
    private readonly TasksViewModel _tasks;
    private readonly IEntityStore<string, OperationalTaskRecord> _taskStore;
    private readonly IBehaviourWorkspaceService _workspace;
    private readonly IBehaviourBindingWorkspaceService _bindings;
    private readonly IUiDispatcher _dispatcher;
    private readonly BehaviourParameterSchemaReader _schemaReader = new();
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _applyCommand;
    private readonly RelayCommand _resetCommand;

    private TaskBehaviourChoice? _selectedBehaviour;
    private string _status = "Select a local task draft, a Logos connection, and a vehicle, then load compatible behaviours.";
    private string _inventorySummary = "The installed behaviour inventory has not been loaded for this task.";
    private bool _busy;
    private int _selectionRevision;
    private int _disposed;

    public TaskBehaviourComposerViewModel(
        TasksViewModel tasks,
        IEntityStore<string, OperationalTaskRecord> taskStore,
        IBehaviourWorkspaceService workspace,
        IBehaviourBindingWorkspaceService bindings,
        IUiDispatcher dispatcher)
    {
        _tasks = tasks;
        _taskStore = taskStore;
        _workspace = workspace;
        _bindings = bindings;
        _dispatcher = dispatcher;

        Behaviours = [];
        Parameters = new BehaviourParameterFormViewModel();
        Parameters.Changed += OnParametersChanged;
        _tasks.PropertyChanged += OnTasksChanged;
        _workspace.Changed += OnWorkspaceChanged;
        _bindings.Changed += OnBindingsChanged;

        _refreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        _applyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        _resetCommand = new RelayCommand(_ => Parameters.Reset(), _ => !Busy && CurrentTask is not null);
    }

    public ObservableCollection<TaskBehaviourChoice> Behaviours { get; }

    public BehaviourParameterFormViewModel Parameters { get; }

    public OperationalTaskRecord? CurrentTask => _tasks.SelectedTask;

    public ConnectionRecord? CurrentConnection => _tasks.SelectedConnection;

    public VehicleRecord? CurrentVehicle => _tasks.SelectedVehicle;

    public bool HasTask => CurrentTask is not null;

    public bool Editable => CurrentTask is { IsLocalDraft: true };

    public string TaskSummary => CurrentTask is null
        ? "No task selected"
        : $"{CurrentTask.Name} · {CurrentTask.Id} · {(CurrentTask.IsLocalDraft ? "local draft" : "remote projection")}";

    public string TargetSummary => CurrentConnection is null
        ? "No Logos connection selected"
        : CurrentVehicle is null
            ? CurrentConnection.Name
            : $"{CurrentVehicle.Name} via {CurrentConnection.Name}";

    public TaskBehaviourChoice? SelectedBehaviour
    {
        get => _selectedBehaviour;
        set
        {
            if (!SetProperty(ref _selectedBehaviour, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedBehaviour));
            OnPropertyChanged(nameof(BehaviourSummary));
            OnPropertyChanged(nameof(BindingSummary));
            var revision = ++_selectionRevision;
            _ = LoadParameterSchemaAsync(value, revision);
            RaiseCommandStates();
        }
    }

    public bool HasSelectedBehaviour => SelectedBehaviour is not null;

    public string BehaviourSummary => SelectedBehaviour?.Summary
        ?? "Select an exact installed behaviour version.";

    public string BindingSummary => SelectedBehaviour?.Bindings.Summary
        ?? "Geometry binding readiness has not been inspected.";

    public string InventorySummary
    {
        get => _inventorySummary;
        private set => SetProperty(ref _inventorySummary, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand ApplyCommand => _applyCommand;

    public ICommand ResetParametersCommand => _resetCommand;

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var task = CurrentTask ?? throw new InvalidOperationException("Select a task first.");
        var connection = CurrentConnection
                         ?? throw new InvalidOperationException("Select a Logos connection first.");
        var previousKey = SelectedBehaviour?.Identity.Key;
        var target = CurrentVehicle is null
            ? null
            : BehaviourCompatibilityTarget.Create(
                CurrentVehicle.Id,
                CurrentVehicle.Name,
                CurrentVehicle.CapabilityKeys,
                CurrentVehicle.ProfileKey);

        Busy = true;
        try
        {
            var snapshot = await _workspace.RefreshAsync(
                connection.Id,
                target,
                refreshLocal: true,
                refreshRemote: true,
                cancellationToken: cancellationToken);
            var choices = new List<TaskBehaviourChoice>();
            if (snapshot.RemoteInventory.Available && !snapshot.RemoteInventory.Stale)
            {
                foreach (var entry in snapshot.Entries
                             .Where(item => item.Remote is not null)
                             .Where(item => !string.IsNullOrWhiteSpace(item.Identity.Version))
                             .Where(item => item.Compatible)
                             .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                             .ThenByDescending(item => item.Identity.Version, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var binding = await _bindings.InspectAsync(
                        connection.Id,
                        entry.Identity,
                        refreshPackages: false,
                        refreshGeometry: false,
                        cancellationToken: cancellationToken);
                    choices.Add(new TaskBehaviourChoice(entry, binding));
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                Behaviours.Clear();
                foreach (var choice in choices)
                {
                    Behaviours.Add(choice);
                }

                InventorySummary = snapshot.RemoteInventory.Available
                    ? snapshot.RemoteInventory.Stale
                        ? "The installed package inventory is stale and cannot be used to compose a task."
                        : $"Loaded {choices.Count} compatible installed behaviour version(s) for {TargetSummary}."
                    : snapshot.RemoteInventory.Summary;
                SelectedBehaviour = choices.FirstOrDefault(item => item.Identity.Key == previousKey)
                                    ?? choices.FirstOrDefault(item =>
                                        string.Equals(item.Identity.BehaviourId, task.BehaviourId, StringComparison.Ordinal) &&
                                        string.Equals(item.Identity.Version, task.BehaviourVersion, StringComparison.Ordinal))
                                    ?? choices.FirstOrDefault();
                Status = choices.Count == 0
                    ? "No compatible, installed behaviour versions are currently available for this task target."
                    : "Choose a behaviour, complete its parameter form, and apply it to the local task draft.";
                RaiseCommandStates();
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Behaviour discovery was cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Could not load behaviours: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task LoadParameterSchemaAsync(TaskBehaviourChoice? choice, int revision)
    {
        var existingJson = CurrentTask is { } task && choice is not null &&
                           string.Equals(task.BehaviourId, choice.Identity.BehaviourId, StringComparison.Ordinal) &&
                           string.Equals(task.BehaviourVersion, choice.Identity.Version, StringComparison.Ordinal)
            ? task.ParametersJson
            : "{}";
        BehaviourParameterSchema? schema = null;
        if (choice?.Package.Local is { } local)
        {
            schema = await _schemaReader.ReadAsync(local);
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (revision != _selectionRevision || !ReferenceEquals(choice, SelectedBehaviour))
            {
                return;
            }
            Parameters.Load(schema, existingJson);
            RaiseCommandStates();
        });
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var task = CurrentTask ?? throw new InvalidOperationException("Select a task first.");
        var choice = SelectedBehaviour ?? throw new InvalidOperationException("Select a behaviour first.");
        var parameters = Parameters.Build();
        if (!parameters.Valid)
        {
            Status = parameters.Summary;
            return;
        }

        Busy = true;
        try
        {
            var updated = TaskBehaviourComposition.Apply(task, choice, parameters.CanonicalJson);
            await _dispatcher.InvokeAsync(() =>
            {
                _taskStore.Upsert(updated);
                _tasks.SelectedTask = updated;
                Status = $"Applied behaviour '{choice.Identity.Key}' to task '{updated.Name}'. Validate the task before publishing or assigning it.";
                OnPropertyChanged(nameof(CurrentTask));
                OnPropertyChanged(nameof(TaskSummary));
                OnPropertyChanged(nameof(Editable));
                RaiseCommandStates();
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"Could not apply the behaviour: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private bool CanRefresh()
        => !Busy && CurrentTask is not null && CurrentConnection is not null;

    private bool CanApply()
        => !Busy &&
           Editable &&
           SelectedBehaviour?.Ready == true &&
           Parameters.IsValid;

    private void OnTasksChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TasksViewModel.SelectedTask) or
                                   nameof(TasksViewModel.SelectedConnection) or
                                   nameof(TasksViewModel.SelectedVehicle)))
        {
            return;
        }

        Behaviours.Clear();
        SelectedBehaviour = null;
        Parameters.Load(null, CurrentTask?.ParametersJson ?? "{}");
        InventorySummary = "Refresh compatible installed behaviours for the current task target.";
        Status = Editable
            ? "Load compatible behaviours, then apply one to the selected local task draft."
            : CurrentTask is null
                ? "Select a local task draft first."
                : "Remote task projections are read-only; create or import a local draft to compose it.";
        OnPropertyChanged(nameof(CurrentTask));
        OnPropertyChanged(nameof(CurrentConnection));
        OnPropertyChanged(nameof(CurrentVehicle));
        OnPropertyChanged(nameof(HasTask));
        OnPropertyChanged(nameof(Editable));
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(TargetSummary));
        RaiseCommandStates();
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
        => InventorySummary = "Behaviour package state changed. Refresh before applying a selection.";

    private void OnBindingsChanged(object? sender, EventArgs e)
        => InventorySummary = "Geometry binding state changed. Refresh before applying a selection.";

    private void OnParametersChanged(object? sender, EventArgs e)
        => RaiseCommandStates();

    private void RaiseCommandStates()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Parameters.Changed -= OnParametersChanged;
        _tasks.PropertyChanged -= OnTasksChanged;
        _workspace.Changed -= OnWorkspaceChanged;
        _bindings.Changed -= OnBindingsChanged;
    }
}
