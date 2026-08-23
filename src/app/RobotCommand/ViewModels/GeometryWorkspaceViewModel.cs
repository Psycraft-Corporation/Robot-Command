using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Geometry;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed partial class GeometryWorkspaceViewModel : ObservableObject
{
    private readonly IGeometryWorkspaceService _workspace;
    private readonly GeometryDocumentValidator _validator;
    private readonly IGeometryEditSession _editSession;
    private readonly IUiDispatcher _dispatcher;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _importCommand;
    private readonly AsyncRelayCommand _exportCommand;
    private readonly AsyncRelayCommand _removeLocalCommand;
    private readonly AsyncRelayCommand _pullCommand;
    private readonly AsyncRelayCommand _createRemoteCommand;
    private readonly AsyncRelayCommand _updateRemoteCommand;
    private readonly AsyncRelayCommand _deleteRemoteCommand;
    private readonly AsyncRelayCommand _assessRemoteCommand;
    private readonly AsyncRelayCommand _duplicateLocalCommand;
    private readonly AsyncRelayCommand _pullRemoteCopyCommand;
    private readonly RelayCommand _editOnMapCommand;
    private readonly AsyncRelayCommand _saveMapDraftCommand;
    private readonly RelayCommand _discardMapDraftCommand;
    private ConnectionRecord? _selectedConnection;
    private string? _selectedConnectionId;
    private GeometryLibraryItemViewModel? _selectedLocal;
    private string? _selectedLocalId;
    private GeometryRemoteItemViewModel? _selectedRemote;
    private string? _selectedRemoteId;
    private string _searchText = string.Empty;
    private string _selectedKindFilter = "All";
    private string _selectedDeploymentFilter = "All";
    private string _importPath = string.Empty;
    private string _exportPath = string.Empty;
    private bool _allowReplaceImport;
    private bool _replaceLocalOnPull;
    private string _remoteDeleteConfirmation = string.Empty;
    private string _statusMessage = "Create or import local geometry, then compare it with a Logos registry.";

    public GeometryWorkspaceViewModel(
        IGeometryWorkspaceService workspace,
        IEntityStore<string, ConnectionRecord> connections,
        GeometryDocumentValidator validator,
        IGeometryEditSession editSession,
        IUiDispatcher dispatcher,
        IBehaviourBindingWorkspaceService? behaviourBindingWorkspace = null,
        ISelectionService? selection = null)
    {
        _workspace = workspace;
        _validator = validator;
        _editSession = editSession;
        _dispatcher = dispatcher;
        Connections = connections.Items;
        LocalItems = [];
        RemoteItems = [];
        KindFilters = ["All", "PointOfInterest", "WaypointSequence", "Zone"];
        DeploymentFilters =
        [
            "All",
            GeometryDeploymentStatus.LocalOnly.ToString(),
            GeometryDeploymentStatus.RemoteOnly.ToString(),
            GeometryDeploymentStatus.Matching.ToString(),
            GeometryDeploymentStatus.LocalModified.ToString(),
            GeometryDeploymentStatus.RemoteModified.ToString(),
            GeometryDeploymentStatus.Conflict.ToString(),
            GeometryDeploymentStatus.MissingRemote.ToString(),
            GeometryDeploymentStatus.Invalid.ToString()
        ];

        _selectedConnection = Connections
            .OrderByDescending(item => item.State == AvailabilityState.Online)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        _selectedConnectionId = _selectedConnection?.Id;

        NewPointCommand = new AsyncRelayCommand(
            token => CreateDraftAsync(GeometryDocumentKind.PointOfInterest, token));
        NewRouteCommand = new AsyncRelayCommand(
            token => CreateDraftAsync(GeometryDocumentKind.WaypointSequence, token));
        NewZoneCommand = new AsyncRelayCommand(
            token => CreateDraftAsync(GeometryDocumentKind.Zone, token));
        _refreshCommand = new AsyncRelayCommand(RefreshAsync, () => SelectedConnection is not null);
        _importCommand = new AsyncRelayCommand(ImportAsync, () => !string.IsNullOrWhiteSpace(ImportPath));
        _exportCommand = new AsyncRelayCommand(
            ExportAsync,
            () => SelectedLocal is not null && !string.IsNullOrWhiteSpace(ExportPath));
        _removeLocalCommand = new AsyncRelayCommand(RemoveLocalAsync, () => SelectedLocal is not null);
        _pullCommand = new AsyncRelayCommand(PullAsync, CanPull);
        _createRemoteCommand = new AsyncRelayCommand(CreateRemoteAsync, CanCreateRemote);
        _updateRemoteCommand = new AsyncRelayCommand(UpdateRemoteAsync, CanUpdateRemote);
        _deleteRemoteCommand = new AsyncRelayCommand(DeleteRemoteAsync, CanDeleteRemote);
        _assessRemoteCommand = new AsyncRelayCommand(AssessRemoteAsync, CanAssessRemote);
        _duplicateLocalCommand = new AsyncRelayCommand(DuplicateLocalForConflictAsync, CanDuplicateLocalForConflict);
        _pullRemoteCopyCommand = new AsyncRelayCommand(PullRemoteCopyAsync, CanPullRemoteCopy);
        _editOnMapCommand = new RelayCommand(_ => BeginMapEdit(), _ => CanBeginMapEdit());
        _saveMapDraftCommand = new AsyncRelayCommand(SaveMapDraftAsync, () => MapEdit.IsCompleted);
        _discardMapDraftCommand = new RelayCommand(_ => DiscardMapDraft(), _ => MapEdit.HasDraft);

        RefreshCommand = _refreshCommand;
        ImportCommand = _importCommand;
        ExportCommand = _exportCommand;
        RemoveLocalCommand = _removeLocalCommand;
        PullCommand = _pullCommand;
        CreateRemoteCommand = _createRemoteCommand;
        UpdateRemoteCommand = _updateRemoteCommand;
        DeleteRemoteCommand = _deleteRemoteCommand;
        AssessRemoteCommand = _assessRemoteCommand;
        DuplicateLocalCommand = _duplicateLocalCommand;
        PullRemoteCopyCommand = _pullRemoteCopyCommand;
        EditOnMapCommand = _editOnMapCommand;
        SaveMapDraftCommand = _saveMapDraftCommand;
        DiscardMapDraftCommand = _discardMapDraftCommand;
        InitializeAuthoring();
        InitializeBindings(behaviourBindingWorkspace, selection);

        _workspace.Changed += OnWorkspaceChanged;
        _editSession.Changed += OnEditSessionChanged;
        ((INotifyCollectionChanged)Connections).CollectionChanged += OnConnectionsChanged;
        RefreshPresentation();
    }

    public ReadOnlyObservableCollection<ConnectionRecord> Connections { get; }

    public ObservableCollection<GeometryLibraryItemViewModel> LocalItems { get; }

    public ObservableCollection<GeometryRemoteItemViewModel> RemoteItems { get; }

    public IReadOnlyList<string> KindFilters { get; }

    public IReadOnlyList<string> DeploymentFilters { get; }

    public string LocalLibraryPath => _workspace.LocalLibraryPath;

    public bool GatewayAvailable => _workspace.GatewayAvailable;

    public string GatewayStatus => _workspace.GatewayStatus;

    public ConnectionRecord? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (value is null && _selectedConnectionId is not null)
            {
                value = Connections.FirstOrDefault(item => item.Id == _selectedConnectionId);
            }

            if (!SetProperty(ref _selectedConnection, value))
            {
                return;
            }

            _selectedConnectionId = value?.Id;
            _selectedRemoteId = null;
            _selectedRemote = null;
            RemoteDeleteConfirmation = string.Empty;
            ClearRemoteAssessment();
            ClearBindingPresentation();
            RefreshPresentation();
        }
    }

    public GeometryLibraryItemViewModel? SelectedLocal
    {
        get => _selectedLocal;
        set
        {
            var activeEditId = MapEdit.Draft?.GeometryId;
            if (MapEdit.HasDraft &&
                value is not null &&
                !string.Equals(value.GeometryId, activeEditId, StringComparison.Ordinal))
            {
                value = LocalItems.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, activeEditId, StringComparison.Ordinal));
                StatusMessage = "Save or discard the active map draft before selecting different local geometry.";
            }

            if (value is null && _selectedLocalId is not null)
            {
                value = LocalItems.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, _selectedLocalId, StringComparison.Ordinal));
                if (value is null)
                {
                    return;
                }
            }

            if (!SetProperty(ref _selectedLocal, value))
            {
                return;
            }

            _selectedLocalId = value?.GeometryId;
            ConflictCopyId = value is null ? string.Empty : $"{value.GeometryId}-copy";
            ClearRemoteAssessment();
            if (value is not null)
            {
                ExportPath = $"{value.GeometryId}.logos-geometry.json";
                var matchingRemote = RemoteItems.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, value.GeometryId, StringComparison.Ordinal));
                SetSelectedRemote(matchingRemote);
            }
            else
            {
                SetSelectedRemote(null);
            }

            LoadAuthoringSelection(value?.Document, force: true);
            RaiseSelectionProperties();
            PublishGeometrySelection();
        }
    }

    public GeometryRemoteItemViewModel? SelectedRemote
    {
        get => _selectedRemote;
        set
        {
            if (value is null && _selectedRemoteId is not null)
            {
                value = RemoteItems.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, _selectedRemoteId, StringComparison.Ordinal));
                if (value is null)
                {
                    return;
                }
            }

            if (!SetProperty(ref _selectedRemote, value))
            {
                return;
            }

            _selectedRemoteId = value?.GeometryId;
            if (value is not null && SelectedLocal is null)
            {
                ConflictCopyId = $"{value.GeometryId}-remote-copy";
            }
            RemoteDeleteConfirmation = string.Empty;
            ClearRemoteAssessment();
            var matchingLocal = value is null
                ? null
                : LocalItems.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, value.GeometryId, StringComparison.Ordinal));
            var activeEditId = MapEdit.Draft?.GeometryId;
            var wouldReplaceActiveEdit = MapEdit.HasDraft &&
                                         matchingLocal is not null &&
                                         !string.Equals(matchingLocal.GeometryId, activeEditId, StringComparison.Ordinal);
            if (!ReferenceEquals(matchingLocal, SelectedLocal) && !wouldReplaceActiveEdit)
            {
                _selectedLocal = matchingLocal;
                _selectedLocalId = matchingLocal?.GeometryId;
                OnPropertyChanged(nameof(SelectedLocal));
                LoadAuthoringSelection(matchingLocal?.Document, force: true);
            }
            else if (wouldReplaceActiveEdit)
            {
                StatusMessage = "The remote selection changed, but the active local map draft remains selected until it is saved or discarded.";
            }

            RaiseSelectionProperties();
            PublishGeometrySelection();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RefreshPresentation();
            }
        }
    }

    public string SelectedKindFilter
    {
        get => _selectedKindFilter;
        set
        {
            if (SetProperty(ref _selectedKindFilter, value ?? "All"))
            {
                RefreshPresentation();
            }
        }
    }

    public string SelectedDeploymentFilter
    {
        get => _selectedDeploymentFilter;
        set
        {
            if (SetProperty(ref _selectedDeploymentFilter, value ?? "All"))
            {
                RefreshPresentation();
            }
        }
    }

    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (SetProperty(ref _importPath, value ?? string.Empty))
            {
                _importCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ExportPath
    {
        get => _exportPath;
        set
        {
            if (SetProperty(ref _exportPath, value ?? string.Empty))
            {
                _exportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AllowReplaceImport
    {
        get => _allowReplaceImport;
        set => SetProperty(ref _allowReplaceImport, value);
    }

    public bool ReplaceLocalOnPull
    {
        get => _replaceLocalOnPull;
        set
        {
            if (SetProperty(ref _replaceLocalOnPull, value))
            {
                _pullCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RemoteDeleteConfirmation
    {
        get => _remoteDeleteConfirmation;
        set
        {
            if (SetProperty(ref _remoteDeleteConfirmation, value ?? string.Empty))
            {
                _deleteRemoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RequiredDeleteConfirmation => SelectedRemote is null
        ? "Select remote geometry"
        : $"DELETE {SelectedRemote.GeometryId}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int LocalCount => LocalItems.Count;

    public int RemoteCount => RemoteItems.Count;

    public int LibraryIssueCount => _workspace.LibraryIssues.Count;

    public string LibraryIssueSummary => $"Library issues: {LibraryIssueCount}";

    public string DeleteConfirmationPrompt => $"Type: {RequiredDeleteConfirmation}";

    public bool HasSelectedLocal => SelectedLocal is not null;

    public bool HasSelectedRemote => SelectedRemote is not null;

    public string SelectedTitle => SelectedLocal?.DisplayName
                                   ?? SelectedRemote?.DisplayName
                                   ?? "No geometry selected";

    public string SelectedSubtitle => SelectedLocal?.StatusSummary
                                      ?? SelectedRemote?.StatusSummary
                                      ?? "Select local or remote geometry to inspect it.";

    public string SelectedDetails
    {
        get
        {
            if (SelectedLocal is { } local)
            {
                var document = local.Document;
                return string.Join(Environment.NewLine,
                    $"Geometry ID: {document.GeometryId}",
                    $"Kind: {document.Kind}",
                    $"Frame: {document.Frame}",
                    $"Shape: {local.ShapeSummary}",
                    $"Origin: {document.Origin}",
                    $"Content SHA-256: {document.ContentSha256}",
                    $"Source connection: {document.SourceConnectionId ?? "None"}",
                    $"Source revision: {document.SourceRevision ?? "None"}",
                    $"Policy: {document.Policy.Kind} / {document.Policy.Constraint} / {document.Policy.Decision}",
                    $"Altitude band: {AltitudeBand(document.Policy)}",
                    $"Tags: {(document.Policy.Tags.Count == 0 ? "None" : string.Join(", ", document.Policy.Tags))}",
                    $"Updated: {document.UpdatedAt.ToLocalTime():g}",
                    string.IsNullOrWhiteSpace(document.Description)
                        ? "Description: None"
                        : $"Description: {document.Description}");
            }

            if (SelectedRemote is { } remote)
            {
                var record = remote.Record;
                return string.Join(Environment.NewLine,
                    $"Geometry ID: {record.GeometryId}",
                    $"Kind: {record.Kind}",
                    $"Frame: {record.Frame}",
                    $"Shape: {remote.ShapeSummary}",
                    $"Revision: {record.Revision ?? "Not reported"}",
                    $"SHA-256: {record.Sha256}",
                    $"Size: {record.SizeBytes:N0} bytes",
                     $"Updated: {record.UpdatedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Not reported"}");
            }

            return "-";
        }
    }

    public string ValidationDetails
    {
        get
        {
            if (SelectedLocal is null)
            {
                return "Select a local geometry document to inspect structural validation.";
            }

            var validation = SelectedLocal.Validation;
            return validation.Issues.Count == 0
                ? validation.Summary
                : $"{validation.Summary}{Environment.NewLine}{string.Join(Environment.NewLine, validation.Issues.Select(item => $"{item.Code}: {item.Message}"))}";
        }
    }

    public string DeploymentDetails => SelectedLocal?.Deployment?.Detail
                                       ?? SelectedRemote?.Deployment?.Detail
                                       ?? "No deployment comparison is available for the selected connection.";

    public GeometryEditSnapshot MapEdit => _editSession.Snapshot;

    public bool MapEditActive => MapEdit.HasDraft;

    public bool MapEditCompleted => MapEdit.IsCompleted;

    public string MapEditSummary => MapEdit.HasDraft
        ? $"{MapEdit.InteractionMode} · {MapEdit.Status}"
        : "Select local WGS84 geometry and choose Edit on map.";

    public string MapEditActionLabel => MapEdit.IsCompleted
        ? "Draft ready to save"
        : MapEdit.IsEditing
            ? "Editing in Operate"
            : "No map edit";

    public GeometryRegistryWatchState? SelectedRegistryWatch => SelectedConnection is null
        ? null
        : _workspace.RegistryWatchStates.FirstOrDefault(item =>
            string.Equals(item.ConnectionId, SelectedConnection.Id, StringComparison.Ordinal));

    public string RegistryWatchSummary => SelectedRegistryWatch is null
        ? "Live watch has not started."
        : $"{SelectedRegistryWatch.Status} · {SelectedRegistryWatch.Summary}";

    public string RegistryWatchDetails
    {
        get
        {
            var watch = SelectedRegistryWatch;
            if (watch is null)
            {
                return "Connect to a Logos runtime with GeometryService support to start registry supervision.";
            }

            var lastMessage = watch.LastMessageAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "No message received";
            var restart = watch.RestartCount == 0 ? "No restarts" : $"{watch.RestartCount} restart(s)";
            return string.IsNullOrWhiteSpace(watch.LastError)
                ? $"Last message: {lastMessage} · {restart}"
                : $"Last message: {lastMessage} · {restart} · {watch.LastError}";
        }
    }

    public string RegistrySummary
    {
        get
        {
            if (SelectedConnection is null)
            {
                return "Select a Logos connection.";
            }

            var status = _workspace.RegistrySnapshots.FirstOrDefault(item =>
                string.Equals(item.ConnectionId, SelectedConnection.Id, StringComparison.Ordinal));
            var registry = status is null
                ? "Registry inventory is not authoritative yet."
                : $"{status.State} · {status.Health} · {status.Readiness} · {status.ObjectCount} object(s) · {status.Message}";
            return $"{RegistryWatchSummary}{Environment.NewLine}{registry}";
        }
    }

    public ICommand NewPointCommand { get; }

    public ICommand NewRouteCommand { get; }

    public ICommand NewZoneCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand RemoveLocalCommand { get; }

    public ICommand PullCommand { get; }

    public ICommand CreateRemoteCommand { get; }

    public ICommand UpdateRemoteCommand { get; }

    public ICommand DeleteRemoteCommand { get; }

    public ICommand AssessRemoteCommand { get; }

    public ICommand DuplicateLocalCommand { get; }

    public ICommand PullRemoteCopyCommand { get; }

    public ICommand EditOnMapCommand { get; }

    public ICommand SaveMapDraftCommand { get; }

    public ICommand DiscardMapDraftCommand { get; }

    private void BeginMapEdit()
    {
        try
        {
            var document = CurrentAuthoringDocumentRequired();
            if (GeometryEditShape.GetVertices(document).Count == 0)
            {
                _editSession.BeginCreate(document);
            }
            else
            {
                _editSession.BeginEdit(document);
            }

            StatusMessage = $"Map editing started for '{document.DisplayName}'. Open Operate to place or drag vertices.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start map editing: {ex.Message}";
        }
    }

    private bool CanBeginMapEdit()
        => _authoringDocument?.Frame == GeometryCoordinateFrame.GlobalWgs84 &&
           !(_authoringDocument.Kind == GeometryDocumentKind.Zone && _authoringDocument.Rings.Count > 1) &&
           _editSession.Snapshot.Stage == GeometryEditSessionStage.Idle;

    private async Task SaveMapDraftAsync(CancellationToken cancellationToken)
    {
        var draft = _editSession.Snapshot.Draft;
        if (!_editSession.Snapshot.IsCompleted || draft is null)
        {
            return;
        }

        try
        {
            var merged = GeometryEditShape.NormalizeCompleted(MergeAuthoringWithMapDraft(draft));
            var validation = _validator.Validate(merged, requireAuthorableFrame: true);
            if (!validation.IsValid)
            {
                StatusMessage = $"Cannot save the map draft: {validation.Summary}";
                return;
            }

            var saved = await _workspace.SaveLocalAsync(merged, cancellationToken);
            _selectedLocalId = saved.GeometryId;
            _authoringDirty = false;
            _authoringDocument = saved;
            _editSession.Clear();
            RefreshPresentation();
            StatusMessage = $"Saved the completed map draft for '{saved.DisplayName}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not save the map draft: {ex.Message}";
        }
    }

    private void DiscardMapDraft()
    {
        var name = _editSession.Snapshot.Draft?.DisplayName ?? "geometry";
        _editSession.Cancel();
        StatusMessage = $"Discarded the in-memory map edit for '{name}'. The saved local document was not changed.";
    }

    private async Task CreateDraftAsync(
        GeometryDocumentKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await _workspace.CreateLocalDraftAsync(kind, cancellationToken: cancellationToken);
            _selectedLocalId = document.GeometryId;
            RefreshPresentation();
            StatusMessage = $"Created local {kind} draft '{document.DisplayName}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not create geometry draft: {ex.Message}";
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null)
        {
            return;
        }

        try
        {
            await _workspace.RefreshAsync(SelectedConnection.Id, cancellationToken);
            RefreshBindingGeometryOptions();
            if (SelectedBehaviourPackage is not null)
            {
                await LoadSelectedBehaviourBindingsAsync(SelectedBehaviourPackage, cancellationToken);
            }
            StatusMessage = $"Refreshed local geometry and the {SelectedConnection.Name} registry.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Geometry refresh failed: {ex.Message}";
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await _workspace.ImportLocalAsync(
                ImportPath,
                AllowReplaceImport,
                cancellationToken);
            _selectedLocalId = document.GeometryId;
            RefreshPresentation();
            StatusMessage = $"Imported local geometry '{document.DisplayName}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Geometry import failed: {ex.Message}";
        }
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (SelectedLocal is null)
        {
            return;
        }

        try
        {
            await _workspace.ExportLocalAsync(
                SelectedLocal.GeometryId,
                ExportPath,
                cancellationToken);
            StatusMessage = $"Exported geometry to {Path.GetFullPath(ExportPath)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Geometry export failed: {ex.Message}";
        }
    }

    private async Task RemoveLocalAsync(CancellationToken cancellationToken)
    {
        if (SelectedLocal is null)
        {
            return;
        }

        var id = SelectedLocal.GeometryId;
        try
        {
            await _workspace.RemoveLocalAsync(id, cancellationToken);
            _selectedLocalId = null;
            LoadAuthoringSelection(null, force: true);
            RefreshPresentation();
            StatusMessage = $"Removed local geometry '{id}'. The Logos registry was not changed.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not remove local geometry: {ex.Message}";
        }
    }

    private async Task PullAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || SelectedRemote is null)
        {
            return;
        }

        try
        {
            var document = await _workspace.PullAsync(
                SelectedConnection.Id,
                SelectedRemote.GeometryId,
                ReplaceLocalOnPull,
                cancellationToken);
            _selectedLocalId = document.GeometryId;
            RefreshPresentation();
            StatusMessage = $"Pulled '{document.DisplayName}' from {SelectedConnection.Name}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Geometry pull failed: {ex.Message}";
        }
    }

    private async Task CreateRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || SelectedLocal is null)
        {
            return;
        }

        try
        {
            var result = await _workspace.CreateRemoteAsync(
                SelectedConnection.Id,
                SelectedLocal.GeometryId,
                cancellationToken);
            StatusMessage = result.Message;
            RecordRemoteOperationResult(GeometryRemoteOperationKind.Create, SelectedLocal.GeometryId, result);
            ClearRemoteAssessment();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not create geometry on Logos: {ex.Message}";
        }
    }

    private async Task UpdateRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || SelectedLocal is null)
        {
            return;
        }

        try
        {
            var result = await _workspace.UpdateRemoteAsync(
                SelectedConnection.Id,
                SelectedLocal.GeometryId,
                cancellationToken);
            StatusMessage = result.Message;
            RecordRemoteOperationResult(GeometryRemoteOperationKind.Update, SelectedLocal.GeometryId, result);
            ClearRemoteAssessment();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not update geometry on Logos: {ex.Message}";
        }
    }

    private async Task DeleteRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || SelectedRemote is null)
        {
            return;
        }

        var id = SelectedRemote.GeometryId;
        try
        {
            var result = await _workspace.DeleteRemoteAsync(
                SelectedConnection.Id,
                id,
                cancellationToken);
            StatusMessage = result.Message;
            RecordRemoteOperationResult(GeometryRemoteOperationKind.Delete, id, result);
            ClearRemoteAssessment();
            if (result.Accepted)
            {
                _selectedRemoteId = null;
                RemoteDeleteConfirmation = string.Empty;
                RefreshPresentation();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not delete geometry from Logos: {ex.Message}";
        }
    }

    private bool CanPull()
    {
        if (SelectedConnection is null || SelectedRemote is null)
        {
            return false;
        }

        var localExists = _workspace.LocalDocuments.Any(item =>
            string.Equals(item.GeometryId, SelectedRemote.GeometryId, StringComparison.Ordinal));
        return !localExists || ReplaceLocalOnPull;
    }

    private bool CanCreateRemote()
        => SelectedConnection is not null &&
           !AuthoringDirty &&
           !MapEdit.HasDraft &&
           SelectedLocal?.Deployment?.Status is
               GeometryDeploymentStatus.LocalOnly or GeometryDeploymentStatus.MissingRemote;

    private bool CanUpdateRemote()
        => SelectedConnection is not null &&
           !AuthoringDirty &&
           !MapEdit.HasDraft &&
           SelectedLocal?.Deployment?.Status == GeometryDeploymentStatus.LocalModified;

    private bool CanDeleteRemote()
        => SelectedRemote is not null &&
           string.Equals(
               RemoteDeleteConfirmation.Trim(),
               RequiredDeleteConfirmation,
               StringComparison.Ordinal);

    private void RefreshPresentation()
    {
        var selectedLocalId = _selectedLocalId;
        var selectedRemoteId = _selectedRemoteId;
        var connectionId = SelectedConnection?.Id;
        var deployments = _workspace.Deployments
            .Where(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
            .ToDictionary(item => item.GeometryId, StringComparer.Ordinal);
        var localItems = _workspace.LocalDocuments
            .Select(document =>
            {
                deployments.TryGetValue(document.GeometryId, out var deployment);
                var issueCount = _workspace.LibraryIssues.Count(item =>
                    string.Equals(item.GeometryId, document.GeometryId, StringComparison.Ordinal));
                return new GeometryLibraryItemViewModel(
                    document,
                    _validator.Validate(document, requireAuthorableFrame: false),
                    deployment,
                    issueCount);
            })
            .Where(LocalMatchesFilters)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
            .ToArray();
        Replace(LocalItems, localItems);

        var remoteItems = _workspace.RemoteRecords
            .Where(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
            .Select(record =>
            {
                deployments.TryGetValue(record.GeometryId, out var deployment);
                return new GeometryRemoteItemViewModel(record, deployment);
            })
            .Where(RemoteMatchesFilters)
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
            .ToArray();
        Replace(RemoteItems, remoteItems);

        _selectedLocal = LocalItems.FirstOrDefault(item =>
            string.Equals(item.GeometryId, selectedLocalId, StringComparison.Ordinal));
        _selectedRemote = RemoteItems.FirstOrDefault(item =>
            string.Equals(item.GeometryId, selectedRemoteId, StringComparison.Ordinal));
        OnPropertyChanged(nameof(SelectedLocal));
        OnPropertyChanged(nameof(SelectedRemote));
        OnPropertyChanged(nameof(LocalCount));
        OnPropertyChanged(nameof(RemoteCount));
        OnPropertyChanged(nameof(LibraryIssueCount));
        OnPropertyChanged(nameof(LibraryIssueSummary));
        OnPropertyChanged(nameof(GatewayAvailable));
        OnPropertyChanged(nameof(GatewayStatus));
        OnPropertyChanged(nameof(SelectedRegistryWatch));
        OnPropertyChanged(nameof(RegistryWatchSummary));
        OnPropertyChanged(nameof(RegistryWatchDetails));
        OnPropertyChanged(nameof(RegistrySummary));
        RefreshAuthoringAfterPresentation();
        if (!AssessmentMatchesCurrentSelection())
        {
            ClearRemoteAssessment();
        }
        RaiseSelectionProperties();
        RefreshBindingGeometryOptions();
        RaiseBindingCommandStates();
    }

    private bool LocalMatchesFilters(GeometryLibraryItemViewModel item)
        => SearchMatches(item.GeometryId, item.DisplayName, item.Description) &&
           KindMatches(item.Kind) &&
           DeploymentMatches(item.DeploymentState);

    private bool RemoteMatchesFilters(GeometryRemoteItemViewModel item)
        => SearchMatches(item.GeometryId, item.DisplayName, string.Empty) &&
           KindMatches(item.Kind) &&
           DeploymentMatches(item.DeploymentState);

    private bool SearchMatches(params string[] values)
        => string.IsNullOrWhiteSpace(SearchText) ||
           values.Any(value => value.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool KindMatches(string kind)
        => string.Equals(SelectedKindFilter, "All", StringComparison.Ordinal) ||
           string.Equals(SelectedKindFilter, kind, StringComparison.Ordinal);

    private bool DeploymentMatches(string deployment)
        => string.Equals(SelectedDeploymentFilter, "All", StringComparison.Ordinal) ||
           string.Equals(SelectedDeploymentFilter, deployment, StringComparison.Ordinal);

    private void SetSelectedRemote(GeometryRemoteItemViewModel? value)
    {
        _selectedRemote = value;
        _selectedRemoteId = value?.GeometryId;
        _remoteDeleteConfirmation = string.Empty;
        OnPropertyChanged(nameof(SelectedRemote));
        OnPropertyChanged(nameof(RemoteDeleteConfirmation));
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(HasSelectedLocal));
        OnPropertyChanged(nameof(HasSelectedRemote));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(SelectedDetails));
        OnPropertyChanged(nameof(ValidationDetails));
        OnPropertyChanged(nameof(DeploymentDetails));
        OnPropertyChanged(nameof(RequiredDeleteConfirmation));
        OnPropertyChanged(nameof(DeleteConfirmationPrompt));
        OnPropertyChanged(nameof(SelectedRemoteOperation));
        OnPropertyChanged(nameof(SelectedRemoteOperationSummary));
        OnPropertyChanged(nameof(HasConflictResolutionContext));
        _assessRemoteCommand.RaiseCanExecuteChanged();
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _importCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
        _removeLocalCommand.RaiseCanExecuteChanged();
        _pullCommand.RaiseCanExecuteChanged();
        _createRemoteCommand.RaiseCanExecuteChanged();
        _updateRemoteCommand.RaiseCanExecuteChanged();
        _deleteRemoteCommand.RaiseCanExecuteChanged();
        _assessRemoteCommand.RaiseCanExecuteChanged();
        _duplicateLocalCommand.RaiseCanExecuteChanged();
        _pullRemoteCopyCommand.RaiseCanExecuteChanged();
        _editOnMapCommand.RaiseCanExecuteChanged();
        _saveMapDraftCommand.RaiseCanExecuteChanged();
        _discardMapDraftCommand.RaiseCanExecuteChanged();
        RaiseAuthoringCommandStates();
    }

    private void OnEditSessionChanged(object? sender, EventArgs e)
        => _ = _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(MapEdit));
            OnPropertyChanged(nameof(MapEditActive));
            OnPropertyChanged(nameof(MapEditCompleted));
            OnPropertyChanged(nameof(MapEditSummary));
            OnPropertyChanged(nameof(MapEditActionLabel));
            SynchronizeAuthoringFromMapEdit();
            RaiseCommandStates();
        });

    private void OnWorkspaceChanged(object? sender, EventArgs e)
        => _ = _dispatcher.InvokeAsync(RefreshPresentation);

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => _ = _dispatcher.InvokeAsync(() =>
        {
            if (_selectedConnectionId is not null)
            {
                _selectedConnection = Connections.FirstOrDefault(item => item.Id == _selectedConnectionId);
            }
            _selectedConnection ??= Connections.FirstOrDefault();
            _selectedConnectionId = _selectedConnection?.Id;
            OnPropertyChanged(nameof(SelectedConnection));
            RefreshPresentation();
        });


    private static string AltitudeBand(GeometryPolicyAnnotation policy)
    {
        var minimum = policy.MinimumAltitudeMetres?.ToString("0.##", CultureInfo.CurrentCulture) ?? "unbounded";
        var maximum = policy.MaximumAltitudeMetres?.ToString("0.##", CultureInfo.CurrentCulture) ?? "unbounded";
        return $"{minimum} to {maximum} metres";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
