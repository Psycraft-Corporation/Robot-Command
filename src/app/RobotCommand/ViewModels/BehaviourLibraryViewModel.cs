using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class BehaviourLibraryViewModel : ObservableObject
{
    private readonly IBehaviourWorkspaceService _workspace;
    private readonly IBehaviourPackageStore _store;
    private readonly IBehaviourPackageDeploymentService _deployment;
    private readonly IBehaviourBindingWorkspaceService _bindings;
    private readonly IUiDispatcher _dispatcher;
    private readonly BehaviourParameterSchemaReader _parameterSchemas = new();
    private readonly List<BehaviourLibraryItemViewModel> _allEntries = [];
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _importCommand;
    private readonly AsyncRelayCommand _exportCommand;
    private readonly AsyncRelayCommand _removeLocalCommand;
    private readonly AsyncRelayCommand _validateCommand;
    private readonly AsyncRelayCommand _prepareInstallCommand;
    private readonly AsyncRelayCommand _prepareUpdateCommand;
    private readonly AsyncRelayCommand _prepareRemoveRemoteCommand;
    private readonly AsyncRelayCommand _executePreparedOperationCommand;
    private readonly AsyncRelayCommand _inspectBindingsCommand;
    private readonly AsyncRelayCommand _setBindingCommand;
    private readonly AsyncRelayCommand _clearBindingCommand;

    private ConnectionRecord? _selectedConnection;
    private BehaviourLibraryItemViewModel? _selectedEntry;
    private BehaviourBindingSlotItemViewModel? _selectedBindingSlot;
    private BehaviourGeometryCandidate? _selectedBindingCandidate;
    private BehaviourPackageOperationAssessment? _preparedOperation;
    private BehaviourBindingWorkspaceSnapshot? _bindingSnapshot;
    private BehaviourParameterSchema? _parameterSchema;
    private string _searchText = string.Empty;
    private string _selectedSourceFilter = "All";
    private string _importDirectory = string.Empty;
    private string _exportDirectory = string.Empty;
    private bool _allowReplaceImport;
    private bool _allowReplaceExport;
    private string _localRemoveConfirmation = string.Empty;
    private string _operationConfirmation = string.Empty;
    private string _statusMessage = "Select a Logos connection and refresh the behaviour library.";
    private string _remoteInventorySummary = "The installed behaviour inventory has not been loaded.";
    private string _lastOperationDetails = "No behaviour package operation has been attempted in this session.";
    private string _bindingStatus = "Select an installed package and inspect its geometry bindings.";
    private bool _busy;
    private int _parameterSchemaRevision;

    public BehaviourLibraryViewModel(
        IBehaviourWorkspaceService workspace,
        IBehaviourPackageStore store,
        IBehaviourPackageDeploymentService deployment,
        IBehaviourBindingWorkspaceService bindings,
        IEntityStore<string, ConnectionRecord> connections,
        IUiDispatcher dispatcher)
    {
        _workspace = workspace;
        _store = store;
        _deployment = deployment;
        _bindings = bindings;
        _dispatcher = dispatcher;

        Connections = connections.Items;
        Entries = [];
        BindingSlots = [];
        BindingCandidates = [];
        ParameterDefinitions = [];
        ParameterSchemaFindings = [];
        SourceFilters = ["All", "Local", "Installed", "Action required"];

        _selectedConnection = Connections
            .OrderByDescending(item => item.State == AvailabilityState.Online)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        _refreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        _importCommand = new AsyncRelayCommand(ImportAsync, CanImport);
        _exportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        _removeLocalCommand = new AsyncRelayCommand(RemoveLocalAsync, CanRemoveLocal);
        _validateCommand = new AsyncRelayCommand(ValidateAsync, CanValidate);
        _prepareInstallCommand = new AsyncRelayCommand(
            token => PrepareOperationAsync(BehaviourPackageOperationKind.Install, token),
            CanPrepareInstall);
        _prepareUpdateCommand = new AsyncRelayCommand(
            token => PrepareOperationAsync(BehaviourPackageOperationKind.Update, token),
            CanPrepareUpdate);
        _prepareRemoveRemoteCommand = new AsyncRelayCommand(
            token => PrepareOperationAsync(BehaviourPackageOperationKind.Remove, token),
            CanPrepareRemoveRemote);
        _executePreparedOperationCommand = new AsyncRelayCommand(
            ExecutePreparedOperationAsync,
            CanExecutePreparedOperation);
        _inspectBindingsCommand = new AsyncRelayCommand(InspectBindingsAsync, CanInspectBindings);
        _setBindingCommand = new AsyncRelayCommand(SetBindingAsync, CanSetBinding);
        _clearBindingCommand = new AsyncRelayCommand(ClearBindingAsync, CanClearBinding);

        _workspace.Changed += OnWorkspaceChanged;
        _bindings.Changed += OnBindingsChanged;
        _deployment.Changed += OnDeploymentChanged;
        ((INotifyCollectionChanged)Connections).CollectionChanged += OnConnectionsChanged;

        if (_selectedConnection is not null)
        {
            ApplySnapshot(_workspace.GetSnapshot(_selectedConnection.Id));
        }
    }

    public ReadOnlyObservableCollection<ConnectionRecord> Connections { get; }

    public ObservableCollection<BehaviourLibraryItemViewModel> Entries { get; }

    public ObservableCollection<BehaviourBindingSlotItemViewModel> BindingSlots { get; }

    public ObservableCollection<BehaviourGeometryCandidate> BindingCandidates { get; }

    public ObservableCollection<BehaviourParameterDefinition> ParameterDefinitions { get; }

    public ObservableCollection<string> ParameterSchemaFindings { get; }

    public IReadOnlyList<string> SourceFilters { get; }

    public string LocalLibraryPath => _store.RootPath;

    public string BindingAvailability => _bindings.AvailabilityMessage;

    public bool BindingServiceAvailable => _bindings.IsAvailable;

    public bool IsEmpty => Entries.Count == 0;

    public int EntryCount => Entries.Count;

    public int LocalPackageCount => _allEntries.Count(item => item.HasLocalPackage);

    public int InstalledPackageCount => _allEntries.Count(item => item.HasRemotePackage);

    public ConnectionRecord? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (value is null && _selectedConnection is not null)
            {
                value = Connections.FirstOrDefault(item => item.Id == _selectedConnection.Id);
            }

            if (!SetProperty(ref _selectedConnection, value))
            {
                return;
            }

            ClearPreparedOperation();
            ClearBindingPresentation();
            ApplySnapshot(value is null
                ? null
                : _workspace.GetSnapshot(value.Id));
            StatusMessage = value is null
                ? "Select a Logos connection to compare local and installed behaviour packages."
                : $"Refresh to compare the local library with {value.Name}.";
            RaiseCommandStates();
        }
    }

    public BehaviourLibraryItemViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value))
            {
                return;
            }

            ClearPreparedOperation();
            ClearBindingPresentation();
            ClearParameterSchemaPresentation();
            LocalRemoveConfirmation = string.Empty;
            ExportDirectory = value?.HasLocalPackage == true
                ? $"{SafeFileName(value.IdentityLabel)}-export"
                : string.Empty;
            BindingStatus = value?.HasRemotePackage == true
                ? "Inspect the installed package to load its geometry binding readiness."
                : "Geometry bindings are available only for a package installed on Logos.";
            OnPropertyChanged(nameof(HasSelectedEntry));
            OnPropertyChanged(nameof(ShowParameterSchemaEmpty));
            OnPropertyChanged(nameof(SelectedEntryDetails));
            OnPropertyChanged(nameof(RequiredLocalRemoveConfirmation));
            RaiseCommandStates();
            var revision = ++_parameterSchemaRevision;
            _ = LoadParameterSchemaAsync(value, revision);
        }
    }

    public bool HasSelectedEntry => SelectedEntry is not null;

    public string SelectedEntryDetails
    {
        get
        {
            var item = SelectedEntry;
            if (item is null)
            {
                return "Select a package to inspect its local integrity, Logos state, hashes, and requirements.";
            }

            return string.Join(Environment.NewLine,
                $"Identity: {item.IdentityLabel}",
                $"Source: {item.SourceSummary}",
                $"Deployment: {item.DeploymentState}",
                $"Local state: {item.LocalState}",
                $"Logos state: {item.RemoteState}",
                $"Integrity: {item.LocalIntegrity}",
                $"Logos validation: {item.LogosValidation}",
                item.CapabilitySummary,
                item.GeometrySummary,
                item.HashSummary,
                item.Active ? "Package is active." : "Package is not reported active.",
                item.InUse ? "Package is currently in use." : "Package is not reported in use.");
        }
    }


    public BehaviourParameterSchema? ParameterSchema => _parameterSchema;

    public bool HasParameterDefinitions => ParameterDefinitions.Count > 0;

    public bool ShowParameterSchemaEmpty => HasSelectedEntry && !HasParameterDefinitions;

    public bool HasParameterSchemaFindings => ParameterSchemaFindings.Count > 0;

    public string ParameterSchemaState => ParameterSchema?.State.ToString() ?? "Not loaded";

    public string ParameterSchemaSummary => ParameterSchema?.Summary
                                            ?? "Select a local package to inspect optional operator parameter metadata.";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RefreshEntryPresentation();
            }
        }
    }

    public string SelectedSourceFilter
    {
        get => _selectedSourceFilter;
        set
        {
            if (SetProperty(ref _selectedSourceFilter, string.IsNullOrWhiteSpace(value) ? "All" : value))
            {
                RefreshEntryPresentation();
            }
        }
    }

    public string ImportDirectory
    {
        get => _importDirectory;
        set
        {
            if (SetProperty(ref _importDirectory, value ?? string.Empty))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ExportDirectory
    {
        get => _exportDirectory;
        set
        {
            if (SetProperty(ref _exportDirectory, value ?? string.Empty))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool AllowReplaceImport
    {
        get => _allowReplaceImport;
        set => SetProperty(ref _allowReplaceImport, value);
    }

    public bool AllowReplaceExport
    {
        get => _allowReplaceExport;
        set => SetProperty(ref _allowReplaceExport, value);
    }

    public string LocalRemoveConfirmation
    {
        get => _localRemoveConfirmation;
        set
        {
            if (SetProperty(ref _localRemoveConfirmation, value ?? string.Empty))
            {
                RaiseCommandStates();
            }
        }
    }

    public string RequiredLocalRemoveConfirmation => SelectedEntry is null
        ? string.Empty
        : BehaviourOperationConfirmation.ForLocalRemoval(SelectedEntry.Identity);

    public string OperationConfirmation
    {
        get => _operationConfirmation;
        set
        {
            if (SetProperty(ref _operationConfirmation, value ?? string.Empty))
            {
                RaiseCommandStates();
            }
        }
    }

    public BehaviourPackageOperationAssessment? PreparedOperation
    {
        get => _preparedOperation;
        private set
        {
            if (!SetProperty(ref _preparedOperation, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPreparedOperation));
            OnPropertyChanged(nameof(PreparedOperationTitle));
            OnPropertyChanged(nameof(PreparedOperationDetails));
            OnPropertyChanged(nameof(RequiredOperationConfirmation));
            RaiseCommandStates();
        }
    }

    public bool HasPreparedOperation => PreparedOperation is not null;

    public string PreparedOperationTitle => PreparedOperation is null
        ? "No remote operation prepared"
        : $"{PreparedOperation.Operation} {PreparedOperation.Identity.Key}";

    public string PreparedOperationDetails
    {
        get
        {
            var assessment = PreparedOperation;
            if (assessment is null)
            {
                return "Assess an install, update, or removal before sending a package mutation to Logos.";
            }

            var builder = new StringBuilder();
            builder.AppendLine(assessment.Summary);
            builder.AppendLine(assessment.Allowed ? "Assessment: allowed" : "Assessment: blocked");
            foreach (var blocker in assessment.Blockers)
            {
                builder.AppendLine(CultureInfo.CurrentCulture, $"BLOCKER · {blocker}");
            }
            foreach (var warning in assessment.Warnings)
            {
                builder.AppendLine(CultureInfo.CurrentCulture, $"WARNING · {warning}");
            }
            return builder.ToString().TrimEnd();
        }
    }

    public string RequiredOperationConfirmation => PreparedOperation is null
        ? string.Empty
        : BehaviourOperationConfirmation.ForRemote(PreparedOperation);

    public BehaviourBindingSlotItemViewModel? SelectedBindingSlot
    {
        get => _selectedBindingSlot;
        set
        {
            if (!SetProperty(ref _selectedBindingSlot, value))
            {
                return;
            }

            RefreshBindingCandidates();
            OnPropertyChanged(nameof(SelectedBindingDetails));
            RaiseCommandStates();
        }
    }

    public BehaviourGeometryCandidate? SelectedBindingCandidate
    {
        get => _selectedBindingCandidate;
        set
        {
            if (SetProperty(ref _selectedBindingCandidate, value))
            {
                OnPropertyChanged(nameof(SelectedBindingDetails));
                RaiseCommandStates();
            }
        }
    }

    public string SelectedBindingDetails
    {
        get
        {
            var slot = SelectedBindingSlot;
            if (slot is null)
            {
                return "Select a declared geometry slot to inspect its requirement and compatible registered geometry.";
            }

            var candidate = SelectedBindingCandidate;
            return string.Join(Environment.NewLine,
                $"Slot: {slot.SlotId} ({slot.RequirementLabel})",
                $"State: {slot.State}",
                $"Expected geometry: {slot.ExpectedKind}",
                $"Expected policy: {slot.ExpectedPolicy}",
                $"Current geometry: {(slot.Bound ? slot.GeometryId : "Unbound")}",
                slot.Summary,
                slot.Issues,
                candidate is null
                    ? "Candidate: none selected"
                    : $"Candidate: {candidate.DisplayName} · {candidate.GeometryId} · {candidate.Kind} · {candidate.PolicySummary}");
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string RemoteInventorySummary
    {
        get => _remoteInventorySummary;
        private set => SetProperty(ref _remoteInventorySummary, value);
    }

    public string LastOperationDetails
    {
        get => _lastOperationDetails;
        private set => SetProperty(ref _lastOperationDetails, value);
    }

    public string BindingStatus
    {
        get => _bindingStatus;
        private set => SetProperty(ref _bindingStatus, value);
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
    public ICommand ImportCommand => _importCommand;
    public ICommand ExportCommand => _exportCommand;
    public ICommand RemoveLocalCommand => _removeLocalCommand;
    public ICommand ValidateCommand => _validateCommand;
    public ICommand PrepareInstallCommand => _prepareInstallCommand;
    public ICommand PrepareUpdateCommand => _prepareUpdateCommand;
    public ICommand PrepareRemoveRemoteCommand => _prepareRemoveRemoteCommand;
    public ICommand ExecutePreparedOperationCommand => _executePreparedOperationCommand;
    public ICommand InspectBindingsCommand => _inspectBindingsCommand;
    public ICommand SetBindingCommand => _setBindingCommand;
    public ICommand ClearBindingCommand => _clearBindingCommand;

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var connection = SelectedConnection
                         ?? throw new InvalidOperationException("Select a Logos connection before refreshing behaviours.");
        await RunBusyAsync(async () =>
        {
            var snapshot = await _workspace.RefreshAsync(
                connection.Id,
                refreshLocal: true,
                refreshRemote: true,
                cancellationToken: cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplySnapshot(snapshot);
                StatusMessage = $"Refreshed {snapshot.Entries.Count} behaviour package record(s) for {connection.Name}.";
            }, cancellationToken);
        });
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _store.ImportDirectoryAsync(
                ImportDirectory.Trim(),
                AllowReplaceImport,
                cancellationToken);
            var snapshot = await RefreshWorkspaceAfterLocalChangeAsync(cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplySnapshot(snapshot, result.Package.Identity);
                StatusMessage = result.Message;
            }, cancellationToken);
        });
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedEntry
                       ?? throw new InvalidOperationException("Select a local package before exporting it.");
        await RunBusyAsync(async () =>
        {
            await _store.ExportDirectoryAsync(
                selected.Identity,
                ExportDirectory.Trim(),
                AllowReplaceExport,
                cancellationToken);
            await _dispatcher.InvokeAsync(
                () => StatusMessage = $"Exported '{selected.IdentityLabel}' to '{ExportDirectory.Trim()}'.",
                cancellationToken);
        });
    }

    private async Task RemoveLocalAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedEntry
                       ?? throw new InvalidOperationException("Select a local package before removing it.");
        await RunBusyAsync(async () =>
        {
            await _store.RemoveAsync(selected.Identity, cancellationToken);
            var snapshot = await RefreshWorkspaceAfterLocalChangeAsync(cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplySnapshot(snapshot);
                LocalRemoveConfirmation = string.Empty;
                StatusMessage = $"Removed local package '{selected.IdentityLabel}'. The Logos runtime was not changed.";
            }, cancellationToken);
        });
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var (connection, selected) = RequireSelection();
        await RunBusyAsync(async () =>
        {
            var assessment = await _deployment.AssessAsync(
                BehaviourPackageOperationKind.Validate,
                connection.Id,
                selected.Identity,
                cancellationToken);
            if (!assessment.Allowed)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    LastOperationDetails = FormatAssessment(assessment);
                    StatusMessage = assessment.Summary;
                }, cancellationToken);
                return;
            }

            var result = await _deployment.ValidateAsync(assessment.CreateRequest(), cancellationToken);
            var snapshot = await RefreshWorkspaceAfterRemoteOperationAsync(connection.Id, cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplySnapshot(snapshot, selected.Identity);
                ApplyCommandResult(result);
            }, cancellationToken);
        });
    }

    private async Task PrepareOperationAsync(
        BehaviourPackageOperationKind operation,
        CancellationToken cancellationToken)
    {
        var (connection, selected) = RequireSelection();
        await RunBusyAsync(async () =>
        {
            var assessment = await _deployment.AssessAsync(
                operation,
                connection.Id,
                selected.Identity,
                cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                PreparedOperation = assessment;
                OperationConfirmation = string.Empty;
                StatusMessage = assessment.Summary;
            }, cancellationToken);
        });
    }

    private async Task ExecutePreparedOperationAsync(CancellationToken cancellationToken)
    {
        var assessment = PreparedOperation
                         ?? throw new InvalidOperationException("Assess a behaviour package operation first.");
        await RunBusyAsync(async () =>
        {
            var request = assessment.CreateRequest();
            var result = assessment.Operation switch
            {
                BehaviourPackageOperationKind.Install =>
                    await _deployment.InstallAsync(request, cancellationToken),
                BehaviourPackageOperationKind.Update =>
                    await _deployment.UpdateAsync(request, cancellationToken),
                BehaviourPackageOperationKind.Remove =>
                    await _deployment.RemoveAsync(request, cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Prepared operation '{assessment.Operation}' is not a remote mutation.")
            };
            var snapshot = await RefreshWorkspaceAfterRemoteOperationAsync(
                assessment.ConnectionId,
                cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplySnapshot(snapshot, assessment.Identity);
                ApplyCommandResult(result);
                ClearPreparedOperation();
            }, cancellationToken);
        });
    }

    private async Task InspectBindingsAsync(CancellationToken cancellationToken)
    {
        var (connection, selected) = RequireSelection();
        await RunBusyAsync(async () =>
        {
            var snapshot = await _bindings.InspectAsync(
                connection.Id,
                selected.Identity,
                refreshPackages: true,
                refreshGeometry: true,
                cancellationToken);
            await _dispatcher.InvokeAsync(() => ApplyBindingSnapshot(snapshot), cancellationToken);
        });
    }

    private async Task SetBindingAsync(CancellationToken cancellationToken)
    {
        var (connection, selected) = RequireSelection();
        var slot = SelectedBindingSlot
                   ?? throw new InvalidOperationException("Select a geometry slot before setting a binding.");
        var candidate = SelectedBindingCandidate
                        ?? throw new InvalidOperationException("Select compatible registered geometry before setting a binding.");
        await RunBusyAsync(async () =>
        {
            var result = await _bindings.SetAsync(
                new BehaviourGeometryBindingCommandRequest(
                    connection.Id,
                    selected.Identity.BehaviourId,
                    selected.Identity.Version ?? string.Empty,
                    slot.SlotId,
                    candidate.GeometryId),
                cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                if (result.Snapshot is not null)
                {
                    ApplyBindingSnapshot(result.Snapshot);
                }
                BindingStatus = result.Message;
                StatusMessage = result.Message;
            }, cancellationToken);
        });
    }

    private async Task ClearBindingAsync(CancellationToken cancellationToken)
    {
        var (connection, selected) = RequireSelection();
        var slot = SelectedBindingSlot
                   ?? throw new InvalidOperationException("Select a geometry slot before clearing a binding.");
        await RunBusyAsync(async () =>
        {
            var result = await _bindings.ClearAsync(
                new BehaviourGeometryBindingCommandRequest(
                    connection.Id,
                    selected.Identity.BehaviourId,
                    selected.Identity.Version ?? string.Empty,
                    slot.SlotId),
                cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                if (result.Snapshot is not null)
                {
                    ApplyBindingSnapshot(result.Snapshot);
                }
                BindingStatus = result.Message;
                StatusMessage = result.Message;
            }, cancellationToken);
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        Busy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() => StatusMessage = ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private Task<BehaviourWorkspaceSnapshot> RefreshWorkspaceAfterLocalChangeAsync(
        CancellationToken cancellationToken)
    {
        var connection = SelectedConnection;
        return connection is null
            ? Task.FromResult(BehaviourWorkspaceSnapshot.Empty("local-library", _store.Packages))
            : _workspace.RefreshAsync(
                connection.Id,
                refreshLocal: true,
                refreshRemote: false,
                cancellationToken: cancellationToken);
    }

    private Task<BehaviourWorkspaceSnapshot> RefreshWorkspaceAfterRemoteOperationAsync(
        string connectionId,
        CancellationToken cancellationToken)
        => _workspace.RefreshAsync(
            connectionId,
            refreshLocal: true,
            refreshRemote: true,
            cancellationToken: cancellationToken);

    private void ApplySnapshot(
        BehaviourWorkspaceSnapshot? snapshot,
        BehaviourPackageIdentity? preferredIdentity = null)
    {
        var selectedIdentity = preferredIdentity ?? SelectedEntry?.Identity;
        _allEntries.Clear();
        if (snapshot is not null)
        {
            _allEntries.AddRange(snapshot.Entries.Select(item => new BehaviourLibraryItemViewModel(item)));
            RemoteInventorySummary = snapshot.RemoteInventory.Summary;
        }
        else
        {
            RemoteInventorySummary = "Select a Logos connection to load the installed behaviour inventory.";
        }

        RefreshEntryPresentation(selectedIdentity);
        OnPropertyChanged(nameof(LocalPackageCount));
        OnPropertyChanged(nameof(InstalledPackageCount));
        OnPropertyChanged(nameof(LocalLibraryPath));
        OnPropertyChanged(nameof(BindingAvailability));
        OnPropertyChanged(nameof(BindingServiceAvailable));
    }

    private void RefreshEntryPresentation(BehaviourPackageIdentity? preferredIdentity = null)
    {
        var selectedIdentity = preferredIdentity ?? SelectedEntry?.Identity;
        var filtered = _allEntries
            .Where(MatchesSearch)
            .Where(MatchesSourceFilter)
            .ToArray();

        Entries.Clear();
        foreach (var item in filtered)
        {
            Entries.Add(item);
        }

        SelectedEntry = selectedIdentity is null
            ? Entries.FirstOrDefault()
            : Entries.FirstOrDefault(item => item.Identity == selectedIdentity)
              ?? Entries.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EntryCount));
    }

    private bool MatchesSearch(BehaviourLibraryItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var value = SearchText.Trim();
        return item.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               item.IdentityLabel.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSourceFilter(BehaviourLibraryItemViewModel item)
        => SelectedSourceFilter switch
        {
            "Local" => item.HasLocalPackage,
            "Installed" => item.HasRemotePackage,
            "Action required" => item.Entry.Deployment.Status is
                BehaviourDeploymentStatus.NotInstalled or
                BehaviourDeploymentStatus.RemoteOnly or
                BehaviourDeploymentStatus.LocalUpdateAvailable or
                BehaviourDeploymentStatus.Drifted or
                BehaviourDeploymentStatus.Conflict or
                BehaviourDeploymentStatus.Invalid or
                BehaviourDeploymentStatus.Incompatible or
                BehaviourDeploymentStatus.Unavailable,
            _ => true
        };

    private async Task LoadParameterSchemaAsync(
        BehaviourLibraryItemViewModel? item,
        int revision)
    {
        if (item?.Entry.Local is not { } local)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (revision != _parameterSchemaRevision) return;
                ApplyParameterSchema(null);
            });
            return;
        }

        var schema = await _parameterSchemas.ReadAsync(local);
        await _dispatcher.InvokeAsync(() =>
        {
            if (revision != _parameterSchemaRevision || SelectedEntry?.Identity != item.Identity) return;
            ApplyParameterSchema(schema);
        });
    }

    private void ApplyParameterSchema(BehaviourParameterSchema? schema)
    {
        _parameterSchema = schema;
        ParameterDefinitions.Clear();
        ParameterSchemaFindings.Clear();
        foreach (var definition in schema?.Parameters ?? [])
        {
            ParameterDefinitions.Add(definition);
        }
        foreach (var finding in schema?.Findings ?? [])
        {
            ParameterSchemaFindings.Add(string.IsNullOrWhiteSpace(finding.Field)
                ? $"{finding.Code}: {finding.Message}"
                : $"{finding.Field} · {finding.Code}: {finding.Message}");
        }

        OnPropertyChanged(nameof(ParameterSchema));
        OnPropertyChanged(nameof(HasParameterDefinitions));
        OnPropertyChanged(nameof(ShowParameterSchemaEmpty));
        OnPropertyChanged(nameof(HasParameterSchemaFindings));
        OnPropertyChanged(nameof(ParameterSchemaState));
        OnPropertyChanged(nameof(ParameterSchemaSummary));
    }

    private void ClearParameterSchemaPresentation()
    {
        unchecked { _parameterSchemaRevision++; }
        ApplyParameterSchema(null);
    }

    private void ApplyBindingSnapshot(BehaviourBindingWorkspaceSnapshot snapshot)
    {
        _bindingSnapshot = snapshot;
        var selectedSlotId = SelectedBindingSlot?.SlotId;
        BindingSlots.Clear();
        foreach (var slot in snapshot.Slots)
        {
            BindingSlots.Add(new BehaviourBindingSlotItemViewModel(slot));
        }

        SelectedBindingSlot = selectedSlotId is null
            ? BindingSlots.FirstOrDefault(item => !item.Ready) ?? BindingSlots.FirstOrDefault()
            : BindingSlots.FirstOrDefault(item => item.SlotId == selectedSlotId)
              ?? BindingSlots.FirstOrDefault(item => !item.Ready)
              ?? BindingSlots.FirstOrDefault();
        BindingStatus = snapshot.Summary;
        OnPropertyChanged(nameof(BindingAvailability));
        OnPropertyChanged(nameof(BindingServiceAvailable));
    }

    private void RefreshBindingCandidates()
    {
        var selectedGeometryId = SelectedBindingCandidate?.GeometryId;
        BindingCandidates.Clear();
        foreach (var candidate in SelectedBindingSlot?.CompatibleCandidates ?? [])
        {
            BindingCandidates.Add(candidate);
        }

        SelectedBindingCandidate = selectedGeometryId is null
            ? BindingCandidates.FirstOrDefault(item =>
                  string.Equals(item.GeometryId, SelectedBindingSlot?.GeometryId, StringComparison.Ordinal))
              ?? BindingCandidates.FirstOrDefault()
            : BindingCandidates.FirstOrDefault(item => item.GeometryId == selectedGeometryId)
              ?? BindingCandidates.FirstOrDefault();
    }

    private void ApplyCommandResult(BehaviourPackageCommandResult result)
    {
        LastOperationDetails = FormatResult(result);
        StatusMessage = result.Message;
    }

    private void ClearPreparedOperation()
    {
        PreparedOperation = null;
        OperationConfirmation = string.Empty;
    }

    private void ClearBindingPresentation()
    {
        _bindingSnapshot = null;
        BindingSlots.Clear();
        BindingCandidates.Clear();
        _selectedBindingSlot = null;
        _selectedBindingCandidate = null;
        OnPropertyChanged(nameof(SelectedBindingSlot));
        OnPropertyChanged(nameof(SelectedBindingCandidate));
        OnPropertyChanged(nameof(SelectedBindingDetails));
        RaiseCommandStates();
    }

    private (ConnectionRecord Connection, BehaviourLibraryItemViewModel Entry) RequireSelection()
        => (
            SelectedConnection
            ?? throw new InvalidOperationException("Select a Logos connection first."),
            SelectedEntry
            ?? throw new InvalidOperationException("Select a behaviour package first."));

    private bool CanRefresh() => !Busy && SelectedConnection is not null;

    private bool CanImport() => !Busy && !string.IsNullOrWhiteSpace(ImportDirectory);

    private bool CanExport() => !Busy &&
                                SelectedEntry?.HasLocalPackage == true &&
                                !string.IsNullOrWhiteSpace(ExportDirectory);

    private bool CanRemoveLocal() => !Busy &&
                                     SelectedEntry?.HasLocalPackage == true &&
                                     BehaviourOperationConfirmation.Matches(
                                         LocalRemoveConfirmation,
                                         RequiredLocalRemoveConfirmation);

    private bool CanValidate() => !Busy &&
                                  SelectedConnection is not null &&
                                  SelectedEntry?.HasLocalPackage == true;

    private bool CanPrepareInstall() => !Busy &&
                                        SelectedConnection is not null &&
                                        SelectedEntry?.HasLocalPackage == true &&
                                        SelectedEntry.HasRemotePackage == false;

    private bool CanPrepareUpdate() => !Busy &&
                                       SelectedConnection is not null &&
                                       SelectedEntry?.HasLocalPackage == true &&
                                       SelectedEntry.HasRemotePackage;

    private bool CanPrepareRemoveRemote() => !Busy &&
                                             SelectedConnection is not null &&
                                             SelectedEntry?.HasRemotePackage == true;

    private bool CanExecutePreparedOperation() => !Busy &&
                                                  PreparedOperation?.Allowed == true &&
                                                  BehaviourOperationConfirmation.Matches(
                                                      OperationConfirmation,
                                                      RequiredOperationConfirmation);

    private bool CanInspectBindings() => !Busy &&
                                         SelectedConnection is not null &&
                                         SelectedEntry?.HasRemotePackage == true &&
                                         !string.IsNullOrWhiteSpace(SelectedEntry.Identity.Version);

    private bool CanSetBinding() => !Busy &&
                                    _bindingSnapshot?.Available == true &&
                                    SelectedBindingSlot is not null &&
                                    SelectedBindingCandidate?.Compatible == true;

    private bool CanClearBinding() => !Busy &&
                                      _bindingSnapshot?.Available == true &&
                                      SelectedBindingSlot?.Bound == true;

    private void RaiseCommandStates()
    {
        _refreshCommand.RaiseCanExecuteChanged();
        _importCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
        _removeLocalCommand.RaiseCanExecuteChanged();
        _validateCommand.RaiseCanExecuteChanged();
        _prepareInstallCommand.RaiseCanExecuteChanged();
        _prepareUpdateCommand.RaiseCanExecuteChanged();
        _prepareRemoveRemoteCommand.RaiseCanExecuteChanged();
        _executePreparedOperationCommand.RaiseCanExecuteChanged();
        _inspectBindingsCommand.RaiseCanExecuteChanged();
        _setBindingCommand.RaiseCanExecuteChanged();
        _clearBindingCommand.RaiseCanExecuteChanged();
    }

    private async void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        try
        {
            var connectionId = SelectedConnection?.Id;
            if (connectionId is null)
            {
                return;
            }

            var snapshot = _workspace.GetSnapshot(connectionId);
            await _dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
        }
        catch
        {
            // A later explicit refresh reports the underlying failure to the operator.
        }
    }

    private async void OnBindingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var connectionId = SelectedConnection?.Id;
            var identity = SelectedEntry?.Identity;
            if (connectionId is null || identity is null)
            {
                return;
            }

            var snapshot = _bindings.GetSnapshot(connectionId, identity);
            if (snapshot is not null)
            {
                await _dispatcher.InvokeAsync(() => ApplyBindingSnapshot(snapshot));
            }
        }
        catch
        {
            // Binding mutations already return an operator-visible result.
        }
    }

    private async void OnDeploymentChanged(object? sender, EventArgs e)
    {
        try
        {
            var result = _deployment.LastResult;
            if (result is not null)
            {
                await _dispatcher.InvokeAsync(() => LastOperationDetails = FormatResult(result));
            }
        }
        catch
        {
            // The command path also projects the result directly.
        }
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedConnection is null || !Connections.Any(item => item.Id == SelectedConnection.Id))
        {
            SelectedConnection = Connections
                .OrderByDescending(item => item.State == AvailabilityState.Online)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        else
        {
            SelectedConnection = Connections.First(item => item.Id == SelectedConnection.Id);
        }
    }

    private static string FormatAssessment(BehaviourPackageOperationAssessment assessment)
    {
        var lines = new List<string> { assessment.Summary };
        lines.AddRange(assessment.Blockers.Select(item => $"BLOCKER · {item}"));
        lines.AddRange(assessment.Warnings.Select(item => $"WARNING · {item}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatResult(BehaviourPackageCommandResult result)
    {
        var lines = new List<string>
        {
            $"{result.Operation} {result.Identity.Key}: {result.State}",
            result.Message,
            $"Verified: {result.Verified}",
            $"Expected local SHA: {result.ExpectedLocalSha256 ?? "unknown"}",
            $"Observed Logos SHA: {result.ObservedRemoteSha256 ?? "unknown"}"
        };
        lines.AddRange(result.Warnings.Select(item => $"WARNING · {item}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var normalized = new string(value
            .Select(character => invalid.Contains(character) || character is '/' or '\\' or ':'
                ? '-'
                : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "behaviour-package" : normalized;
    }
}
