using System.Collections.ObjectModel;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;

namespace RobotCommand.ViewModels;

/// <summary>
/// Local geometry-library presentation. The document and group lifecycle is
/// deliberately owned by the shared Runtime workflow so CLI and GUI operate
/// on exactly the same library.
/// </summary>
public sealed class GeometryLibraryViewModel : ObservableObject, IDisposable
{
    private readonly IGeometryWorkflow _workflow;
    private readonly IFenceWorkflow _fences;
    private readonly IReviewedOperationWorkflow _reviewed;
    private readonly IUiDispatcher _dispatcher;
    private string _searchText = string.Empty;
    private string _kindFilter = "All";
    private string _groupFilter = "All";
    private GeometryLibraryRowViewModel? _selectedItem;
    private GeometryGroupWorkflowSnapshot? _selectedGroup;
    private FenceSnapshot? _selectedFence;
    private FenceKind _selectedFenceKind = FenceKind.Inclusion;
    private FenceTargetSnapshot? _selectedFenceTarget;
    private string _newGroupName = string.Empty;
    private string _importPath = string.Empty;
    private string _exportPath = string.Empty;
    private string _setPath = string.Empty;
    private string _statusMessage = "Create geometry from the Map Geometry tool, then organise it here.";
    private bool _deleteConfirmationPending;
    private ReviewedOperationSnapshot? _pendingFenceOperation;

