using System.Collections.ObjectModel;
using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed partial class GeometryWorkspaceViewModel
{
    private IBehaviourBindingWorkspaceService? _behaviourBindingWorkspace;
    private ISelectionService? _selection;
    private AsyncRelayCommand? _refreshBindingsCommand;
    private AsyncRelayCommand? _setBindingCommand;
    private AsyncRelayCommand? _clearBindingCommand;
    private RelayCommand? _showBoundGeometryCommand;
    private BehaviourPackageOption? _selectedBehaviourPackage;
    private BehaviourGeometrySlotViewModel? _selectedBehaviourSlot;
    private GeometryRemoteItemViewModel? _selectedBindingGeometry;
    private string _bindingStatus = "Select a Logos connection, then refresh behaviour bindings.";
    private bool _bindingsBusy;

    public ObservableCollection<BehaviourPackageOption> BehaviourPackages { get; } = [];

    public ObservableCollection<BehaviourGeometrySlotViewModel> BehaviourSlots { get; } = [];

    public ObservableCollection<GeometryRemoteItemViewModel> BindingGeometryOptions { get; } = [];

    public BehaviourPackageOption? SelectedBehaviourPackage
    {
        get => _selectedBehaviourPackage;
        set
        {
            if (!SetProperty(ref _selectedBehaviourPackage, value))
            {
                return;
            }

            SelectedBehaviourSlot = null;
            BehaviourSlots.Clear();
            BindingGeometryOptions.Clear();
            RaiseBindingCommandStates();
            if (value is not null && !_bindingsBusy)
            {
                _ = LoadSelectedBehaviourBindingsAsync(value);
            }
        }
    }

    public BehaviourGeometrySlotViewModel? SelectedBehaviourSlot
    {
        get => _selectedBehaviourSlot;
        set
        {
            if (!SetProperty(ref _selectedBehaviourSlot, value))
            {
                return;
            }

            RefreshBindingGeometryOptions();
            SelectedBindingGeometry = value is null
                ? null
                : BindingGeometryOptions.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, value.GeometryId, StringComparison.Ordinal));
            OnPropertyChanged(nameof(SelectedBindingDetails));
            RaiseBindingCommandStates();
        }
    }

    public GeometryRemoteItemViewModel? SelectedBindingGeometry
    {
        get => _selectedBindingGeometry;
        set
        {
            if (SetProperty(ref _selectedBindingGeometry, value))
            {
                OnPropertyChanged(nameof(SelectedBindingDetails));
                RaiseBindingCommandStates();
            }
        }
    }

    public string BindingStatus
    {
        get => _bindingStatus;
        private set => SetProperty(ref _bindingStatus, value);
    }

    public bool BindingsAvailable => _behaviourBindingWorkspace?.IsAvailable == true;

    public string BindingAvailability => _behaviourBindingWorkspace?.AvailabilityMessage
                                         ?? "Behaviour geometry binding support is unavailable.";

    public bool BindingsBusy
    {
        get => _bindingsBusy;
        private set
        {
            if (SetProperty(ref _bindingsBusy, value))
            {
                RaiseBindingCommandStates();
            }
        }
    }

    public string SelectedBindingDetails
    {
        get
        {
            if (SelectedBehaviourSlot is null)
            {
                return "Select a behaviour geometry slot.";
            }

            var slot = SelectedBehaviourSlot;
            var candidate = SelectedBindingGeometry;
            return string.Join(Environment.NewLine,
                $"Slot: {slot.SlotId} ({slot.RequirementLabel})",
                $"Expected geometry: {slot.ExpectedKind}",
                $"Expected policy: {slot.ExpectedPolicy}",
                $"Current binding: {(string.IsNullOrWhiteSpace(slot.GeometryId) ? "Unbound" : slot.GeometryId)}",
                $"Current status: {slot.Status}",
                candidate is null
                    ? "Compatible candidate: none selected"
                    : $"Compatible candidate: {candidate.GeometryId} · {candidate.Record.Kind} · {candidate.DeploymentState}",
                string.IsNullOrWhiteSpace(slot.Issues) ? "Issues: none" : $"Issues: {slot.Issues}");
        }
    }

    public ICommand RefreshBindingsCommand => _refreshBindingsCommand!;

    public ICommand SetBindingCommand => _setBindingCommand!;

    public ICommand ClearBindingCommand => _clearBindingCommand!;

    public ICommand ShowBoundGeometryCommand => _showBoundGeometryCommand!;

    private void InitializeBindings(
        IBehaviourBindingWorkspaceService? bindingWorkspace,
        ISelectionService? selection)
    {
        _behaviourBindingWorkspace = bindingWorkspace;
        _selection = selection;
        _refreshBindingsCommand = new AsyncRelayCommand(
            token => RefreshBindingPackagesAsync(refreshPackages: true, token),
            () => !BindingsBusy && SelectedConnection is not null && _behaviourBindingWorkspace is not null);
        _setBindingCommand = new AsyncRelayCommand(SetBindingAsync, CanSetBinding);
        _clearBindingCommand = new AsyncRelayCommand(ClearBindingAsync, CanClearBinding);
        _showBoundGeometryCommand = new RelayCommand(_ => ShowBoundGeometry(), _ => CanShowBoundGeometry());
    }

    private async Task RefreshBindingPackagesAsync(
        bool refreshPackages,
        CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || _behaviourBindingWorkspace is null)
        {
            return;
        }

        BindingsBusy = true;
        try
        {
            var selectedKey = SelectedBehaviourPackage?.Key;
            var packages = await _behaviourBindingWorkspace.ListPackagesAsync(
                SelectedConnection.Id,
                refreshPackages,
                cancellationToken);
            BehaviourPackages.Clear();
            foreach (var package in packages)
            {
                BehaviourPackages.Add(package.ToLegacyOption());
            }

            SelectedBehaviourPackage = selectedKey is null
                ? BehaviourPackages.FirstOrDefault(item => item.GeometrySlots.Count > 0)
                : BehaviourPackages.FirstOrDefault(item => item.Key == selectedKey)
                  ?? BehaviourPackages.FirstOrDefault(item => item.GeometrySlots.Count > 0);
            if (SelectedBehaviourPackage is not null)
            {
                await LoadSelectedBehaviourBindingsAsync(SelectedBehaviourPackage, cancellationToken);
            }
            else
            {
                BindingStatus = BehaviourPackages.Count == 0
                    ? "No installed behaviour packages were reported by Logos."
                    : "Installed packages do not declare geometry slots.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BindingStatus = $"Could not refresh behaviour packages: {ex.Message}";
        }
        finally
        {
            BindingsBusy = false;
        }
    }

    private async Task LoadSelectedBehaviourBindingsAsync(
        BehaviourPackageOption package,
        CancellationToken cancellationToken = default)
    {
        if (SelectedConnection is null || _behaviourBindingWorkspace is null)
        {
            return;
        }

        BindingsBusy = true;
        try
        {
            var snapshot = await _behaviourBindingWorkspace.InspectAsync(
                SelectedConnection.Id,
                new BehaviourPackageIdentity(package.BehaviourId, package.Version),
                refreshGeometry: false,
                cancellationToken: cancellationToken);
            if (!ReferenceEquals(package, SelectedBehaviourPackage) &&
                package.Key != SelectedBehaviourPackage?.Key)
            {
                return;
            }

            ApplyBindingSnapshot(package, snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BehaviourSlots.Clear();
            BindingGeometryOptions.Clear();
            BindingStatus = $"Could not inspect behaviour bindings: {ex.Message}";
        }
        finally
        {
            BindingsBusy = false;
        }
    }

    private void ApplyBindingSnapshot(
        BehaviourPackageOption package,
        BehaviourBindingWorkspaceSnapshot snapshot)
    {
        var enriched = package with { GeometryReadiness = snapshot.Readiness };
        var packageIndex = BehaviourPackages.IndexOf(package);
        if (packageIndex < 0)
        {
            packageIndex = Enumerable.Range(0, BehaviourPackages.Count)
                .FirstOrDefault(index => BehaviourPackages[index].Key == package.Key, -1);
        }

        if (packageIndex >= 0 && packageIndex < BehaviourPackages.Count)
        {
            BehaviourPackages[packageIndex] = enriched;
        }

        _selectedBehaviourPackage = enriched;
        OnPropertyChanged(nameof(SelectedBehaviourPackage));

        BehaviourSlots.Clear();
        foreach (var slot in snapshot.Slots)
        {
            BehaviourSlots.Add(new BehaviourGeometrySlotViewModel(slot));
        }

        SelectedBehaviourSlot = BehaviourSlots.FirstOrDefault(item => !item.Ready)
                                ?? BehaviourSlots.FirstOrDefault();
        BindingStatus = snapshot.Summary;
    }

    private void RefreshBindingGeometryOptions()
    {
        var selectedId = SelectedBindingGeometry?.GeometryId;
        BindingGeometryOptions.Clear();
        var slot = SelectedBehaviourSlot;
        if (slot is null)
        {
            SelectedBindingGeometry = null;
            return;
        }

        var compatibleIds = slot.CompatibleGeometryIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in RemoteItems
                     .Where(item => compatibleIds.Contains(item.GeometryId))
                     .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            BindingGeometryOptions.Add(item);
        }

        SelectedBindingGeometry = BindingGeometryOptions.FirstOrDefault(item => item.GeometryId == selectedId)
                                  ?? BindingGeometryOptions.FirstOrDefault(item => item.GeometryId == slot.GeometryId)
                                  ?? BindingGeometryOptions.FirstOrDefault();
    }

    private bool CanSetBinding()
        => !BindingsBusy &&
           SelectedConnection is not null &&
           SelectedBehaviourPackage is not null &&
           SelectedBehaviourSlot is not null &&
           SelectedBindingGeometry is not null &&
           _behaviourBindingWorkspace is not null;

    private async Task SetBindingAsync(CancellationToken cancellationToken)
    {
        if (!CanSetBinding()) return;
        BindingsBusy = true;
        try
        {
            var package = SelectedBehaviourPackage!;
            var result = await _behaviourBindingWorkspace!.SetAsync(
                new BehaviourGeometryBindingCommandRequest(
                    SelectedConnection!.Id,
                    package.BehaviourId,
                    package.Version,
                    SelectedBehaviourSlot!.SlotId,
                    SelectedBindingGeometry!.GeometryId),
                cancellationToken);
            BindingStatus = result.Message;
            if (result.Snapshot is not null)
            {
                ApplyBindingSnapshot(package, result.Snapshot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BindingStatus = $"Could not set behaviour binding: {ex.Message}";
        }
        finally
        {
            BindingsBusy = false;
        }
    }

    private bool CanClearBinding()
        => !BindingsBusy &&
           SelectedConnection is not null &&
           SelectedBehaviourPackage is not null &&
           SelectedBehaviourSlot is { Bound: true } &&
           _behaviourBindingWorkspace is not null;

    private async Task ClearBindingAsync(CancellationToken cancellationToken)
    {
        if (!CanClearBinding()) return;
        BindingsBusy = true;
        try
        {
            var package = SelectedBehaviourPackage!;
            var result = await _behaviourBindingWorkspace!.ClearAsync(
                new BehaviourGeometryBindingCommandRequest(
                    SelectedConnection!.Id,
                    package.BehaviourId,
                    package.Version,
                    SelectedBehaviourSlot!.SlotId),
                cancellationToken);
            BindingStatus = result.Message;
            if (result.Snapshot is not null)
            {
                ApplyBindingSnapshot(package, result.Snapshot);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BindingStatus = $"Could not clear behaviour binding: {ex.Message}";
        }
        finally
        {
            BindingsBusy = false;
        }
    }

    private bool CanShowBoundGeometry()
        => SelectedBehaviourSlot is { Bound: true } &&
           RemoteItems.Any(item => item.GeometryId == SelectedBehaviourSlot.GeometryId);

    private void ShowBoundGeometry()
    {
        var id = SelectedBehaviourSlot?.GeometryId;
        if (string.IsNullOrWhiteSpace(id)) return;
        SelectedRemote = RemoteItems.FirstOrDefault(item => item.GeometryId == id);
        SelectedLocal = LocalItems.FirstOrDefault(item => item.GeometryId == id) ?? SelectedLocal;
        PublishGeometrySelection();
    }

    private void ClearBindingPresentation()
    {
        BehaviourPackages.Clear();
        BehaviourSlots.Clear();
        BindingGeometryOptions.Clear();
        _selectedBehaviourPackage = null;
        _selectedBehaviourSlot = null;
        _selectedBindingGeometry = null;
        OnPropertyChanged(nameof(SelectedBehaviourPackage));
        OnPropertyChanged(nameof(SelectedBehaviourSlot));
        OnPropertyChanged(nameof(SelectedBindingGeometry));
        BindingStatus = SelectedConnection is null
            ? "Select a Logos connection, then refresh behaviour bindings."
            : "Refresh behaviour packages for the selected Logos connection.";
        RaiseBindingCommandStates();
    }

    private void RaiseBindingCommandStates()
    {
        _refreshBindingsCommand?.RaiseCanExecuteChanged();
        _setBindingCommand?.RaiseCanExecuteChanged();
        _clearBindingCommand?.RaiseCanExecuteChanged();
        _showBoundGeometryCommand?.RaiseCanExecuteChanged();
    }

    private void PublishGeometrySelection()
    {
        if (_selection is null)
        {
            return;
        }

        if (SelectedLocal is { } local)
        {
            _selection.Select(SelectionFactory.From(new GeometrySelectionContext(
                local.GeometryId,
                local.DisplayName,
                local.Document.Kind,
                local.Document.Frame,
                SelectedConnection?.Id,
                local.Deployment?.Status.ToString() ?? "Local",
                $"{local.Document.Policy.Kind}/{local.Document.Policy.Constraint}",
                "Geometry library")));
            return;
        }

        if (SelectedRemote is { } remote)
        {
            _selection.Select(SelectionFactory.From(new GeometrySelectionContext(
                remote.GeometryId,
                remote.DisplayName,
                remote.Record.Kind,
                remote.Record.Frame,
                remote.Record.ConnectionId,
                remote.DeploymentState.ToString(),
                "Remote registry",
                "Logos GeometryService")));
        }
    }
}
