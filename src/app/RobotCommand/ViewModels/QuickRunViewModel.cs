using System.Windows.Input;
using Microsoft.Extensions.Logging;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Missions;
using RobotCommand.Services.Operations;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

/// <summary>
/// Compact launch composer for a single installed Logos behaviour. Package
/// management, compatibility reconciliation, and geometry binding ownership
/// live in the shared Behaviours services and workspace rather than here.
/// </summary>
public sealed class QuickRunViewModel : ObservableObject, IDisposable
{
    private static readonly Action<ILogger, double, string, Exception?> BusyWarningLog =
        LoggerMessage.Define<double, string>(
            LogLevel.Warning,
            new EventId(1010, nameof(BusyWarningLog)),
            "Quick Run has remained busy for {ElapsedSeconds:0.0}s. status={Status}");
    private readonly IOperationalRunService _runs;
    private readonly IEntityStore<string, VehicleRecord> _vehiclesStore;
    private readonly IEntityStore<string, ConnectionRecord> _connectionsStore;
    private readonly IEntityStore<string, RuntimeRecord>? _runtimesStore;
    private readonly ISelectionService? _selection;
    private readonly IUiDispatcher _dispatcher;
    private readonly IOperationalInspectionTargetService _inspectionTargets;
    private readonly OperatorControlsViewModel? _operatorControls;
    private readonly ILogger<QuickRunViewModel>? _logger;
    private readonly BehaviourParameterFormViewModel _parameterForm = new();
    private readonly Timer _expiryTimer;

    private IReadOnlyList<QuickRunVehicleOption> _vehicles = [];
    private IReadOnlyList<BehaviourPackageOption> _behaviours = [];
    private IReadOnlyList<string> _warnings = [];
    private IReadOnlyList<string> _blockers = [];
    private IReadOnlyList<QuickRunStepPresentation> _steps = [];
    private QuickRunVehicleOption? _selectedVehicle;
    private BehaviourPackageOption? _selectedBehaviour;
    private OperationalRunPreparation? _preparation;
    private OperationalRunResult? _result;
    private string _objective = "Execute a bounded SITL Quick Run.";
    private string _status = "Quick Run is ready to inspect the connected Logos runtime.";
    private string _detail = "Select a vehicle and an installed behaviour, then prepare the run.";
    private bool _rejectOnWarnings;
    private bool _synchronizingParameterForm;
    private bool _isBusy;
    private bool _isInitialized;
    private bool _isPanelOpen = true;
    private int _selectionRevision;
    private int _refreshRequested;
    private CancellationTokenSource? _refreshDebounce;
    private DateTimeOffset? _busySince;
    private DateTimeOffset _lastBusyWarning;
    private int _disposed;

