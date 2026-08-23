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

public sealed class MissionsViewModel : ObservableObject
{
    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, MissionRecord> _missionsStore;
    private readonly IGeometryWorkspaceService? _geometryWorkspace;
    private readonly IMissionTaskWorkspaceService _workspace;
    private readonly IAutonomyWorkflow? _workflow;
    private readonly IReviewedOperationWorkflow? _reviewedOperations;
    private MissionRecord? _selectedMission;
    private ConnectionRecord? _selectedConnection;
    private string? _selectedGeometryId;
    private string? _candidateGeometryId;
    private string _importPath = "";
    private string _exportPath = "";
    private string _reason = "Operator request";
    private string _statusMessage = "Import a Logos mission document or select a mission projected from live events.";
    private string _validationDetails = "No validation has been run.";

    public MissionsViewModel(
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, OperationalCommandRecord> commands,
        ISelectionService selection,
        IMissionTaskWorkspaceService workspace,
        IGeometryWorkspaceService? geometryWorkspace = null,
        IAutonomyWorkflow? workflow = null,
        IReviewedOperationWorkflow? reviewedOperations = null)
    {
        _selection = selection;
        _missionsStore = missions;
        _workspace = workspace;
        _geometryWorkspace = geometryWorkspace;
        _workflow = workflow;
        _reviewedOperations = reviewedOperations;
        Missions = missions.Items;
        Connections = connections.Items;
        CommandHistory = commands.Items;
        AvailableGeometryIds = [];
        _selectedConnection = Connections.FirstOrDefault();

        ((INotifyCollectionChanged)Missions).CollectionChanged += OnCollectionChanged;
        ((INotifyCollectionChanged)Connections).CollectionChanged += OnConnectionsChanged;

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !string.IsNullOrWhiteSpace(ImportPath));
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedMission is not null);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => SelectedMission is not null);
        RefreshRemoteCommand = new AsyncRelayCommand(RefreshRemoteAsync, () => SelectedConnection is not null);
        StartCommand = CreateMissionCommand("Start");
        PauseCommand = CreateMissionCommand("Pause");
        ResumeCommand = CreateMissionCommand("Resume");
        EndCommand = CreateMissionCommand("End");
        CancelCommand = CreateMissionCommand("Cancel");
        AbortCommand = CreateMissionCommand("Abort", emergency: true);
        ShowGeometryCommand = new RelayCommand(_ => ShowGeometry(), _ => !string.IsNullOrWhiteSpace(SelectedGeometryId));
        AddGeometryReferenceCommand = new RelayCommand(_ => AddGeometryReference(), _ => CanAddGeometryReference());
        RemoveGeometryReferenceCommand = new RelayCommand(_ => RemoveGeometryReference(), _ => CanRemoveGeometryReference());
    }

    public ReadOnlyObservableCollection<MissionRecord> Missions { get; }

    public ReadOnlyObservableCollection<ConnectionRecord> Connections { get; }

    public ReadOnlyObservableCollection<OperationalCommandRecord> CommandHistory { get; }

    public ObservableCollection<string> AvailableGeometryIds { get; }

    public bool IsEmpty => Missions.Count == 0;

    public int Count => Missions.Count;

    public bool GatewayAvailable => _workspace.GatewayAvailable;

    public string GatewayStatus => _workspace.GatewayStatus;

    public MissionRecord? SelectedMission
    {
        get => _selectedMission;
        set
        {
            if (!SetProperty(ref _selectedMission, value))
            {
                return;
            }

            if (value is not null)
            {
                _selection.Select(SelectionFactory.From(value));
                ExportPath = value.SourcePath ?? $"{value.Id}.logos-mission.json";
                ValidationDetails = value.ValidationSummary;
                if (!string.IsNullOrWhiteSpace(value.ConnectionId))
                {
                    SelectedConnection = Connections.FirstOrDefault(item => item.Id == value.ConnectionId) ?? SelectedConnection;
                }
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


    public IReadOnlyList<string> SelectedGeometryIds => SelectedMission?.GeometryIds ?? [];

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

    public string ExportPath
    {
        get => _exportPath;
        set => SetProperty(ref _exportPath, value);
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ValidationDetails
    {
        get => _validationDetails;
        private set => SetProperty(ref _validationDetails, value);
    }

    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand RefreshRemoteCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AbortCommand { get; }
    public ICommand ShowGeometryCommand { get; }
    public ICommand AddGeometryReferenceCommand { get; }
    public ICommand RemoveGeometryReferenceCommand { get; }

    private AsyncRelayCommand CreateMissionCommand(string command, bool emergency = false)
        => new(
            token => ExecuteMissionCommandAsync(command, emergency, token),
            () => SelectedMission is not null && ResolveConnectionId() is not null);

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            MissionRecord mission;
            if (_workflow is null)
            {
                mission = await _workspace.ImportMissionAsync(ImportPath, cancellationToken);
            }
            else
            {
                var imported = await _workflow.ImportMissionAsync(ImportPath, cancellationToken);
                mission = Missions.FirstOrDefault(item => item.Id == imported.Id)
                    ?? throw new InvalidOperationException("Imported mission was not projected into the local library.");
            }
            SelectedMission = mission;
            StatusMessage = $"Imported mission '{mission.Name}'.";
            ValidationDetails = mission.ValidationSummary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (SelectedMission is null)
        {
            return;
        }

        try
        {
            var path = string.IsNullOrWhiteSpace(ExportPath)
                ? $"{SelectedMission.Id}.logos-mission.json"
                : ExportPath;
            if (_workflow is null) await _workspace.ExportMissionAsync(SelectedMission.Id, path, cancellationToken);
            else await _workflow.ExportMissionAsync(SelectedMission.Id, path, cancellationToken);
            ExportPath = path;
            StatusMessage = $"Exported mission to {Path.GetFullPath(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if (SelectedMission is null)
        {
            return;
        }

        try
        {
            if (_workflow is null)
            {
                var result = await _workspace.ValidateMissionAsync(SelectedMission.Id, cancellationToken);
                ValidationDetails = result.Issues.Count == 0 ? result.Summary : $"{result.Summary}{Environment.NewLine}{string.Join(Environment.NewLine, result.Issues)}";
                StatusMessage = result.Summary;
            }
            else
            {
                var findings = await _workflow.ValidateMissionAsync(SelectedMission.Id, cancellationToken);
                ValidationDetails = findings.Count == 0 ? "Mission is valid." : string.Join(Environment.NewLine, findings.Select(item => $"{item.Severity}: {item.Message}"));
                StatusMessage = findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking) ? "Mission validation failed." : "Mission validation completed.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Validation failed: {ex.Message}";
        }
    }

    private async Task RefreshRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null)
        {
            return;
        }

        if (_workflow is null) await _workspace.RefreshRemoteAsync(SelectedConnection.Id, cancellationToken);
        else await _workflow.RefreshAsync(SelectedConnection.Id, cancellationToken);
        RefreshAvailableGeometry();
        StatusMessage = _workspace.GatewayAvailable
            ? $"Refreshed missions from {SelectedConnection.Name}."
            : GatewayStatus;
    }

    private async Task ExecuteMissionCommandAsync(
        string command,
        bool emergency,
        CancellationToken cancellationToken)
    {
        if (SelectedMission is null || ResolveConnectionId() is not { } connectionId)
        {
            StatusMessage = "Select a mission and a Logos connection first.";
            return;
        }

        if (_workflow is null || _reviewedOperations is null)
        {
            var result = await _workspace.ExecuteMissionCommandAsync(new MissionCommandRequest(connectionId, SelectedMission.Id, command, SelectedMission.MissionExecutionId, Reason, emergency), cancellationToken);
            StatusMessage = result.Message;
            return;
        }

        var plan = await _workflow.PlanMissionCommandAsync(new(SelectedMission.Id, connectionId, command, Reason, emergency), cancellationToken);
        var execution = await _reviewedOperations.ExecuteAsync(plan.Id, cancellationToken);
        StatusMessage = execution.Summary;
    }

    private string? ResolveConnectionId()
        => SelectedMission?.ConnectionId ?? SelectedConnection?.Id;

    private void RefreshAvailableGeometry()
    {
        var connectionId = SelectedConnection?.Id ?? SelectedMission?.ConnectionId;
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
        => SelectedMission is { IsLocalDraft: true } &&
           !string.IsNullOrWhiteSpace(CandidateGeometryId) &&
           !(SelectedMission.GeometryIds ?? []).Contains(CandidateGeometryId, StringComparer.Ordinal);

    private void AddGeometryReference()
    {
        if (!CanAddGeometryReference() || SelectedMission is null) return;
        var updated = SelectedMission with
        {
            GeometryIds = (SelectedMission.GeometryIds ?? [])
                .Append(CandidateGeometryId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            ValidationState = PlanValidationState.NotValidated,
            ValidationSummary = "Geometry references changed; validate again."
        };
        _missionsStore.Upsert(updated);
        SelectedMission = updated;
        SelectedGeometryId = CandidateGeometryId;
        StatusMessage = $"Added geometry '{CandidateGeometryId}' to mission '{updated.Name}'.";
    }

    private bool CanRemoveGeometryReference()
        => SelectedMission is { IsLocalDraft: true } &&
           !string.IsNullOrWhiteSpace(SelectedGeometryId) &&
           (SelectedMission.GeometryIds ?? []).Contains(SelectedGeometryId, StringComparer.Ordinal);

    private void RemoveGeometryReference()
    {
        if (!CanRemoveGeometryReference() || SelectedMission is null) return;
        var removed = SelectedGeometryId!;
        var updated = SelectedMission with
        {
            GeometryIds = (SelectedMission.GeometryIds ?? [])
                .Where(item => !string.Equals(item, removed, StringComparison.Ordinal))
                .ToArray(),
            ValidationState = PlanValidationState.NotValidated,
            ValidationSummary = "Geometry references changed; validate again."
        };
        _missionsStore.Upsert(updated);
        SelectedMission = updated;
        StatusMessage = $"Removed geometry '{removed}' from mission '{updated.Name}'.";
    }

    private void ShowGeometry()
    {
        if (SelectedMission is null || string.IsNullOrWhiteSpace(SelectedGeometryId))
        {
            return;
        }

        _selection.Select(SelectionFactory.From(new GeometrySelectionContext(
            SelectedGeometryId,
            SelectedGeometryId,
            GeometryDocumentKind.Unknown,
            GeometryCoordinateFrame.GlobalWgs84,
            SelectedMission.ConnectionId,
            "Referenced by mission",
            "Unknown",
            $"Mission {SelectedMission.Id}")));
        StatusMessage = $"Selected geometry '{SelectedGeometryId}' on the operational map.";
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
        if (SelectedMission is not null)
        {
            var replacement = Missions.FirstOrDefault(item => item.Id == SelectedMission.Id);
            if (replacement is not null && !ReferenceEquals(replacement, SelectedMission))
            {
                _selectedMission = replacement;
                OnPropertyChanged(nameof(SelectedMission));
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

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectedConnection ??= Connections.FirstOrDefault();
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[]
        {
            ImportCommand, ExportCommand, ValidateCommand, RefreshRemoteCommand,
            StartCommand, PauseCommand, ResumeCommand, EndCommand, CancelCommand, AbortCommand
        }.OfType<AsyncRelayCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
        (ShowGeometryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddGeometryReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveGeometryReferenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