    public GeometryLibraryViewModel(IGeometryWorkflow workflow, IFenceWorkflow fences, IUiDispatcher dispatcher, IReviewedOperationWorkflow reviewed)
    {
        _workflow = workflow;
        _fences = fences;
        _reviewed = reviewed;
        _dispatcher = dispatcher;
        Items = [];
        Groups = [];
        Fences = [];
        FenceTargets = [];
        KindFilters = ["All", "PointOfInterest", "WaypointSequence", "Zone"];

        DeleteCommand = new AsyncRelayCommand(BeginDeleteAsync, () => SelectedItem is not null);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync, () => DeleteConfirmationPending && SelectedItem is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !string.IsNullOrWhiteSpace(ImportPath));
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedItem is not null && !string.IsNullOrWhiteSpace(ExportPath));
        ImportSetCommand = new AsyncRelayCommand(ImportSetAsync, () => !string.IsNullOrWhiteSpace(SetPath));
        ExportSetCommand = new AsyncRelayCommand(ExportSetAsync, () => Items.Count > 0 && !string.IsNullOrWhiteSpace(SetPath));
        CreateGroupCommand = new AsyncRelayCommand(CreateGroupAsync, () => !string.IsNullOrWhiteSpace(NewGroupName));
        AssignGroupCommand = new AsyncRelayCommand(AssignGroupAsync, () => SelectedItem is not null && SelectedGroup is not null);
        RemoveFromGroupCommand = new AsyncRelayCommand(RemoveFromGroupAsync, () => SelectedItem is not null && SelectedGroup is not null);
        DeleteGroupCommand = new AsyncRelayCommand(DeleteGroupAsync, () => SelectedGroup is not null);
        CreateFenceCommand = new AsyncRelayCommand(CreateFenceAsync, () => SelectedItem?.Kind == "Zone");
        DeleteFenceCommand = new AsyncRelayCommand(DeleteFenceAsync, () => SelectedFence is not null);
        UploadFenceCommand = new AsyncRelayCommand(UploadFenceAsync, () => SelectedFence is not null && SelectedFenceTarget is not null);
        DownloadFenceCommand = new AsyncRelayCommand(DownloadFenceAsync, () => SelectedFenceTarget is not null);
        ClearFenceCommand = new AsyncRelayCommand(ClearFenceAsync, () => SelectedFenceTarget is not null);
        ExecuteFenceCommand = new AsyncRelayCommand(ExecuteFenceAsync, () => PendingFenceOperation?.CanExecute == true);
        CancelFenceCommand = new AsyncRelayCommand(CancelFenceAsync, () => PendingFenceOperation is not null);
        _workflow.Changed += OnWorkflowChanged;
        _fences.Changed += OnWorkflowChanged;
        Refresh();
    }

    public ObservableCollection<GeometryLibraryRowViewModel> Items { get; }
    public ObservableCollection<GeometryGroupWorkflowSnapshot> Groups { get; }
    public ObservableCollection<FenceSnapshot> Fences { get; }
    public ObservableCollection<FenceTargetSnapshot> FenceTargets { get; }
    public IReadOnlyList<string> KindFilters { get; }
    public IReadOnlyList<FenceKind> FenceKinds { get; } = [FenceKind.Inclusion, FenceKind.Exclusion];
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportSetCommand { get; }
    public ICommand ExportSetCommand { get; }
    public ICommand CreateGroupCommand { get; }
    public ICommand AssignGroupCommand { get; }
    public ICommand RemoveFromGroupCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand CreateFenceCommand { get; }
    public ICommand DeleteFenceCommand { get; }
    public ICommand UploadFenceCommand { get; }
    public ICommand DownloadFenceCommand { get; }
    public ICommand ClearFenceCommand { get; }
    public ICommand ExecuteFenceCommand { get; }
    public ICommand CancelFenceCommand { get; }

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) Refresh(); } }
    public string KindFilter { get => _kindFilter; set { if (SetProperty(ref _kindFilter, value)) Refresh(); } }
    public string GroupFilter { get => _groupFilter; set { if (SetProperty(ref _groupFilter, value)) Refresh(); } }
    public GeometryLibraryRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                DeleteConfirmationPending = false;
                RaiseCommandStates();
            }
        }
    }
    public GeometryGroupWorkflowSnapshot? SelectedGroup { get => _selectedGroup; set { if (SetProperty(ref _selectedGroup, value)) { GroupFilter = value?.Name ?? "All"; RaiseCommandStates(); } } }
    public FenceSnapshot? SelectedFence { get => _selectedFence; set { if (SetProperty(ref _selectedFence, value)) RaiseCommandStates(); } }
    public FenceKind SelectedFenceKind { get => _selectedFenceKind; set => SetProperty(ref _selectedFenceKind, value); }
    public FenceTargetSnapshot? SelectedFenceTarget { get => _selectedFenceTarget; set { if (SetProperty(ref _selectedFenceTarget, value)) RaiseCommandStates(); } }
    public string NewGroupName { get => _newGroupName; set { if (SetProperty(ref _newGroupName, value)) RaiseCommandStates(); } }
    public string ImportPath { get => _importPath; set { if (SetProperty(ref _importPath, value)) RaiseCommandStates(); } }
    public string ExportPath { get => _exportPath; set { if (SetProperty(ref _exportPath, value)) RaiseCommandStates(); } }
    public string SetPath { get => _setPath; set { if (SetProperty(ref _setPath, value)) RaiseCommandStates(); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public ReviewedOperationSnapshot? PendingFenceOperation
    {
        get => _pendingFenceOperation;
        private set
        {
            if (SetProperty(ref _pendingFenceOperation, value))
            {
                OnPropertyChanged(nameof(HasPendingFenceOperation));
                RaiseCommandStates();
            }
        }
    }
    public bool HasPendingFenceOperation => PendingFenceOperation is not null;
    public bool DeleteConfirmationPending
    {
        get => _deleteConfirmationPending;
        private set
        {
            if (SetProperty(ref _deleteConfirmationPending, value)) RaiseCommandStates();
        }
    }
    public int TotalCount => _workflow.LocalDocuments.Count;

    private Task BeginDeleteAsync(CancellationToken token)
    {
        if (SelectedItem is null) return Task.CompletedTask;
        DeleteConfirmationPending = true;
        StatusMessage = $"Confirm deletion of local geometry '{SelectedItem.Name}'.";
        return Task.CompletedTask;
    }
    private async Task ConfirmDeleteAsync(CancellationToken token)
    {
        if (SelectedItem is null || !DeleteConfirmationPending) return;
        var item = SelectedItem;
        await _workflow.RemoveAsync(item.Id, token);
        DeleteConfirmationPending = false;
        StatusMessage = $"Deleted local geometry '{item.Name}'.";
    }
    private async Task ImportAsync(CancellationToken token)
    {
        var saved = await _workflow.ImportAsync(ImportPath, cancellationToken: token);
        StatusMessage = $"Imported local geometry '{saved.Name}'.";
    }
    private async Task ExportAsync(CancellationToken token)
    {
        if (SelectedItem is null) return;
        await _workflow.ExportAsync(SelectedItem.Id, ExportPath, token);
        StatusMessage = $"Exported '{SelectedItem.Name}'.";
    }
    private async Task ImportSetAsync(CancellationToken token)
    {
        await _workflow.ImportSetAsync(SetPath, cancellationToken: token);
        StatusMessage = "Imported geometry set.";
    }
    private async Task ExportSetAsync(CancellationToken token)
    {
        await _workflow.ExportSetAsync(new(SetPath, "Robot Command geometry set", _workflow.LocalDocuments.Select(item => item.Id).ToArray()), token);
        StatusMessage = "Exported geometry set.";
    }
    private async Task CreateGroupAsync(CancellationToken token)
    {
        await _workflow.CreateGroupAsync(NewGroupName, token);
        StatusMessage = $"Created group '{NewGroupName.Trim()}'.";
        NewGroupName = string.Empty;
    }
    private async Task AssignGroupAsync(CancellationToken token)
    {
        if (SelectedItem is null || SelectedGroup is null) return;
        await _workflow.AssignGroupAsync(SelectedGroup.Name, [SelectedItem.Id], token);
        StatusMessage = $"Added '{SelectedItem.Name}' to '{SelectedGroup.Name}'.";
    }
    private async Task RemoveFromGroupAsync(CancellationToken token)
    {
        if (SelectedItem is null || SelectedGroup is null) return;
        await _workflow.RemoveFromGroupAsync(SelectedGroup.Name, [SelectedItem.Id], token);
        StatusMessage = $"Removed '{SelectedItem.Name}' from '{SelectedGroup.Name}'.";
    }
    private async Task DeleteGroupAsync(CancellationToken token)
    {
        if (SelectedGroup is null) return;
        var group = SelectedGroup.Name;
        await _workflow.DeleteGroupAsync(group, token);
        SelectedGroup = null;
        StatusMessage = $"Deleted group '{group}'.";
    }
    private async Task CreateFenceAsync(CancellationToken token)
    {
        if (SelectedItem?.Kind != "Zone") return;
        var fence = await _fences.CreateFromZoneAsync(SelectedItem.Id, kind: SelectedFenceKind, cancellationToken: token);
        SelectedFence = fence;
        StatusMessage = $"Created {SelectedFenceKind.ToString().ToLowerInvariant()} fence '{fence.Document.DisplayName}'.";
    }
    private async Task DeleteFenceAsync(CancellationToken token)
    {
        if (SelectedFence is null) return;
        var name = SelectedFence.Document.DisplayName;
        await _fences.DeleteAsync(SelectedFence.Document.FenceId, token);
        SelectedFence = null;
        StatusMessage = $"Deleted fence '{name}'.";
    }
    public async Task ImportFenceAsync(string path, CancellationToken cancellationToken = default)
    {
        var fence = await _fences.ImportAsync(path, cancellationToken: cancellationToken);
        SelectedFence = fence;
        StatusMessage = $"Imported fence '{fence.Document.DisplayName}'.";
    }
    public async Task ExportFenceAsync(string path, CancellationToken cancellationToken = default)
    {
        if (SelectedFence is null) return;
        await _fences.ExportAsync(SelectedFence.Document.FenceId, path, cancellationToken);
        StatusMessage = $"Exported fence '{SelectedFence.Document.DisplayName}'.";
    }
    private async Task UploadFenceAsync(CancellationToken token)
    {
        if (SelectedFence is null || SelectedFenceTarget is null) return;
        PendingFenceOperation = await _fences.PlanUploadAsync(SelectedFence.Document.FenceId, string.Empty, SelectedFenceTarget.VehicleId, token);
        StatusMessage = PendingFenceOperation.Findings.FirstOrDefault(item => item.Severity == WorkflowFindingSeverity.Blocking)?.Message ?? PendingFenceOperation.Summary;
    }
    private async Task DownloadFenceAsync(CancellationToken token)
    {
        if (SelectedFenceTarget is null) return;
        PendingFenceOperation = await _fences.PlanDownloadAsync(string.Empty, SelectedFenceTarget.VehicleId, token);
        StatusMessage = PendingFenceOperation.Findings.FirstOrDefault(item => item.Severity == WorkflowFindingSeverity.Blocking)?.Message ?? PendingFenceOperation.Summary;
    }
    private async Task ClearFenceAsync(CancellationToken token)
    {
        if (SelectedFenceTarget is null) return;
        PendingFenceOperation = await _fences.PlanClearAsync(string.Empty, SelectedFenceTarget.VehicleId, token);
        StatusMessage = PendingFenceOperation.Findings.FirstOrDefault(item => item.Severity == WorkflowFindingSeverity.Blocking)?.Message ?? PendingFenceOperation.Summary;
    }
    private async Task ExecuteFenceAsync(CancellationToken token)
    {
        if (PendingFenceOperation is null) return;
        var result = await _reviewed.ExecuteAsync(PendingFenceOperation.Id, token);
        PendingFenceOperation = _reviewed.TryGet(PendingFenceOperation.Id, out var current) ? current : PendingFenceOperation;
        StatusMessage = result.Summary;
    }
    private async Task CancelFenceAsync(CancellationToken token)
    {
        if (PendingFenceOperation is null) return;
        await _reviewed.CancelAsync(PendingFenceOperation.Id, cancellationToken: token);
        PendingFenceOperation = null;
        StatusMessage = "Fence operation cancelled.";
    }
    private void OnWorkflowChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess()) Refresh();
        else _ = _dispatcher.InvokeAsync(Refresh);
    }
    private void Refresh()
    {
        var selectedId = SelectedItem?.Id; var selectedFenceId = SelectedFence?.Document.FenceId; var selectedTargetId = SelectedFenceTarget?.VehicleId;
        Groups.Clear();
        foreach (var group in _workflow.Groups.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)) Groups.Add(group);
        var rows = _workflow.LocalDocuments
            .Where(item => string.IsNullOrWhiteSpace(SearchText) || item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            .Where(item => KindFilter == "All" || string.Equals(item.Kind, KindFilter, StringComparison.OrdinalIgnoreCase))
            .Where(item => GroupFilter == "All" || _workflow.GetGroups(item.Id).Contains(GroupFilter, StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new GeometryLibraryRowViewModel(item, _workflow.GetGroups(item.Id)))
            .ToArray();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        Fences.Clear();
        foreach (var fence in _fences.Fences.OrderBy(item => item.Document.DisplayName, StringComparer.OrdinalIgnoreCase)) Fences.Add(fence);
        FenceTargets.Clear();
        foreach (var target in _fences.Targets.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)) FenceTargets.Add(target);
        _selectedItem = selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId);
        _selectedFence = selectedFenceId is null ? null : Fences.FirstOrDefault(item => item.Document.FenceId == selectedFenceId);
        _selectedFenceTarget = selectedTargetId is null ? FenceTargets.FirstOrDefault() : FenceTargets.FirstOrDefault(item => item.VehicleId == selectedTargetId);
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(SelectedFence));
        OnPropertyChanged(nameof(SelectedFenceTarget));
        OnPropertyChanged(nameof(TotalCount));
        RaiseCommandStates();
    }
    private void RaiseCommandStates()
    {
        foreach (var command in new ICommand[] { DeleteCommand, ConfirmDeleteCommand, ImportCommand, ExportCommand, ImportSetCommand, ExportSetCommand, CreateGroupCommand, AssignGroupCommand, RemoveFromGroupCommand, DeleteGroupCommand, CreateFenceCommand, DeleteFenceCommand, UploadFenceCommand, DownloadFenceCommand, ClearFenceCommand, ExecuteFenceCommand, CancelFenceCommand })
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
    }
    public void Dispose() { _workflow.Changed -= OnWorkflowChanged; _fences.Changed -= OnWorkflowChanged; }
}

public sealed record GeometryLibraryRowViewModel(GeometryWorkflowSnapshot Snapshot, IReadOnlyList<string> Groups)
{
    public string Id => Snapshot.Id;
    public string Name => Snapshot.Name;
    public string Kind => Snapshot.Kind;
    public string Validation => Snapshot.ValidationSummary;
    public string GroupSummary => Groups.Count == 0 ? "No groups" : string.Join(", ", Groups);
}