    public QuickRunViewModel(
        IOperationalRunService runs,
        IEntityStore<string, VehicleRecord> vehiclesStore,
        IEntityStore<string, ConnectionRecord> connectionsStore,
        IUiDispatcher dispatcher,
        IOperationalInspectionTargetService? inspectionTargets = null,
        ISelectionService? selection = null,
        IEntityStore<string, RuntimeRecord>? runtimesStore = null,
        ILogger<QuickRunViewModel>? logger = null,
        OperatorControlsViewModel? operatorControls = null)
    {
        _runs = runs;
        _vehiclesStore = vehiclesStore;
        _connectionsStore = connectionsStore;
        _runtimesStore = runtimesStore;
        _selection = selection;
        _dispatcher = dispatcher;
        _inspectionTargets = inspectionTargets ?? NullOperationalInspectionTargetService.Instance;
        _operatorControls = operatorControls;
        _logger = logger;

        if (_selection is not null)
        {
            _selection.Changed += OnSelectionChanged;
        }

        _runs.Changed += OnRunsChanged;
        _parameterForm.Changed += OnParameterFormChanged;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        RefreshBehavioursCommand = new AsyncRelayCommand(
            token => RefreshBehavioursAsync(refresh: true, token),
            () => !IsBusy && SelectedVehicle is not null);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, CanPrepare);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, CanLaunch);
        DiscardCommand = new AsyncRelayCommand(DiscardAsync, CanDiscard);
        ResetParametersCommand = new RelayCommand(_ => ParameterForm.Reset(), _ => InputsEnabled);
        TogglePanelCommand = new RelayCommand(_ => IsPanelOpen = !IsPanelOpen);

        _expiryTimer = new Timer(OnExpiryTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public IReadOnlyList<QuickRunVehicleOption> Vehicles
    {
        get => _vehicles;
        private set => SetProperty(ref _vehicles, value);
    }

    public IReadOnlyList<BehaviourPackageOption> Behaviours
    {
        get => _behaviours;
        private set => SetProperty(ref _behaviours, value);
    }

    public QuickRunVehicleOption? SelectedVehicle
    {
        get => _selectedVehicle;
        set
        {
            if (!SetProperty(ref _selectedVehicle, value))
            {
                return;
            }

            _selectionRevision++;
            OnPropertyChanged(nameof(HasSelectedVehicle));
            OnPropertyChanged(nameof(SelectedVehicleSummary));
            if (value is not null && _selection is not null)
            {
                _selection.Select(new OperationalSelection(
                    SelectionKind.Vehicle,
                    value.VehicleId,
                    value.VehicleName,
                    value.ConnectionName,
                    []));
            }
            ClearPreparedState("Vehicle selection changed. Prepare the run again.");
            Behaviours = [];
            SelectedBehaviour = null;
            RaiseCommandStates();
            if (value is not null && !IsBusy)
            {
                _ = LoadBehavioursAfterSelectionAsync(value, _selectionRevision);
            }
        }
    }

    public bool HasSelectedVehicle => SelectedVehicle is not null &&
                                      (_selection is null || _selection.SelectedUnitIds.Count <= 1);

    public OperatorControlsViewModel? OperatorControls => _operatorControls;

    public string SelectedVehicleSummary => SelectedVehicle is null
        ? "Select a unit from the Units panel to target Quick Run."
        : $"Target: {SelectedVehicle.VehicleName} · {SelectedVehicle.ConnectionName}";

    public BehaviourPackageOption? SelectedBehaviour
    {
        get => _selectedBehaviour;
        set
        {
            var previousKey = _selectedBehaviour?.Key;
            var existingParameters = ParameterForm.Build();
            var retainedJson = existingParameters.Valid
                ? existingParameters.CanonicalJson
                : ParameterForm.RawJson;
            if (!SetProperty(ref _selectedBehaviour, value))
            {
                return;
            }

            ClearPreparedState("Behaviour selection changed. Prepare the run again.");
            _synchronizingParameterForm = true;
            try
            {
                ParameterForm.Load(
                    value?.ParameterSchema,
                    string.Equals(previousKey, value?.Key, StringComparison.Ordinal) ? retainedJson : "{}");
            }
            finally
            {
                _synchronizingParameterForm = false;
            }
            OnPropertyChanged(nameof(SelectedBehaviourDetail));
            OnPropertyChanged(nameof(SelectedGeometrySummary));
            OnPropertyChanged(nameof(HasSelectedGeometry));
            OnPropertyChanged(nameof(ParametersJson));
            RaiseCommandStates();
        }
    }

    public string SelectedBehaviourDetail => SelectedBehaviour is null
        ? "No compatible installed behaviour selected."
        : string.Join(Environment.NewLine,
            string.IsNullOrWhiteSpace(SelectedBehaviour.Description)
                ? $"{SelectedBehaviour.BehaviourId}@{SelectedBehaviour.Version}"
                : SelectedBehaviour.Description,
            $"Status: {SelectedBehaviour.Status} · Channel: {SelectedBehaviour.Channel}",
            SelectedBehaviour.ParameterSchema?.Summary
            ?? "No local operator parameter schema is available; raw JSON will be used.");

    public bool HasSelectedGeometry => SelectedBehaviour?.GeometrySlots.Count > 0;

    public string SelectedGeometrySummary => SelectedBehaviour is null
        ? "Select a behaviour to inspect its geometry readiness."
        : SelectedBehaviour.GeometryBindingSummary;

    public string Objective
    {
        get => _objective;
        set
        {
            if (SetProperty(ref _objective, value))
            {
                ClearPreparedState("Objective changed. Prepare the run again.");
                RaiseCommandStates();
            }
        }
    }

    public BehaviourParameterFormViewModel ParameterForm => _parameterForm;

    /// <summary>
    /// Compatibility surface for tests, automation, and packages without a usable
    /// parameter schema. Typed fields still serialize through this property.
    /// </summary>
    public string ParametersJson
    {
        get
        {
            var result = ParameterForm.Build();
            return result.Valid ? result.CanonicalJson : ParameterForm.RawJson;
        }
        set
        {
            ParameterForm.UseRawJson = true;
            ParameterForm.RawJson = value;
        }
    }

    public bool RejectOnWarnings
    {
        get => _rejectOnWarnings;
        set
        {
            if (SetProperty(ref _rejectOnWarnings, value))
            {
                ClearPreparedState("Warning policy changed. Prepare the run again.");
            }
        }
    }

    public OperationalRunPreparation? Preparation
    {
        get => _preparation;
        private set
        {
            if (!SetProperty(ref _preparation, value))
            {
                return;
            }

            Warnings = value?.Warnings ?? [];
            Blockers = value?.Blockers ?? [];
            OnPropertyChanged(nameof(HasPreparation));
            OnPropertyChanged(nameof(PreparationSummary));
            OnPropertyChanged(nameof(PreparationExpiry));
            OnPropertyChanged(nameof(InputsEnabled));
            RaiseCommandStates();
        }
    }

    public OperationalRunResult? Result
    {
        get => _result;
        private set
        {
            if (!SetProperty(ref _result, value))
            {
                return;
            }

            Steps = value?.Steps.Select(QuickRunStepPresentation.From).ToArray() ?? [];
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ResultSummary));
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set
        {
            if (SetProperty(ref _warnings, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public IReadOnlyList<string> Blockers
    {
        get => _blockers;
        private set
        {
            if (SetProperty(ref _blockers, value))
            {
                OnPropertyChanged(nameof(HasBlockers));
            }
        }
    }

    public IReadOnlyList<QuickRunStepPresentation> Steps
    {
        get => _steps;
        private set => SetProperty(ref _steps, value);
    }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(InputsEnabled));
            RaiseCommandStates();
            if (value)
            {
                _busySince = DateTimeOffset.UtcNow;
            }
            else
            {
                _busySince = null;
                if (_isInitialized && Volatile.Read(ref _refreshRequested) != 0)
                {
                    _ = RefreshRequestedAsync();
                }
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public bool InputsEnabled => !IsBusy && !IsActiveStage(Preparation?.Stage);

    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (SetProperty(ref _isPanelOpen, value))
            {
                OnPropertyChanged(nameof(IsPanelClosed));
            }
        }
    }

    public bool IsPanelClosed => !IsPanelOpen;

    public bool HasPreparation => Preparation is not null;

    public bool HasResult => Result is not null;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasBlockers => Blockers.Count > 0;

    public string PreparationSummary => Preparation is null
        ? "No prepared run."
        : $"{Preparation.Behaviour.Label} · {Preparation.Stage}";

    public string PreparationExpiry
    {
        get
        {
            if (Preparation is null)
            {
                return string.Empty;
            }

            var remaining = Preparation.ExpiresAt - DateTimeOffset.UtcNow;
            return remaining <= TimeSpan.Zero
                ? "Preparation expired"
                : $"Expires in {Math.Ceiling(remaining.TotalSeconds):0} seconds";
        }
    }

    public string ResultSummary => Result is null
        ? string.Empty
        : $"{Result.Stage}: {Result.Message}";

    public ICommand RefreshCommand { get; }

    public ICommand RefreshBehavioursCommand { get; }

    public ICommand PrepareCommand { get; }

    public ICommand LaunchCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand ResetParametersCommand { get; }

    public ICommand TogglePanelCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _isInitialized = true;
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IsBusy = true;
        try
        {
            var previousVehicleId = SelectedVehicle?.VehicleId;
            var previousConnectionId = SelectedVehicle?.ConnectionId;
            Vehicles = BuildVehicleOptions();
            SelectedVehicle = ResolveCurrentVehicleOption(previousVehicleId, previousConnectionId);

            if (SelectedVehicle is null)
            {
                Status = "No Logos vehicle is available.";
                Detail = "Connect to a Logos runtime and wait for vehicle discovery.";
                Behaviours = [];
                SelectedBehaviour = null;
                return;
            }

            await RefreshBehavioursCoreAsync(SelectedVehicle, refresh: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Quick Run refresh cancelled.";
            Detail = "The current selections were left unchanged.";
        }
        catch (Exception ex)
        {
            Status = "Quick Run refresh failed.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
            if (Interlocked.Exchange(ref _refreshRequested, 0) != 0 && _isInitialized)
            {
                _ = RefreshRequestedAsync();
            }
        }
    }

    private async Task RefreshBehavioursAsync(bool refresh, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var selected = SelectedVehicle;
        if (selected is null)
        {
            Behaviours = [];
            SelectedBehaviour = null;
            Status = "Select a vehicle.";
            Detail = "Behaviour compatibility is evaluated against the selected Logos vehicle.";
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshBehavioursCoreAsync(selected, refresh, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Behaviour refresh cancelled.";
            Detail = "No command was sent to Logos.";
        }
        catch (Exception ex)
        {
            Behaviours = [];
            SelectedBehaviour = null;
            Status = "Could not list compatible behaviours.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadBehavioursAfterSelectionAsync(
        QuickRunVehicleOption selected,
        int revision)
    {
        IsBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LoadBehavioursForSelectionAsync(selected, revision, refresh: false, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (revision == _selectionRevision)
            {
                Status = "Behaviour lookup timed out.";
                Detail = "Use Reload after the Logos behaviour inventory becomes available.";
            }
        }
        finally
        {
            IsBusy = false;
            if (revision != _selectionRevision && _isInitialized)
            {
                RequestRefresh();
            }
        }
    }

    private async Task RefreshBehavioursCoreAsync(
        QuickRunVehicleOption selected,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var revision = _selectionRevision;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await LoadBehavioursForSelectionAsync(selected, revision, refresh, timeout.Token);
    }

    private async Task LoadBehavioursForSelectionAsync(
        QuickRunVehicleOption selected,
        int revision,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var previousKey = SelectedBehaviour?.Key;
        var behaviours = await _runs.ListCompatibleBehavioursAsync(
            selected.ConnectionId,
            selected.VehicleId,
            refresh,
            cancellationToken);
        if (revision != _selectionRevision ||
            !string.Equals(SelectedVehicle?.VehicleId, selected.VehicleId, StringComparison.Ordinal) ||
            !string.Equals(SelectedVehicle?.ConnectionId, selected.ConnectionId, StringComparison.Ordinal))
        {
            return;
        }

        Behaviours = behaviours;
        SelectedBehaviour = Behaviours.FirstOrDefault(item =>
                                string.Equals(item.Key, previousKey, StringComparison.Ordinal))
                            ?? (Behaviours.Count > 0 ? Behaviours[0] : null);
        Status = Behaviours.Count == 0
            ? "No launch-ready behaviour is installed."
            : "Quick Run inputs are ready.";
        Detail = Behaviours.Count == 0
            ? "Use the Behaviours workspace to install a compatible version and resolve required geometry bindings."
            : $"Found {Behaviours.Count} compatible, binding-ready behaviour package(s) for {selected.VehicleName}.";
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var vehicle = SelectedVehicle;
        var behaviour = SelectedBehaviour;
        if (vehicle is null || behaviour is null)
        {
            Status = "Quick Run is not ready to prepare.";
            Detail = "Select a vehicle and a compatible behaviour.";
            return;
        }

        if (!TryValidateParameters(out var parametersJson, out var parametersError))
        {
            Status = "Parameters JSON is invalid.";
            Detail = parametersError;
            return;
        }

        IsBusy = true;
        Result = null;
        try
        {
            Status = "Preparing Quick Run...";
            Detail = "Refreshing the installed package and bindings, then asking Logos to validate the generated mission and task.";
            var preparation = await _runs.PrepareAsync(
                new OperationalRunRequest(
                    vehicle.ConnectionId,
                    vehicle.VehicleId,
                    behaviour.BehaviourId,
                    behaviour.Version,
                    Objective.Trim(),
                    parametersJson,
                    RejectOnWarnings: RejectOnWarnings),
                cancellationToken);
            Preparation = preparation;
            Status = preparation.CanLaunch
                ? "Quick Run prepared."
                : "Quick Run preparation is blocked.";
            Detail = preparation.CanLaunch
                ? "Review the preparation and launch it before the confirmation window expires."
                : string.Join(" ", preparation.Blockers.DefaultIfEmpty(preparation.TaskValidation.Summary));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Quick Run preparation cancelled.";
            Detail = "No mission was started.";
        }
        catch (Exception ex)
        {
            Status = "Quick Run preparation failed.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var preparation = Preparation;
        if (preparation is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Status = "Launching Quick Run...";
            Detail = "Rechecking the installed behaviour and bindings before publishing and starting the plan.";
            var result = await _runs.LaunchAsync(preparation.OperationId, cancellationToken);
            Result = result;
            SynchronizePreparation();
            Status = result.Accepted
                ? "Quick Run accepted by Logos."
                : "Quick Run was not launched.";
            Detail = result.CleanupAttempted && !string.IsNullOrWhiteSpace(result.CleanupMessage)
                ? $"{result.Message} Cleanup: {result.CleanupMessage}"
                : result.Message;

            if (result.Accepted && SelectedVehicle is { } selected)
            {
                _inspectionTargets.Publish(
                    new OperationalExecutionTarget(
                        selected.ConnectionId,
                        selected.VehicleId,
                        selected.VehicleName,
                        selected.LogosInstanceId,
                        result.MissionId,
                        result.TaskId,
                        result.MissionExecutionId,
                        result.TaskExecutionId,
                        result.OperationId,
                        DateTimeOffset.UtcNow),
                    OperationalInspectionSource.QuickRun,
                    $"Quick Run launched {result.MissionId} / {result.TaskId}.",
                    openInspector: true);
                Detail = $"{Detail} The execution was handed to the persistent Inspector.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Quick Run launch cancelled.";
            Detail = "Review command history and mission state before attempting another launch.";
        }
        catch (Exception ex)
        {
            Status = "Quick Run launch failed.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var operationId = Preparation?.OperationId;
        if (operationId is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _runs.DiscardAsync(operationId, cancellationToken);
            Preparation = null;
            Result = null;
            Status = "Quick Run preparation discarded.";
            Detail = "No mission or task lifecycle command was submitted.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Discard cancelled.";
            Detail = "The existing preparation remains available until it expires.";
        }
        catch (Exception ex)
        {
            Status = "Could not discard Quick Run preparation.";
            Detail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private QuickRunVehicleOption[] BuildVehicleOptions()
    {
        var connections = _connectionsStore.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var result = new List<QuickRunVehicleOption>();
        foreach (var vehicle in _vehiclesStore.Items.Where(item => !item.IsGhost))
        {
            var routes = vehicle.ConnectionIds
                .Select(id => connections.TryGetValue(id, out var connection) ? connection : null)
                .Where(connection => connection is not null)
                .Cast<ConnectionRecord>()
                .OrderByDescending(connection => AvailabilityRank(connection.State))
                .ThenBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var connection in routes)
            {
                result.Add(new QuickRunVehicleOption(
                    vehicle.Id,
                    vehicle.Name,
                    connection.Id,
                    connection.Name,
                    vehicle.LogosInstanceId,
                    vehicle.State,
                    connection.State,
                    vehicle.VehicleClass,
                    vehicle.Domain,
                    vehicle.Readiness,
                    vehicle.Health));
            }
        }

        return result
            .OrderByDescending(item => item.CanPrepare)
            .ThenByDescending(item => AvailabilityRank(item.VehicleState))
            .ThenBy(item => item.VehicleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ConnectionName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private QuickRunVehicleOption? ResolveCurrentVehicleOption(
        string? previousVehicleId,
        string? previousConnectionId)
    {
        var current = _selection?.Current;
        string? vehicleId = current?.Kind == SelectionKind.Vehicle ? current.Id : null;
        if (vehicleId is not null && _vehiclesStore.TryGet(vehicleId, out var selectedVehicle) && selectedVehicle?.IsGhost == true)
        {
            return null;
        }
        HashSet<string> connectionIds = new(StringComparer.Ordinal);
        if (current?.Kind == SelectionKind.Runtime &&
            !string.IsNullOrWhiteSpace(current.Id) &&
            _runtimesStore?.TryGet(current.Id, out var runtime) == true &&
            runtime is not null)
        {
            vehicleId = runtime.VehicleId;
            connectionIds = runtime.ConnectionIds.ToHashSet(StringComparer.Ordinal);
        }

        var derived = Vehicles.FirstOrDefault(item =>
            vehicleId is not null &&
            string.Equals(item.VehicleId, vehicleId, StringComparison.Ordinal) &&
            (connectionIds.Count == 0 || connectionIds.Contains(item.ConnectionId)));
        if (derived is not null)
        {
            return derived;
        }

        // Vehicle and connection projections are published independently.
        // Preserve an explicit unit selection during the short interval before
        // the combined Quick Run option list catches up.
        var fallbackVehicleId = vehicleId ?? previousVehicleId;
        if (fallbackVehicleId is not null &&
            _vehiclesStore.TryGet(fallbackVehicleId, out var fallbackVehicle) &&
            fallbackVehicle is not null)
        {
            var fallbackConnection = fallbackVehicle.ConnectionIds
                .Select(id => _connectionsStore.TryGet(id, out var connection) ? connection : null)
                .Where(connection => connection is not null)
                .Cast<ConnectionRecord>()
                .OrderByDescending(connection => AvailabilityRank(connection.State))
                .ThenBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (fallbackConnection is not null)
            {
                return new QuickRunVehicleOption(
                    fallbackVehicle.Id,
                    fallbackVehicle.Name,
                    fallbackConnection.Id,
                    fallbackConnection.Name,
                    fallbackVehicle.LogosInstanceId,
                    fallbackVehicle.State,
                    fallbackConnection.State,
                    fallbackVehicle.VehicleClass,
                    fallbackVehicle.Domain,
                    fallbackVehicle.Readiness,
                    fallbackVehicle.Health);
            }
        }

        return Vehicles.FirstOrDefault(item =>
                   string.Equals(item.VehicleId, previousVehicleId, StringComparison.Ordinal) &&
                   string.Equals(item.ConnectionId, previousConnectionId, StringComparison.Ordinal))
               ?? Vehicles.FirstOrDefault(item => item.CanPrepare)
               ?? (Vehicles.Count > 0 ? Vehicles[0] : null);
    }

    private bool TryValidateParameters(out string canonicalJson, out string error)
    {
        var result = ParameterForm.Build();
        canonicalJson = result.CanonicalJson;
        error = result.Summary;
        return result.Valid;
    }

    private void OnParameterFormChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ParametersJson));
        if (!_synchronizingParameterForm)
        {
            ClearPreparedState("Parameters changed. Prepare the run again.");
        }
        RaiseCommandStates();
    }

    private void ClearPreparedState(string detail)
    {
        if (Preparation is null && Result is null)
        {
            return;
        }

        var discarded = Preparation;
        Preparation = null;
        Result = null;
        Status = "Quick Run inputs changed.";
        Detail = detail;
        if (discarded is not null && !IsActiveStage(discarded.Stage))
        {
            _ = DiscardSupersededAsync(discarded.OperationId);
        }
    }

    private async Task DiscardSupersededAsync(string operationId)
    {
        try
        {
            await _runs.DiscardAsync(operationId, CancellationToken.None);
        }
        catch
        {
            // A superseded local preparation must not interrupt operator editing.
        }
    }

    private bool CanPrepare()
        => InputsEnabled &&
           HasSelectedVehicle &&
           SelectedVehicle?.CanPrepare == true &&
           SelectedBehaviour is not null &&
           ParameterForm.IsValid &&
           !string.IsNullOrWhiteSpace(Objective);

    private bool CanLaunch()
        => !IsBusy && HasSelectedVehicle && Preparation?.CanLaunch == true;

    private bool CanDiscard()
        => !IsBusy && Preparation is not null && !IsActiveStage(Preparation.Stage);

    private static bool IsActiveStage(OperationalRunStage? stage)
        => stage is
            OperationalRunStage.PublishingMission or
            OperationalRunStage.PublishingTask or
            OperationalRunStage.ValidatingAssignment or
            OperationalRunStage.AssigningTask or
            OperationalRunStage.StartingMission or
            OperationalRunStage.StartingTask or
            OperationalRunStage.Running;

    private void RaiseCommandStates()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RefreshBehavioursCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiscardCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ResetParametersCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_isInitialized || _selection is null)
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelectedVehicle));
        RaiseCommandStates();

        var current = _selection.Current;
        if (current.Kind is not SelectionKind.Vehicle and not SelectionKind.Runtime)
        {
            return;
        }

        if (current.Kind == SelectionKind.Vehicle &&
            string.Equals(current.Id, SelectedVehicle?.VehicleId, StringComparison.Ordinal))
        {
            return;
        }

        RequestRefresh();
    }

    private void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshRequested, 1);
        _refreshDebounce?.Cancel();
        var cancellation = _refreshDebounce = new CancellationTokenSource();
        _ = DebouncedRefreshAsync(cancellation.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(75, cancellationToken);
            if (!IsBusy)
            {
                await RefreshRequestedAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this refresh request.
        }
    }

    private async Task RefreshRequestedAsync()
    {
        if (Interlocked.Exchange(ref _refreshRequested, 0) == 0 || IsBusy)
        {
            return;
        }

        await RefreshAsync();
    }

    private void OnRunsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = _dispatcher.InvokeAsync(SynchronizePreparation);
        }
    }

    private void SynchronizePreparation()
    {
        var operationId = Preparation?.OperationId;
        if (operationId is null)
        {
            return;
        }

        var current = _runs.Preparations.FirstOrDefault(item =>
            string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
        if (current is not null)
        {
            Preparation = current;
        }
    }

    private void OnExpiryTimer(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (IsBusy && _busySince is not null &&
            DateTimeOffset.UtcNow - _busySince.Value > TimeSpan.FromSeconds(10) &&
            DateTimeOffset.UtcNow - _lastBusyWarning > TimeSpan.FromSeconds(10))
        {
            _lastBusyWarning = DateTimeOffset.UtcNow;
            if (_logger is not null)
            {
                BusyWarningLog(
                    _logger,
                    (DateTimeOffset.UtcNow - _busySince.Value).TotalSeconds,
                    Status,
                    null);
            }
        }

        if (Preparation is null)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(PreparationExpiry));
            OnPropertyChanged(nameof(PreparationSummary));
            RaiseCommandStates();
        });
    }

    private static int AvailabilityRank(AvailabilityState state)
        => state switch
        {
            AvailabilityState.Online => 6,
            AvailabilityState.Degraded => 5,
            AvailabilityState.Connecting => 4,
            AvailabilityState.Reconnecting => 3,
            AvailabilityState.Stale => 2,
            AvailabilityState.Offline => 1,
            _ => 0
        };

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _runs.Changed -= OnRunsChanged;
        _parameterForm.Changed -= OnParameterFormChanged;
        if (_selection is not null)
        {
            _selection.Changed -= OnSelectionChanged;
        }

        _refreshDebounce?.Cancel();
        _refreshDebounce?.Dispose();
        _expiryTimer.Dispose();
    }
}
