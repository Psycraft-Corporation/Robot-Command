using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Missions;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class TasksViewModel : ObservableObject
{
    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasksStore;
    private readonly IGeometryWorkspaceService? _geometryWorkspace;
    private readonly IMissionTaskWorkspaceService _workspace;
    private readonly IAutonomyWorkflow? _workflow;
    private readonly IReviewedOperationWorkflow? _reviewedOperations;
    private OperationalTaskRecord? _selectedTask;
    private VehicleRecord? _selectedVehicle;
    private ConnectionRecord? _selectedConnection;
    private string? _selectedGeometryId;
    private string? _candidateGeometryId;
    private string _importPath = "";
    private string _exportPath = "";
    private string _reason = "Operator request";
    private string _statusMessage = "Import a Logos task document or select a task projected from live events.";
    private string _validationDetails = "No validation has been run.";

    public TasksViewModel(
        IEntityStore<string, OperationalTaskRecord> tasks,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, OperationalCommandRecord> commands,
        ISelectionService selection,
        IMissionTaskWorkspaceService workspace,
        IGeometryWorkspaceService? geometryWorkspace = null,
        IAutonomyWorkflow? workflow = null,
        IReviewedOperationWorkflow? reviewedOperations = null)
    {
        _selection = selection;
        _tasksStore = tasks;
        _workspace = workspace;
        _geometryWorkspace = geometryWorkspace;
        _workflow = workflow;
        _reviewedOperations = reviewedOperations;
        Tasks = tasks.Items;
        Vehicles = vehicles.Items;
        Connections = connections.Items;
        CommandHistory = commands.Items;
        AvailableGeometryIds = [];
        _selectedVehicle = Vehicles.FirstOrDefault();
        _selectedConnection = Connections.FirstOrDefault();

        ((INotifyCollectionChanged)Tasks).CollectionChanged += OnCollectionChanged;
        ((INotifyCollectionChanged)Vehicles).CollectionChanged += OnChoicesChanged;
        ((INotifyCollectionChanged)Connections).CollectionChanged += OnChoicesChanged;

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !string.IsNullOrWhiteSpace(ImportPath));
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedTask is not null);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => SelectedTask is not null);
        PlanAssignmentCommand = new AsyncRelayCommand(PlanAssignmentAsync, () => SelectedTask is not null && SelectedVehicle is not null);
        RefreshRemoteCommand = new AsyncRelayCommand(RefreshRemoteAsync, () => SelectedConnection is not null);
        AssignCommand = CreateTaskCommand("Assign");
        StartCommand = CreateTaskCommand("Start");
        CancelCommand = CreateTaskCommand("Cancel");
        AbortCommand = CreateTaskCommand("Abort", emergency: true);
        ShowGeometryCommand = new RelayCommand(_ => ShowGeometry(), _ => !string.IsNullOrWhiteSpace(SelectedGeometryId));
        AddGeometryReferenceCommand = new RelayCommand(_ => AddGeometryReference(), _ => CanAddGeometryReference());
        RemoveGeometryReferenceCommand = new RelayCommand(_ => RemoveGeometryReference(), _ => CanRemoveGeometryReference());
    }

    public ReadOnlyObservableCollection<OperationalTaskRecord> Tasks { get; }
    public ReadOnlyObservableCollection<VehicleRecord> Vehicles { get; }
    public ReadOnlyObservableCollection<ConnectionRecord> Connections { get; }
    public ReadOnlyObservableCollection<OperationalCommandRecord> CommandHistory { get; }
    public ObservableCollection<string> AvailableGeometryIds { get; }

    public bool IsEmpty => Tasks.Count == 0;
    public int Count => Tasks.Count;
    public bool GatewayAvailable => _workspace.GatewayAvailable;
    public string GatewayStatus => _workspace.GatewayStatus;

    public OperationalTaskRecord? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value))
            {
                return;
            }

            if (value is not null)
            {
                _selection.Select(SelectionFactory.From(value));
                ExportPath = value.SourcePath ?? $"{value.Id}.logos-task.json";
                ValidationDetails = value.ValidationSummary;
                SelectedVehicle = Vehicles.FirstOrDefault(item => item.Id == value.AssignedVehicleId) ?? SelectedVehicle;
                SelectedConnection = Connections.FirstOrDefault(item => item.Id == value.ConnectionId) ?? SelectedConnection;
                SelectedGeometryId = value.GeometryIds?.FirstOrDefault();
            }
            else
            {
                SelectedGeometryId = null;
            }

            OnPropertyChanged(nameof(SelectedGeometryIds));
            OnPropertyChanged(nameof(HasGeometryReferences));
            OnPropertyChanged(nameof(HasNoGeometryReferences));
            RefreshAvailableGeometry();
            RaiseCommandStates();
        }
    }

    public VehicleRecord? SelectedVehicle
    {
        get => _selectedVehicle;
        set
        {
            if (SetProperty(ref _selectedVehicle, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ConnectionRecord? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                RefreshAvailableGeometry();
                RaiseCommandStates();
            }
        }
    }


    public IReadOnlyList<string> SelectedGeometryIds => SelectedTask?.GeometryIds ?? [];

    public bool HasGeometryReferences => SelectedGeometryIds.Count > 0;

    public bool HasNoGeometryReferences => !HasGeometryReferences;

    public string? SelectedGeometryId
    {
        get => _selectedGeometryId;
        set
        {
            if (SetProperty(ref _selectedGeometryId, value))
            {
                (ShowGeometryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RemoveGeometryReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }


    public string? CandidateGeometryId
    {
        get => _candidateGeometryId;
        set
        {
            if (SetProperty(ref _candidateGeometryId, value))
            {
                (AddGeometryReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (SetProperty(ref _importPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ExportPath { get => _exportPath; set => SetProperty(ref _exportPath, value); }
    public string Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ValidationDetails { get => _validationDetails; private set => SetProperty(ref _validationDetails, value); }

    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand PlanAssignmentCommand { get; }
    public ICommand RefreshRemoteCommand { get; }
    public ICommand AssignCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AbortCommand { get; }
    public ICommand ShowGeometryCommand { get; }
    public ICommand AddGeometryReferenceCommand { get; }
    public ICommand RemoveGeometryReferenceCommand { get; }

    private AsyncRelayCommand CreateTaskCommand(string command, bool emergency = false)
        => new(token => ExecuteTaskCommandAsync(command, emergency, token), () => SelectedTask is not null && ResolveConnectionId() is not null);

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            OperationalTaskRecord task;
            if (_workflow is null)
            {
                task = await _workspace.ImportTaskAsync(ImportPath, cancellationToken);
            }
            else
            {
                var imported = await _workflow.ImportTaskAsync(ImportPath, cancellationToken);
                task = Tasks.FirstOrDefault(item => item.Id == imported.Id)
                    ?? throw new InvalidOperationException("Imported task was not projected into the local library.");
            }
            SelectedTask = task;
            StatusMessage = $"Imported task '{task.Name}'.";
            ValidationDetails = task.ValidationSummary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (SelectedTask is null) return;
        try
        {
            var path = string.IsNullOrWhiteSpace(ExportPath) ? $"{SelectedTask.Id}.logos-task.json" : ExportPath;
            if (_workflow is null) await _workspace.ExportTaskAsync(SelectedTask.Id, path, cancellationToken);
            else await _workflow.ExportTaskAsync(SelectedTask.Id, path, cancellationToken);
            ExportPath = path;
            StatusMessage = $"Exported task to {Path.GetFullPath(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if (SelectedTask is null) return;
        try
        {
            if (_workflow is null)
            {
                var result = await _workspace.ValidateTaskAsync(SelectedTask.Id, cancellationToken);
                ValidationDetails = result.Issues.Count == 0 ? result.Summary : $"{result.Summary}{Environment.NewLine}{string.Join(Environment.NewLine, result.Issues)}";
                StatusMessage = result.Summary;
            }
            else
            {
                var findings = await _workflow.ValidateTaskAsync(SelectedTask.Id, cancellationToken);
                ValidationDetails = findings.Count == 0 ? "Task is valid." : string.Join(Environment.NewLine, findings.Select(item => $"{item.Severity}: {item.Message}"));
                StatusMessage = findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking) ? "Task validation failed." : "Task validation completed.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Validation failed: {ex.Message}";
        }
    }

    private async Task PlanAssignmentAsync(CancellationToken cancellationToken)
    {
        if (SelectedTask is null || SelectedVehicle is null) return;
        if (_workflow is null || _reviewedOperations is null)
        {
            await _workspace.PlanTaskAssignmentAsync(SelectedTask.Id, SelectedVehicle.Id, cancellationToken);
            StatusMessage = $"Planned assignment of {SelectedTask.Name} to {SelectedVehicle.Name}.";
            return;
        }
        var plan = await _workflow.PlanTaskAssignmentAsync(new(SelectedTask.Id, SelectedVehicle.Id), cancellationToken);
        var result = await _reviewedOperations.ExecuteAsync(plan.Id, cancellationToken);
        StatusMessage = result.Summary;
    }

    private async Task RefreshRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null) return;
        if (_workflow is null) await _workspace.RefreshRemoteAsync(SelectedConnection.Id, cancellationToken);
        else await _workflow.RefreshAsync(SelectedConnection.Id, cancellationToken);
        RefreshAvailableGeometry();
        StatusMessage = _workspace.GatewayAvailable ? $"Refreshed tasks from {SelectedConnection.Name}." : GatewayStatus;
    }

    private async Task ExecuteTaskCommandAsync(string command, bool emergency, CancellationToken cancellationToken)
    {
        if (SelectedTask is null || ResolveConnectionId() is not { } connectionId)
        {
            StatusMessage = "Select a task and a Logos connection first.";
            return;
        }

        if (_workflow is null || _reviewedOperations is null)
        {
            var vehicle = SelectedVehicle;
            var result = await _workspace.ExecuteTaskCommandAsync(new TaskCommandRequest(connectionId, SelectedTask.Id, command, SelectedTask.TaskExecutionId, vehicle?.Id ?? SelectedTask.AssignedVehicleId, SelectedTask.AssignedMemberId, vehicle?.LogosInstanceId ?? SelectedTask.AssignedLogosInstanceId, Reason, emergency), cancellationToken);
            StatusMessage = result.Message;
            return;
        }
        var plan = await _workflow.PlanTaskCommandAsync(new(SelectedTask.Id, connectionId, command, Reason, emergency), cancellationToken);
        var execution = await _reviewedOperations.ExecuteAsync(plan.Id, cancellationToken);
        StatusMessage = execution.Summary;
    }

    private string? ResolveConnectionId()
        => SelectedTask?.ConnectionId
            ?? SelectedVehicle?.ConnectionIds.FirstOrDefault()
            ?? SelectedConnection?.Id;

    private void RefreshAvailableGeometry()
    {
        var connectionId = SelectedConnection?.Id ?? SelectedTask?.ConnectionId;
        var selected = CandidateGeometryId;
        AvailableGeometryIds.Clear();
        if (_geometryWorkspace is not null && !string.IsNullOrWhiteSpace(connectionId))
        {
            foreach (var id in _geometryWorkspace.RemoteRecords
                         .Where(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
                         .Select(item => item.GeometryId)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                AvailableGeometryIds.Add(id);
            }
        }

        CandidateGeometryId = selected is not null && AvailableGeometryIds.Contains(selected)
            ? selected
            : AvailableGeometryIds.FirstOrDefault();
    }

    private bool CanAddGeometryReference()
        => SelectedTask is { IsLocalDraft: true } &&
           !string.IsNullOrWhiteSpace(CandidateGeometryId) &&
           !(SelectedTask.GeometryIds ?? []).Contains(CandidateGeometryId, StringComparer.Ordinal);

    private void AddGeometryReference()
    {
        if (!CanAddGeometryReference() || SelectedTask is null) return;
        var updated = SelectedTask with
        {
            GeometryIds = (SelectedTask.GeometryIds ?? [])
                .Append(CandidateGeometryId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            ValidationState = PlanValidationState.NotValidated,
            ValidationSummary = "Geometry references changed; validate again."
        };
        _tasksStore.Upsert(updated);
        SelectedTask = updated;
        SelectedGeometryId = CandidateGeometryId;
        StatusMessage = $"Added geometry '{CandidateGeometryId}' to task '{updated.Name}'.";
    }

    private bool CanRemoveGeometryReference()
        => SelectedTask is { IsLocalDraft: true } &&
           !string.IsNullOrWhiteSpace(SelectedGeometryId) &&
           (SelectedTask.GeometryIds ?? []).Contains(SelectedGeometryId, StringComparer.Ordinal);

    private void RemoveGeometryReference()
    {
        if (!CanRemoveGeometryReference() || SelectedTask is null) return;
        var removed = SelectedGeometryId!;
        var updated = SelectedTask with
        {
            GeometryIds = (SelectedTask.GeometryIds ?? [])
                .Where(item => !string.Equals(item, removed, StringComparison.Ordinal))
                .ToArray(),
            ValidationState = PlanValidationState.NotValidated,
            ValidationSummary = "Geometry references changed; validate again."
        };
        _tasksStore.Upsert(updated);
        SelectedTask = updated;
        StatusMessage = $"Removed geometry '{removed}' from task '{updated.Name}'.";
    }

    private void ShowGeometry()
    {
        if (SelectedTask is null || string.IsNullOrWhiteSpace(SelectedGeometryId))
        {
            return;
        }

        _selection.Select(SelectionFactory.From(new GeometrySelectionContext(
            SelectedGeometryId,
            SelectedGeometryId,
            GeometryDocumentKind.Unknown,
            GeometryCoordinateFrame.GlobalWgs84,
            SelectedTask.ConnectionId,
            "Referenced by task",
            "Unknown",
            $"Task {SelectedTask.Id}")));
        StatusMessage = $"Selected geometry '{SelectedGeometryId}' on the operational map.";
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
        if (SelectedTask is not null)
        {
            var replacement = Tasks.FirstOrDefault(item => item.Id == SelectedTask.Id);
            if (replacement is not null && !ReferenceEquals(replacement, SelectedTask))
            {
                _selectedTask = replacement;
                OnPropertyChanged(nameof(SelectedTask));
                _selection.Select(SelectionFactory.From(replacement));
                OnPropertyChanged(nameof(SelectedGeometryIds));
                OnPropertyChanged(nameof(HasGeometryReferences));
                OnPropertyChanged(nameof(HasNoGeometryReferences));
                SelectedGeometryId = replacement.GeometryIds?.Contains(SelectedGeometryId ?? string.Empty, StringComparer.Ordinal) == true
                    ? SelectedGeometryId
                    : replacement.GeometryIds?.FirstOrDefault();
            }
        }
    }

    private void OnChoicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectedVehicle ??= Vehicles.FirstOrDefault();
        SelectedConnection ??= Connections.FirstOrDefault();
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[]
        {
            ImportCommand, ExportCommand, ValidateCommand, PlanAssignmentCommand,
            RefreshRemoteCommand, AssignCommand, StartCommand, CancelCommand, AbortCommand
        }.OfType<AsyncRelayCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
        (ShowGeometryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddGeometryReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveGeometryReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
