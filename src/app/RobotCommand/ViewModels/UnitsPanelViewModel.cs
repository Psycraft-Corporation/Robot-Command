using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.ManualControl;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Reconciliation;
using RobotCommand.Services.Simulation;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class UnitsPanelViewModel : ObservableObject
{
    public const int MaxGhostCount = 20;

    private readonly IEntityStore<string, RuntimeRecord> _runtimeStore;
    private readonly IEntityStore<string, ConnectionRecord> _connectionStore;
    private readonly IEntityStore<string, VehicleRecord>? _vehicleStore;
    private readonly IEntityStore<string, VehicleTelemetryRecord>? _telemetryStore;
    private readonly IEntityStore<string, OperationalCommandRecord>? _commandStore;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot>? _diagnosticsStore;
    private readonly ISelectionService _selection;
    private readonly IGhostUnitService? _ghosts;
    private readonly IMapViewportState? _viewportState;
    private readonly OperatorControlsViewModel? _operatorControls;
    private readonly IManualControlService? _manualControl;
    private readonly IUnitDefinitionService? _reconciliation;
    private readonly IUnitSettingsService? _unitSettings;
    private readonly IUnitObservationWorkflow _observations;
    private readonly IGhostUnitWorkflow? _ghostWorkflow;
    private readonly IFlightMissionWorkflow? _flightMissions;
    private readonly ITeamWorkflow? _teams;
    private readonly ITeamSelectionWorkflow? _teamSelection;
    private readonly IFormationLockWorkflow? _formation;
    private readonly IFormationAssignmentWorkflow? _formationAssignment;
    private readonly IFormationAuthoringWorkflow? _formationAuthoring;
    private readonly UnitsLibraryViewModel? _unitLibrary;
    private readonly IReviewedOperationWorkflow? _reviewed;
    private readonly IGhostProfileWorkflow _ghostProfiles;
    private ReviewedOperationSnapshot? _pendingFormationOperation;
    private readonly IUiDispatcher _dispatcher;
    public IMapNavigationController? MapNavigation { get; }
    private RuntimeRecord? _selectedUnit;
    private UnitListItemViewModel? _selectedUnitItem;
    private bool _isCreatingGhost;
    private int _ghostCount = 1;
    private string _teamStatus = string.Empty;
    private bool _isApplyingTeamLayout;
    private bool _isRefreshingUnits;
    private int _unitRefreshQueued;
    private int _teamLayoutQueued;
    private string _teamGoToLatitude = string.Empty;
    private string _teamGoToLongitude = string.Empty;
    private string _teamAltitude = string.Empty;
    private string _teamRotation = string.Empty;
    private string _teamScale = "100";
    private GhostProfileSnapshot? _selectedGhostProfile;
    private FormationWorkflowSnapshot? _selectedAuthoredFormation;
    private string _teamEntryHeading = "0";

    public UnitsPanelViewModel(
        IEntityStore<string, RuntimeRecord> runtimeStore,
        IEntityStore<string, ConnectionRecord> connectionStore,
        ISelectionService selection,
        IEntityStore<string, VehicleRecord>? vehicleStore = null,
        IEntityStore<string, VehicleTelemetryRecord>? telemetryStore = null,
        IEntityStore<string, OperationalCommandRecord>? commandStore = null,
        IGhostUnitService? ghosts = null,
        IMapViewportState? viewportState = null,
        OperatorControlsViewModel? operatorControls = null,
        IMapNavigationController? mapNavigation = null,
        IManualControlService? manualControl = null,
        IEntityStore<string, VehicleDiagnosticsSnapshot>? diagnosticsStore = null,
        IUnitDefinitionService? reconciliation = null,
        IUnitSettingsService? unitSettings = null,
        IUnitObservationWorkflow? observations = null,
        IGhostUnitWorkflow? ghostWorkflow = null,
        IFlightMissionWorkflow? flightMissions = null,
        ITeamWorkflow? teams = null,
        IUiDispatcher? dispatcher = null,
        ITeamSelectionWorkflow? teamSelection = null,
        IFormationLockWorkflow? formation = null,
        IReviewedOperationWorkflow? reviewed = null,
        IGhostProfileWorkflow? ghostProfiles = null,
        IFormationAssignmentWorkflow? formationAssignment = null,
        IFormationAuthoringWorkflow? formationAuthoring = null,
        UnitsLibraryViewModel? unitLibrary = null)
    {
        _runtimeStore = runtimeStore; _connectionStore = connectionStore; _selection = selection; _vehicleStore = vehicleStore;
        _telemetryStore = telemetryStore; _commandStore = commandStore;
        _diagnosticsStore = diagnosticsStore;
        _ghosts = ghosts; _viewportState = viewportState;
        _operatorControls = operatorControls;
        _manualControl = manualControl;
        _reconciliation = reconciliation;
        _unitSettings = unitSettings;
        _observations = observations ?? EmptyUnitObservationWorkflow.Instance;
        _ghostWorkflow = ghostWorkflow;
        _flightMissions = flightMissions;
        _teams = teams;
        _teamSelection = teamSelection;
        _formation = formation;
        _formationAssignment = formationAssignment;
        _formationAuthoring = formationAuthoring;
        _unitLibrary = unitLibrary;
        _reviewed = reviewed;
        _ghostProfiles = ghostProfiles ?? new GhostProfileWorkflow();
        _dispatcher = dispatcher ?? new InlineUiDispatcher();
        if (_unitSettings is not null) _unitSettings.Changed += (_, _) => RefreshDetails();
        if (_manualControl is not null) _manualControl.Changed += (_, _) => RefreshDetails();
        MapNavigation = mapNavigation;
        if (_operatorControls is not null)
            _operatorControls.QueuedCommandsChanged += OnOperatorQueueChanged;
        Units = new ObservableCollection<UnitListItemViewModel>(); ConnectionSummaries = new ObservableCollection<string>();
        SelectedStatusIndicators = new ObservableCollection<UnitStatusIndicatorViewModel>();
        SelectedConnectionChips = new ObservableCollection<UnitConnectionChipViewModel>();
        GhostProfiles = new ObservableCollection<GhostProfileSnapshot>(_ghostProfiles.Profiles);
        AuthoredFormations = new ObservableCollection<FormationWorkflowSnapshot>(_formationAuthoring?.Formations ?? []);
        _selectedGhostProfile = GhostProfiles.FirstOrDefault(profile => profile.Id == GhostProfileDefaults.Dracula.Id) ?? GhostProfiles.FirstOrDefault();
        ClearSelectionCommand = new RelayCommand(_ => _selection.Clear());
        SelectTeamCommand = new RelayCommand(parameter =>
        {
            if (parameter is string teamId)
                _ = SelectTeamFromHeaderAsync(teamId);
        });
        LockTeamFormationCommand = new AsyncRelayCommand(LockTeamFormationAsync, CanLockTeamFormation);
        UnlockTeamFormationCommand = new AsyncRelayCommand(UnlockTeamFormationAsync, () => IsTeamFormationLocked);
        MoveTeamFormationCommand = new AsyncRelayCommand(MoveTeamFormationAsync, CanMoveTeamFormation);
        PrepareMapTeamGoToCommand = new RelayCommand(
            parameter => _ = PrepareMapTeamGoToAsync(parameter),
            CanPrepareMapTeamGoTo);
        ChangeTeamFormationAltitudeCommand = new AsyncRelayCommand(ChangeTeamFormationAltitudeAsync, CanChangeTeamFormationAltitude);
        RotateTeamFormationCommand = new AsyncRelayCommand(RotateTeamFormationAsync, CanRotateTeamFormation);
        ScaleTeamFormationCommand = new AsyncRelayCommand(ScaleTeamFormationAsync, CanScaleTeamFormation);
        HoldTeamFormationCommand = new AsyncRelayCommand(HoldTeamFormationAsync, () => IsTeamFormationLocked);
        AssignAuthoredFormationCommand = new AsyncRelayCommand(AssignAuthoredFormationAsync, CanAssignAuthoredFormation);
        EnterAuthoredFormationCommand = new AsyncRelayCommand(EnterAuthoredFormationAsync, () => IsTeamFormationAssigned && !IsTeamFormationActive);
        ClearAuthoredFormationCommand = new AsyncRelayCommand(ClearAuthoredFormationAsync, () => IsTeamFormationAssigned);
        // Let the operator press Execute for any visible review card. The
        // reviewed workflow remains the authority and returns the concrete
        // blocker when the plan is no longer executable; the button must not
        // silently no-op because CanExecute changed between refreshes.
        ExecuteFormationOperationCommand = new AsyncRelayCommand(ExecuteFormationOperationAsync, () => PendingFormationOperation is not null);
        CancelFormationOperationCommand = new AsyncRelayCommand(CancelFormationOperationAsync, () => PendingFormationOperation is not null);
        CreateGhostCommand = new RelayCommand(_ => IsCreatingGhost = true);
        ConfirmCreateGhostCommand = new AsyncRelayCommand(CreateGhostAsync, () => IsCreatingGhost);
        CancelCreateGhostCommand = new RelayCommand(_ => IsCreatingGhost = false);
        DeleteGhostCommand = new RelayCommand(parameter => _ = DeleteGhostAsync(parameter));
        ManageUnitConnectionsCommand = new RelayCommand(_ => BeginManageUnitConnections(), _ => CanManageUnitConnections);
        _selection.Changed += OnSelectionChanged;
        ((INotifyCollectionChanged)runtimeStore.Items).CollectionChanged += OnChanged;
        ((INotifyCollectionChanged)connectionStore.Items).CollectionChanged += OnChanged;
        if (_vehicleStore is not null)
        {
            ((INotifyCollectionChanged)_vehicleStore.Items).CollectionChanged += OnChanged;
        }
        if (_telemetryStore is not null)
            ((INotifyCollectionChanged)_telemetryStore.Items).CollectionChanged += OnChanged;
        if (_commandStore is not null)
            ((INotifyCollectionChanged)_commandStore.Items).CollectionChanged += OnChanged;
        if (_diagnosticsStore is not null)
            ((INotifyCollectionChanged)_diagnosticsStore.Items).CollectionChanged += OnChanged;
        if (_reconciliation is not null) _reconciliation.Changed += OnReconciliationChanged;
        _observations.Changed += OnObservationChanged;
        if (_ghostWorkflow is not null) _ghostWorkflow.Changed += OnObservationChanged;
        if (_flightMissions is not null) _flightMissions.Changed += OnMissionChanged;
        if (_teams is not null) _teams.Changed += OnTeamChanged;
        if (_teamSelection is not null) _teamSelection.Changed += OnTeamSelectionChanged;
        if (_formation is not null) _formation.Changed += OnFormationWorkflowChanged;
        if (_formationAssignment is not null) _formationAssignment.Changed += OnFormationAssignmentWorkflowChanged;
        if (_formationAuthoring is not null) _formationAuthoring.Changed += OnFormationAuthoringChanged;
        _ghostProfiles.Changed += OnGhostProfilesChanged;
        LocalizationService.Current.PropertyChanged += OnLocalizationChanged;
    }

    public ObservableCollection<UnitListItemViewModel> Units { get; }
    public ObservableCollection<FormationWorkflowSnapshot> AuthoredFormations { get; }
    public ITeamWorkflow? Teams => _teams;
    public ITeamSelectionWorkflow? TeamSelection => _teamSelection;
    public bool IsTeamSelected => _teamSelection?.Current.IsSelected == true;
    public bool ShowUnitCards => !IsTeamSelected;
    public TeamSelectionWorkflowSnapshot? SelectedTeam => IsTeamSelected ? _teamSelection!.Current : null;
    public string SelectedTeamTitle => SelectedTeam?.TeamName ?? string.Empty;
    public string SelectedTeamMemberSummary => SelectedTeam is { } team ? $"{team.MemberUnitIds.Count} members" : string.Empty;
    public string SelectedTeamOnlineSummary
        => SelectedTeam is not { } team ? string.Empty : $"{team.MemberUnitIds.Count(id => _observations.TryGet(id, out var unit) && unit?.State != ManagedConnectionState.Offline)} online · {team.MemberUnitIds.Count(id => !_observations.TryGet(id, out var unit) || unit?.State == ManagedConnectionState.Offline)} offline";
    public string SelectedTeamReadiness
        => SelectedTeam is not { } team ? string.Empty : team.MemberUnitIds.All(id => _observations.TryGet(id, out var unit) && string.Equals(unit?.Readiness, "Ready", StringComparison.OrdinalIgnoreCase)) ? "Ready" : "Limited";
    public string SelectedTeamBlockers
        => SelectedTeam is not { } team ? string.Empty : $"{team.MemberUnitIds.Count(id => _observations.TryGet(id, out var unit) && unit?.Diagnostics?.Blockers.Count > 0)} blockers";
    public string SelectedTeamMembers
        => SelectedTeam is not { } team ? string.Empty : string.Join(" · ", team.MemberUnitIds.Select(id => _observations.TryGet(id, out var unit) && unit is not null ? unit.Name : id));
    public bool HasTeamStatus => !string.IsNullOrWhiteSpace(_teamStatus);
    public string TeamStatus => _teamStatus;
    public FormationLockSnapshot Formation => _formation?.Current ?? FormationLockSnapshot.Unlocked;
    public bool IsTeamFormationLocked => IsTeamSelected && Formation.IsLocked && string.Equals(Formation.TeamId, SelectedTeam?.TeamId, StringComparison.Ordinal);
    public string TeamFormationStatus => !IsTeamSelected ? string.Empty : Formation.IsLocked ? $"Locked · {Formation.Members.Count} members" : Formation.InterruptionReason ?? "Unlocked";
    public string TeamFormationPosition => Formation.TeamLatitudeDegrees is { } latitude && Formation.TeamLongitudeDegrees is { } longitude && Formation.TeamAltitudeAglMetres is { } altitude
        ? $"{latitude:F6}, {longitude:F6} · {altitude:F1} m AGL" : string.Empty;
    public string TeamFormationTransform => IsTeamFormationLocked
        ? $"{Formation.CurrentRotationDegrees:F0}° · {Formation.CurrentScalePercent:F0}%"
        : string.Empty;
    public FormationAssignmentSnapshot AuthoredFormationAssignment
    {
        get
        {
            if (_formationAssignment is { } assignment && SelectedTeam?.TeamId is { } teamId &&
                assignment.TryGet(teamId, out var selected) && selected is not null)
                return selected;
            return FormationAssignmentSnapshot.Unassigned;
        }
    }
    public bool IsTeamFormationAssigned => AuthoredFormationAssignment.HasAssignment;
    public bool IsTeamFormationActive => AuthoredFormationAssignment.IsActive;
    public string TeamFormationAssignmentStatus => !IsTeamFormationAssigned ? string.Empty : $"{AuthoredFormationAssignment.State} · {AuthoredFormationAssignment.FormationName}";
    public FormationWorkflowSnapshot? SelectedAuthoredFormation
    {
        get => _selectedAuthoredFormation;
        set { if (SetProperty(ref _selectedAuthoredFormation, value)) (AssignAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); }
    }
    public string TeamEntryHeading { get => _teamEntryHeading; set { if (SetProperty(ref _teamEntryHeading, value)) (AssignAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string TeamGoToLatitude { get => _teamGoToLatitude; set { if (SetProperty(ref _teamGoToLatitude, value)) (MoveTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string TeamGoToLongitude { get => _teamGoToLongitude; set { if (SetProperty(ref _teamGoToLongitude, value)) (MoveTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string TeamAltitude { get => _teamAltitude; set { if (SetProperty(ref _teamAltitude, value)) (ChangeTeamFormationAltitudeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string TeamRotation { get => _teamRotation; set { if (SetProperty(ref _teamRotation, value)) (RotateTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string TeamScale { get => _teamScale; set { if (SetProperty(ref _teamScale, value)) (ScaleTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public ICommand LockTeamFormationCommand { get; }
    public ICommand UnlockTeamFormationCommand { get; }
    public ICommand MoveTeamFormationCommand { get; }
    /// <summary>Prepares the same reviewed Team formation Go To used by the Team card.</summary>
    public ICommand PrepareMapTeamGoToCommand { get; }
    public ICommand ChangeTeamFormationAltitudeCommand { get; }
    public ICommand RotateTeamFormationCommand { get; }
    public ICommand ScaleTeamFormationCommand { get; }
    public ICommand HoldTeamFormationCommand { get; }
    public ICommand AssignAuthoredFormationCommand { get; }
    public ICommand EnterAuthoredFormationCommand { get; }
    public ICommand ClearAuthoredFormationCommand { get; }
    public ICommand ExecuteFormationOperationCommand { get; }
    public ICommand CancelFormationOperationCommand { get; }
    public ReviewedOperationSnapshot? PendingFormationOperation { get => _pendingFormationOperation; private set { if (SetProperty(ref _pendingFormationOperation, value)) { OnPropertyChanged(nameof(HasPendingFormationOperation)); (ExecuteFormationOperationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (CancelFormationOperationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } } }
    public bool HasPendingFormationOperation => PendingFormationOperation is not null;
    /// <summary>Shared UI-neutral projection used by CLI and GUI consumers.</summary>
    public IReadOnlyList<UnitObservationSnapshot> ObservedUnits => _observations.Units;
    public IReadOnlyList<string> SelectedUnitIds => _selection.SelectedUnitIds;
    public event EventHandler? UnitSelectionChanged;
    public ObservableCollection<GhostProfileSnapshot> GhostProfiles { get; }
    public GhostProfileSnapshot? SelectedGhostProfile
    {
        get => _selectedGhostProfile;
        set
        {
            if (SetProperty(ref _selectedGhostProfile, value))
                OnPropertyChanged(nameof(SelectedGhostProfileId));
        }
    }
    public string SelectedGhostProfileId => SelectedGhostProfile?.Id ?? string.Empty;
    public int GhostCount
    {
        get => _ghostCount;
        set => SetProperty(ref _ghostCount, Math.Clamp(value, 1, MaxGhostCount));
    }
    public bool IsCreatingGhost
    {
        get => _isCreatingGhost;
        private set
        {
            if (!SetProperty(ref _isCreatingGhost, value)) return;
            (ConfirmCreateGhostCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public bool IsEmpty => Units.Count == 0;
    public bool HasUnits => !IsEmpty;
    public bool HasSelectedUnit => SelectedUnitCount > 0;
    public bool HasSingleSelectedUnit => SelectedUnitCount == 1;
    public bool HasMultipleSelectedUnits => SelectedUnitCount > 1;
    public bool CanManageUnitConnections
        => HasSingleSelectedUnit && SelectedUnit is { IsGhost: false } && !IsSelectedRemoteObserver && _reconciliation is not null && _vehicleStore is not null;
    public bool IsSelectedRemoteObserver => string.Equals(SelectedUnit?.Role, "Remote observer", StringComparison.Ordinal);
    public string SelectedConnectionAssociationSummary
    {
        get
        {
            if (SelectedUnit?.VehicleId is not { } id || _reconciliation?.FindBySource(id) is not { } association)
                return $"{SelectedUnit?.ConnectionIds.Count ?? 0} associated connection(s)";
            return $"{association.Connections.Count} associated connections";
        }
    }
    public int SelectedUnitCount => _selection.SelectedUnitIds.Count > 0
        ? _selection.SelectedUnitIds.Count
        : _selectedUnit is null ? 0 : 1;
    public ObservableCollection<string> ConnectionSummaries { get; }
    public ObservableCollection<UnitStatusIndicatorViewModel> SelectedStatusIndicators { get; }
    public ObservableCollection<UnitConnectionChipViewModel> SelectedConnectionChips { get; }
    public bool HasSelectedConnectionChips => SelectedConnectionChips.Count > 0;
    public RuntimeRecord? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            // ReplaceAll publishes a new record during every live refresh. Avalonia
            // can briefly report null while it reconciles SelectedItem; retain the
            // current selection when the same unit is still present.
            if (value is null && _selectedUnit is not null &&
                _runtimeStore.Items.Any(item => item.Id == _selectedUnit.Id))
            {
                return;
            }

            if (!SetProperty(ref _selectedUnit, value))
            {
                return;
            }

            _selectedUnitItem = value is null
                ? null
                : Units.FirstOrDefault(item => item.Id == value.Id);
            OnPropertyChanged(nameof(SelectedUnitItem));

            if (value is not null)
            {
                var vehicle = value.VehicleId is not null &&
                              _vehicleStore?.TryGet(value.VehicleId, out var discoveredVehicle) == true
                    ? discoveredVehicle
                    : null;
                SelectUnitTarget(vehicle is not null
                    ? SelectionFactory.From(vehicle)
                    : SelectionFactory.From(value));
            }
            else
            {
                _selection.Clear();
            }

            RefreshDetails();
        }
    }

    public UnitListItemViewModel? SelectedUnitItem
    {
        get => _selectedUnitItem;
        set
        {
            // Avalonia can transiently clear SelectedItem while an existing
            // row is refreshed. Do not turn that visual reconciliation into
            // a real application selection clear.
            if (value is null &&
                _selectedUnit is not null &&
                _runtimeStore.Items.Any(item => item.Id == _selectedUnit.Id))
            {
                return;
            }

            if (ReferenceEquals(_selectedUnitItem, value))
            {
                return;
            }

            _selectedUnitItem = value;
            OnPropertyChanged();
            SelectedUnit = value?.Record;
        }
    }
    public string SelectedTitle => SelectedUnit?.VehicleName ?? SelectedUnit?.Name ?? LocalizationService.Current.Get("UnitNoSelected");
    public string SelectionSummary => string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("UnitSelectedCount"), SelectedUnitCount);
    public string SelectedSubtitle => SelectedUnit is null
        ? LocalizationService.Current.Get("UnitConnectHint")
        : string.IsNullOrWhiteSpace(SelectedUnit.Role)
            ? ResolvePlatformLabel(SelectedUnit.PlatformKind)
            : SelectedUnit.Role;
    public string SelectedState => LocalizeUnitState(SelectedObservation?.State.ToString() ?? SelectedUnit?.State.ToString() ?? "-");
    public string SelectedRuntimeMode => SelectedUnit?.RuntimeMode ?? "-";
    public string SelectedIdentity => SelectedUnit?.VehicleId ?? SelectedUnit?.Id ?? "-";
    public string SelectedHealth => SelectedObservation?.Diagnostics?.OverallStatus ?? SelectedUnit?.Health ?? "-";
    public string SelectedReadiness => SelectedObservation?.Diagnostics?.ArmReadiness ?? SelectedUnit?.Readiness ?? "-";
    public string SelectedOverallStatus => SelectedObservation?.Diagnostics?.OverallStatus ?? SelectedDiagnostics?.OverallStatus.ToString() ?? LocalizationService.Current.Get("UnitUnknown");
    public string SelectedBlockerSummary => SelectedObservation?.Diagnostics is { } observedDiagnostics
        ? observedDiagnostics.Blockers.Count == 0 ? LocalizationService.Current.Get("UnitNoCurrentBlockers") : string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("UnitBlockerCount"), observedDiagnostics.Blockers.Count)
        : SelectedDiagnostics is { } diagnostics
            ? diagnostics.Blockers.Count == 0 ? LocalizationService.Current.Get("UnitNoCurrentBlockers") : string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("UnitBlockerCount"), diagnostics.Blockers.Count)
            : LocalizationService.Current.Get("UnitDiagnosticsNotReported");
    public FlightMissionExecutionSnapshot? SelectedMissionExecution
    {
        get
        {
            if (_flightMissions is null || SelectedUnit is null) return null;
            var ids = (SelectedUnit.ConnectionIds ?? []).ToHashSet(StringComparer.Ordinal);
            return _flightMissions.Executions.FirstOrDefault(item =>
                string.Equals(item.VehicleId, SelectedUnit.Id, StringComparison.Ordinal) ||
                string.Equals(item.VehicleId, SelectedUnit.VehicleId, StringComparison.Ordinal) ||
                ids.Contains(item.ConnectionId));
        }
    }
    public bool HasSelectedMissionExecution => SelectedMissionExecution is not null;
    public string SelectedMissionExecutionState => SelectedMissionExecution?.State.ToString() ?? string.Empty;
    public string SelectedMissionExecutionSummary => SelectedMissionExecution?.Summary ?? string.Empty;
    public string SelectedMissionExecutionProgress => SelectedMissionExecution is { CurrentItemIndex: { } index, ItemCount: > 0 } execution
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("UnitRouteItemProgress"), Math.Min(index + 1, execution.ItemCount), execution.ItemCount)
        : string.Empty;
    public string SelectedMissionExecutionStep => SelectedMissionExecution?.ActiveStepName ?? SelectedMissionExecution?.LastEvent ?? string.Empty;
    public string SelectedPosition
    {
        get
        {
            var telemetry = SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics);
            if (telemetry?.LatitudeDegrees is not double latitude || telemetry.LongitudeDegrees is not double longitude)
                return LocalizationService.Current.Get("UnitNotReported");

            var altitude = telemetry.AltitudeAglMetres is double agl
                ? $"AGL {UnitFormatting.Distance(agl, _unitSettings?.Current.VerticalDistance ?? DistanceUnit.Meters)}"
                : telemetry.AltitudeMslMetres is double msl
                    ? $"MSL {UnitFormatting.Distance(msl, _unitSettings?.Current.VerticalDistance ?? DistanceUnit.Meters)}"
                    : LocalizationService.Current.Get("UnitAltitudeNotReported");
            return $"{latitude:0.000000}, {longitude:0.000000} · {altitude}";
        }
    }

    public string SelectedVelocity
    {
        get
        {
            var telemetry = SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics);
            if (telemetry?.VelocityNorthMetresPerSecond is not double north ||
                telemetry.VelocityEastMetresPerSecond is not double east ||
                !double.IsFinite(north) || !double.IsFinite(east))
                return "—";
            var speed = Math.Sqrt(north * north + east * east);
            return UnitFormatting.Speed(speed, _unitSettings?.Current.Speed ?? SpeedUnit.MetersPerSecond);
        }
    }

    public string SelectedVerticalSpeed
    {
        get
        {
            var telemetry = SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics);
            if (telemetry?.VelocityDownMetresPerSecond is not double down || !double.IsFinite(down))
                return "—";
            return UnitFormatting.Speed(-down, _unitSettings?.Current.Speed ?? SpeedUnit.MetersPerSecond);
        }
    }

    public string SelectedOperatorDistance
        => SelectedObservation?.DistanceFromOperatorMetres is double distance && double.IsFinite(distance)
            ? UnitFormatting.AdaptiveDistance(distance, _unitSettings?.Current.HorizontalDistance ?? DistanceUnit.Meters)
            : "—";

    public string SelectedBattery
    {
        get
        {
            var telemetry = SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics);
            return telemetry?.BatteryRemainingPercent is double percent && double.IsFinite(percent)
                ? $"{Math.Clamp(percent, 0, 100):0}%"
                : "—";
        }
    }

    public string SelectedBatteryTooltip
    {
        get
        {
            var telemetry = SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics);
            if (telemetry?.BatteryRemainingPercent is not double percent || !double.IsFinite(percent))
                return LocalizationService.Current.Get("UnitBatteryNotReported");
            var voltage = telemetry.BatteryVoltageVolts is double volts && double.IsFinite(volts) ? $"; {volts:0.0} V" : string.Empty;
            var freshness = telemetry.BatteryObservedAt is { } observedAt
                ? $" Last update: {FormatAge(observedAt)}."
                : string.Empty;
            return string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("UnitBatteryTooltip"), $"{percent:0}%{voltage}", freshness);
        }
    }

    public string SelectedSignalTooltip
        => SelectedConnectionChips.Count == 0
            ? LocalizationService.Current.Get("UnitSignalNotReported")
            : string.Join(Environment.NewLine, SelectedConnectionChips.Select(item => item.Tooltip));

    public string SelectedVehicleState
    {
        get
        {
            var telemetry = SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics);
            if (telemetry is null)
            {
                var vehicle = SelectedUnit?.VehicleId is not null &&
                               _vehicleStore?.TryGet(SelectedUnit.VehicleId, out var record) == true
                    ? record
                    : null;
                return NormalizeVehicleState(vehicle?.ArmState, vehicle?.Lifecycle);
            }
            if (!telemetry.Armed)
                return LocalizationService.Current.Get("UnitDisarmed");
            return telemetry.LandedState.Contains("land", StringComparison.OrdinalIgnoreCase)
                ? LocalizationService.Current.Get("UnitLanded")
                : LocalizationService.Current.Get("UnitFlying");
        }
    }

    public string SelectedHeading
    {
        get
        {
            var heading = (SelectedObservation?.Telemetry ?? ToTelemetryObservation(SelectedTelemetry, SelectedDiagnostics))?.HeadingDegrees;
            if (heading is not double value || !double.IsFinite(value))
                return LocalizationService.Current.Get("UnitNotReported");

            var normalized = ((value % 360d) + 360d) % 360d;
            return $"{normalized:0.0}°";
        }
    }

    public string SelectedCurrentAction => SelectedQueuedAction;
    public string SelectedManualControlStatus
    {
        get
        {
            var session = _manualControl?.Snapshot;
            if (session is not { IsActive: true }) return "Manual control: inactive";
            return SelectedUnit?.VehicleId == session.TargetVehicleId
                ? $"Manual control: {session.State}"
                : $"Manual control: {session.TargetName} {session.State}";
        }
    }

    /// <summary>True only when the selected unit is the controller's captured target.</summary>
    public bool IsSelectedUnitUnderManualControl
        => _manualControl?.Snapshot is { IsActive: true } session &&
           string.Equals(SelectedUnit?.VehicleId, session.TargetVehicleId, StringComparison.Ordinal);

    public string SelectedManualControlMode
    {
        get
        {
            var session = _manualControl?.Snapshot;
            if (session is not { IsActive: true }) return "Manual control inactive";

            return session.State switch
            {
                ManualControlSessionState.Active => "MANUAL CONTROL · ACTIVE",
                ManualControlSessionState.Hold => "MANUAL CONTROL · HOLD",
                ManualControlSessionState.InputStale => "MANUAL CONTROL · INPUT STALE",
                _ => "MANUAL CONTROL"
            };
        }
    }

    public string SelectedManualControlDetail
    {
        get
        {
            var session = _manualControl?.Snapshot;
            if (session is not { IsActive: true }) return "";

            var controller = string.IsNullOrWhiteSpace(session.DeviceName) ? "Xbox controller" : session.DeviceName;
            var deadman = session.DeadmanPressed ? "LB held" : "LB released";
            return $"{controller} · {deadman}. {session.Status}";
        }
    }

    public bool HasSelectedManualPendingCommand
        => IsSelectedUnitUnderManualControl &&
           _manualControl?.Snapshot.PendingButtonExpiresAt is not null &&
           !string.IsNullOrWhiteSpace(_manualControl.Snapshot.PendingButtonAction);

    public string SelectedManualPendingAction
        => _manualControl?.Snapshot.PendingButtonAction ?? "";

    public string SelectedManualPendingExpiry
    {
        get
        {
            var expiresAt = _manualControl?.Snapshot.PendingButtonExpiresAt;
            if (expiresAt is null) return "";

            var remaining = expiresAt.Value - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero
                ? $"Timeout in {Math.Ceiling(remaining.TotalSeconds):0}s"
                : "Confirmation timed out";
        }
    }

    public string SelectedQueuedAction
        => _operatorControls?.SelectedQueuedAction ?? "None queued";

    public string SelectedQueuedAvailability
        => _operatorControls?.SelectedQueuedAvailability ?? "No queued command";

    public bool HasQueuedCommandsForSelection
        => _operatorControls?.HasQueuedCommandsForSelection == true;

    public bool HasExecutingCommand
        => _operatorControls?.HasExecutingCommand == true;

    public bool ShowClearQueuedCommand
        => _operatorControls?.ShowClearQueuedForSelection == true;

    public ICommand CancelExecutingCommandsCommand
        => _operatorControls?.CancelExecutingCommandsCommand ?? new RelayCommand(_ => { });

    public ICommand ExecuteQueuedCommandsCommand
        => _operatorControls?.ExecuteQueuedCommandsCommand ?? new RelayCommand(_ => { });

    public ICommand ClearQueuedCommandsCommand
        => _operatorControls?.ClearQueuedCommandsCommand ?? new RelayCommand(_ => { });

    private void OnOperatorQueueChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedQueuedAction));
        OnPropertyChanged(nameof(SelectedQueuedAvailability));
        OnPropertyChanged(nameof(SelectedCurrentAction));
        OnPropertyChanged(nameof(HasQueuedCommandsForSelection));
        OnPropertyChanged(nameof(HasExecutingCommand));
        OnPropertyChanged(nameof(ShowClearQueuedCommand));
    }

    // Compatibility properties used by the Operate workspace's unit summary;
    // the Units rail intentionally does not display these redundant fields.
    public string SelectedConnections => SelectedUnit?.ConnectionIds?.Count.ToString(CultureInfo.CurrentCulture) ?? "0";
    public string SelectedPlatform => SelectedUnit is null ? "-" : $"{SelectedUnit.PlatformKind} / {SelectedUnit.PlatformProfile}";
    public string SelectedVersion => SelectedUnit?.LogosVersion ?? "-";

    private VehicleTelemetryRecord? SelectedTelemetry
        => SelectedUnit?.VehicleId is null
            ? null
            : _telemetryStore?.Items
                .Where(item => string.Equals(
                    item.VehicleId,
                    _reconciliation?.ResolveTelemetrySource(SelectedUnit.VehicleId) ?? SelectedUnit.VehicleId,
                    StringComparison.Ordinal))
                .OrderByDescending(item => item.ObservedAt)
                .FirstOrDefault();
    private VehicleDiagnosticsSnapshot? SelectedDiagnostics
        => SelectedUnit?.VehicleId is null
            ? null
            : _diagnosticsStore?.Items
                .Where(item => string.Equals(
                    item.VehicleId,
                    _reconciliation?.ResolveDiagnosticsSource(SelectedUnit.VehicleId) ?? SelectedUnit.VehicleId,
                    StringComparison.Ordinal))
                .OrderByDescending(item => item.ObservedAt)
                .FirstOrDefault();

    private UnitObservationSnapshot? SelectedObservation
        => SelectedUnit is null
            ? null
            : _observations.Units.FirstOrDefault(item =>
                string.Equals(item.Id, SelectedUnit.VehicleId, StringComparison.Ordinal) ||
                string.Equals(item.Id, SelectedUnit.Id, StringComparison.Ordinal));

    private static UnitTelemetryObservation? ToTelemetryObservation(VehicleTelemetryRecord? telemetry, VehicleDiagnosticsSnapshot? diagnostics)
        => telemetry is null
            ? null
            : new UnitTelemetryObservation(
                (ManagedConnectionState)(int)telemetry.State, telemetry.Armed, telemetry.LandedState, telemetry.AirframeMode,
                telemetry.LatitudeDegrees, telemetry.LongitudeDegrees, telemetry.AltitudeMslMetres, telemetry.AltitudeAglMetres,
                telemetry.VelocityNorthMetresPerSecond, telemetry.VelocityEastMetresPerSecond, telemetry.VelocityDownMetresPerSecond,
                telemetry.HeadingDegrees, telemetry.IsStale, telemetry.ObservedAt, diagnostics?.BatteryRemainingPercent, diagnostics?.BatteryVoltageVolts, diagnostics?.ObservedAt);
    public ICommand ClearSelectionCommand { get; }
    public ICommand SelectTeamCommand { get; }
    public ICommand CreateGhostCommand { get; }
    public ICommand ConfirmCreateGhostCommand { get; }
    public ICommand CancelCreateGhostCommand { get; }
    public ICommand DeleteGhostCommand { get; }
    public ICommand ManageUnitConnectionsCommand { get; }

    public void SelectUnit(UnitListItemViewModel item, bool extend, bool range)
    {
        var selection = ToVehicleSelection(item);
        var selectedIds = _selection.SelectedUnitIds.ToList();
        var anchorId = _selection.UnitSelectionAnchorId;

        if (!extend && !range)
        {
            _selection.SetUnitSelection([selection], selection.Id);
            return;
        }

        if (range && selectedIds.Count > 0)
        {
            var anchorIndex = Units
                .Select((unit, index) => (unit, index))
                .FirstOrDefault(pair => (pair.unit.Record.VehicleId ?? pair.unit.Record.Id) == anchorId).index;
            var targetIndex = Units.IndexOf(item);
            var start = Math.Min(anchorIndex, targetIndex);
            var end = Math.Max(anchorIndex, targetIndex);
            selectedIds = Units
                .Skip(start)
                .Take(end - start + 1)
                .Select(unit => unit.Record.VehicleId ?? unit.Record.Id)
                .ToList();
        }
        else if (extend)
        {
            if (!selectedIds.Remove(item.Record.VehicleId ?? item.Record.Id))
                selectedIds.Add(item.Record.VehicleId ?? item.Record.Id);
        }

        var selections = selectedIds
            .Select(id => Units.FirstOrDefault(unit => (unit.Record.VehicleId ?? unit.Record.Id) == id))
            .Where(unit => unit is not null)
            .Cast<UnitListItemViewModel>()
            .Select(ToVehicleSelection)
            .ToArray();
        _selection.SetUnitSelection(selections, anchorId ?? selection.Id);
    }

    public async Task SelectTeamFromHeaderAsync(string teamId, CancellationToken cancellationToken = default)
    {
        if (_teamSelection is null) return;
        if (string.Equals(_teamSelection.Current.TeamId, teamId, StringComparison.Ordinal))
            await _teamSelection.ClearAsync(cancellationToken);
        else
            await _teamSelection.SelectAsync(teamId, cancellationToken);
    }

    private OperationalSelection ToVehicleSelection(UnitListItemViewModel item)
    {
        if (item.Record.VehicleId is not null &&
            _vehicleStore?.TryGet(item.Record.VehicleId, out var vehicle) == true && vehicle is not null)
            return SelectionFactory.From(vehicle);

        var id = item.Record.VehicleId ?? item.Record.Id;
        return new OperationalSelection(
            SelectionKind.Vehicle,
            id,
            item.Record.VehicleName ?? item.Record.Name,
            item.Record.Role,
            []);
    }

    public void ReorderUnit(UnitListItemViewModel item, int targetIndex)
    {
        if (item.TeamId is not null)
        {
            var teamRows = Units.Where(row => row.TeamId == item.TeamId).ToArray();
            var first = Units.IndexOf(teamRows.FirstOrDefault() ?? item);
            var current = Array.IndexOf(teamRows, item);
            if (current < 0 || first < 0) return;
            var target = Math.Clamp(targetIndex - first, 0, teamRows.Length - 1);
            if (target == current) return;
            _ = ReorderTeamMemberAsync(item.TeamId, UnitKey(item), target);
            return;
        }

        // Keep unassigned rows outside Team boxes. They may be reordered
        // among themselves, but a drag cannot visually place one inside a
        // Team and accidentally imply a membership change.
        var unassignedRows = Units.Where(row => row.TeamId is null).ToArray();
        var firstUnassigned = Units.IndexOf(unassignedRows.FirstOrDefault() ?? item);
        if (firstUnassigned < 0 || unassignedRows.Length < 2) return;

        var selectedId = _selectedUnit?.Id;
        var currentIndex = Units.IndexOf(item);
        if (currentIndex < 0 || Units.Count < 2) return;
        targetIndex = Math.Clamp(targetIndex, firstUnassigned, Units.Count - 1);
        if (currentIndex == targetIndex) return;
        Units.Move(currentIndex, targetIndex);

        // Moving an ObservableCollection item can cause ListBox.SelectedItem
        // to transiently clear. Restore the selection by stable identity so
        // dragging a selected unit never changes the operational target.
        if (selectedId is not null)
        {
            var selectedItem = Units.FirstOrDefault(unit => unit.Id == selectedId);
            if (selectedItem is not null)
            {
                _selectedUnitItem = selectedItem;
                OnPropertyChanged(nameof(SelectedUnitItem));
            }
        }
    }

    public async Task CreateTeamFromSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_teams is null) return;
        var ids = _selection.SelectedUnitIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            SetTeamStatus(LocalizationService.Current.Get("TeamStatusSelectCommandable"));
            return;
        }

        try
        {
            var team = await _teams.CreateAsync(ids, cancellationToken: cancellationToken);
            if (_teamSelection is not null)
                await _teamSelection.SelectAsync(team.Id, cancellationToken);
            SetTeamStatus(string.Empty);
        }
        catch (Exception exception)
        {
            SetTeamStatus(exception.Message);
        }
    }

    public async Task ClearSelectedTeamMembershipAsync(CancellationToken cancellationToken = default)
    {
        if (_teams is null) return;
        var ids = _selection.SelectedUnitIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            SetTeamStatus(LocalizationService.Current.Get("TeamStatusSelectUnits"));
            return;
        }

        try
        {
            await _teams.ClearMembershipAsync(ids, cancellationToken);
            SetTeamStatus(string.Empty);
        }
        catch (Exception exception)
        {
            SetTeamStatus(exception.Message);
        }
    }

    private async Task ReorderTeamMemberAsync(string teamId, string unitId, int index)
    {
        if (_teams is null) return;
        try
        {
            await _teams.ReorderMemberAsync(teamId, unitId, index);
        }
        catch (Exception exception)
        {
            SetTeamStatus(exception.Message);
        }
    }

    private bool CanAssignAuthoredFormation()
        => IsTeamSelected && !IsTeamFormationActive && SelectedAuthoredFormation is not null &&
           double.TryParse(TeamEntryHeading, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var heading) && heading is >= 0d and <= 360d;

    private bool CanLockTeamFormation()
        => IsTeamSelected && !IsTeamFormationLocked && !IsTeamFormationAssigned;

    private async Task AssignAuthoredFormationAsync(CancellationToken cancellationToken)
    {
        if (_formationAssignment is null || SelectedTeam?.TeamId is not { } teamId || SelectedAuthoredFormation is null || !CanAssignAuthoredFormation()) return;
        try
        {
            var snapshot = await _formationAssignment.AssignAsync(new(teamId, SelectedAuthoredFormation.Id,
                double.Parse(TeamEntryHeading, System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
            SetTeamStatus($"Assigned {snapshot.FormationName}.");
        }
        catch (Exception exception) { SetTeamStatus(exception.Message); }
        OnFormationAssignmentChanged(this, EventArgs.Empty);
    }

    private async Task EnterAuthoredFormationAsync(CancellationToken cancellationToken)
    {
        if (_formationAssignment is null || SelectedTeam?.TeamId is not { } teamId || !IsTeamFormationAssigned) return;
        try
        {
            PendingFormationOperation = await _formationAssignment.PlanEnterAsync(teamId, cancellationToken);
            SetTeamStatus(PendingFormationOperation.Summary);
        }
        catch (Exception exception) { SetTeamStatus(exception.Message); }
        OnFormationAssignmentChanged(this, EventArgs.Empty);
    }

    private async Task ClearAuthoredFormationAsync(CancellationToken cancellationToken)
    {
        if (_formationAssignment is null || SelectedTeam?.TeamId is not { } teamId) return;
        try { await _formationAssignment.ClearAsync(teamId, cancellationToken); SetTeamStatus(string.Empty); }
        catch (Exception exception) { SetTeamStatus(exception.Message); }
        OnFormationAssignmentChanged(this, EventArgs.Empty);
    }

    private async void OnFormationSlotChanged(UnitListItemViewModel row, FormationSlotOption option)
    {
        if (_formationAssignment is null || row.TeamId is null || string.IsNullOrWhiteSpace(option.Id)) return;
        try { await _formationAssignment.SwapSlotAsync(row.TeamId, UnitKey(row), option.Id); }
        catch (Exception exception) { SetTeamStatus(exception.Message); }
    }

    private void SetTeamStatus(string value)
    {
        _teamStatus = value;
        OnPropertyChanged(nameof(TeamStatus));
        OnPropertyChanged(nameof(HasTeamStatus));
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var row in Units)
        {
            row.NotifyTeamProperties();
            row.RefreshLocalizedPresentation();
        }
        RefreshDetails();
        OnPropertyChanged(nameof(TeamStatus));
    }

    private void OnGhostProfilesChanged(object? sender, EventArgs e)
    {
        var selectedId = SelectedGhostProfile?.Id ?? GhostProfileDefaults.Dracula.Id;
        GhostProfiles.Clear();
        foreach (var profile in _ghostProfiles.Profiles)
            GhostProfiles.Add(profile);
        SelectedGhostProfile = GhostProfiles.FirstOrDefault(profile => string.Equals(profile.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? GhostProfiles.FirstOrDefault(profile => string.Equals(profile.Id, GhostProfileDefaults.Dracula.Id, StringComparison.OrdinalIgnoreCase))
            ?? GhostProfiles.FirstOrDefault();
    }

    private void BeginManageUnitConnections()
    {
        if (!CanManageUnitConnections || _unitLibrary is null || SelectedUnit?.VehicleId is not { } selectedVehicleId)
            return;

        var definition = _reconciliation?.FindBySource(selectedVehicleId);
        if (definition is not null)
        {
            _unitLibrary.OpenExisting(definition.Id);
        }
        else
        {
            var connectionId = SelectedUnit.ConnectionIds.Count > 0 ? SelectedUnit.ConnectionIds[0] : string.Empty;
            _unitLibrary.OpenForVehicle(connectionId, selectedVehicleId);
        }
    }

    private void OnReconciliationChanged(object? sender, EventArgs e)
        => OnChanged(sender, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    private void OnObservationChanged(object? sender, EventArgs e)
        => OnChanged(sender, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    private void OnMissionChanged(object? sender, EventArgs e)
    {
        RefreshDetails();
        OnPropertyChanged(nameof(SelectedMissionExecution));
        OnPropertyChanged(nameof(HasSelectedMissionExecution));
        OnPropertyChanged(nameof(SelectedMissionExecutionState));
        OnPropertyChanged(nameof(SelectedMissionExecutionSummary));
        OnPropertyChanged(nameof(SelectedMissionExecutionProgress));
        OnPropertyChanged(nameof(SelectedMissionExecutionStep));
    }

    private void OnTeamChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            QueueTeamLayout();
            return;
        }

        ApplyTeamLayoutAndNotify();
    }

    private void QueueTeamLayout()
    {
        if (Interlocked.Exchange(ref _teamLayoutQueued, 1) != 0)
            return;

        _ = _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _teamLayoutQueued, 0);
            ApplyTeamLayoutAndNotify();
        });
    }

    private void ApplyTeamLayoutAndNotify()
    {
        ApplyTeamLayout();
        OnPropertyChanged(nameof(Teams));
    }

    private async Task CreateGhostAsync(CancellationToken cancellationToken)
    {
        if (_ghostWorkflow is not null)
        {
            var profileId = SelectedGhostProfile?.Id ?? throw new InvalidOperationException("Select a Ghost profile before creating a unit.");
            var created = await _ghostWorkflow.CreateAsync(new GhostCreateRequest(profileId, GhostCount), cancellationToken);
            IsCreatingGhost = false;
            SelectCreatedGhosts(created.Select(item => (item.UnitId, item.Name)).ToArray());
            return;
        }
        if (_ghosts is null) return;
        var viewport = _viewportState?.Current;
        var fallbackProfileId = SelectedGhostProfile?.Id ?? throw new InvalidOperationException("Select a Ghost profile before creating a unit.");
        var createdFallback = new List<(string Id, string Name)>();
        for (var index = 0; index < GhostCount; index++)
        {
            var vehicle = await _ghosts.CreateAsync(
                fallbackProfileId,
                GhostSpawnLayout.ForBatch(viewport, index, GhostCount),
                cancellationToken);
            createdFallback.Add((vehicle.Id, vehicle.Name));
        }

        IsCreatingGhost = false;
        SelectCreatedGhosts(createdFallback);
    }

    private void SelectCreatedGhosts(IReadOnlyList<(string Id, string Name)> created)
    {
        if (created.Count == 0)
            return;

        var selections = created
            .Select(item => _vehicleStore?.TryGet(item.Id, out var vehicle) == true && vehicle is not null
                ? SelectionFactory.From(vehicle)
                : new OperationalSelection(SelectionKind.Vehicle, item.Id, item.Name, "Ghost", []))
            .ToArray();
        _selection.SetUnitSelection(selections, selections[0].Id);
    }

    private async Task DeleteGhostAsync(object? parameter)
    {
        if (parameter is not UnitListItemViewModel item || !item.IsGhost) return;
        var vehicleId = item.Record.VehicleId ?? item.Record.Id;
        if (_ghostWorkflow is not null) await _ghostWorkflow.DeleteAsync(vehicleId);
        else if (_ghosts is not null) await _ghosts.DeleteAsync(vehicleId);
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Store publication is intentionally batched.  Always queue the
        // presentation refresh, including notifications raised on the UI
        // thread, so a telemetry cycle cannot synchronously rebuild every row
        // once per store.
        QueueUnitRefresh();
    }

    private void QueueUnitRefresh()
    {
        if (Interlocked.Exchange(ref _unitRefreshQueued, 1) != 0)
            return;

        _ = _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _unitRefreshQueued, 0);
            RefreshUnitsFromStores();
        });
    }

    private void RefreshUnitsFromStores()
    {
        if (_isApplyingTeamLayout || _isRefreshingUnits) return;
        _isRefreshingUnits = true;
        try
        {
            var records = _reconciliation?.ProjectRuntimes(
                              _runtimeStore.Items,
                              _vehicleStore?.Items is { } vehicles ? vehicles : Array.Empty<VehicleRecord>())
                          ?? _runtimeStore.Items;
            var selectedId = SelectedRuntimeId(records) ?? _selectedUnit?.Id;
            var previousSelectedRowId = _selectedUnitItem?.Id;
            var collectionChanged = false;
            var byId = records.ToDictionary(item => item.Id, StringComparer.Ordinal);
            for (var index = Units.Count - 1; index >= 0; index--)
            {
                if (!byId.ContainsKey(Units[index].Id))
                {
                    Units.RemoveAt(index);
                    collectionChanged = true;
                }
            }

            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                var item = Units.FirstOrDefault(candidate => candidate.Id == record.Id);
                if (item is null)
                {
                    // Preserve the operator's drag order. Newly discovered units
                    // are appended instead of reshuffling existing rows.
                    var observation = FindObservation(record);
                    Units.Add(new UnitListItemViewModel(
                        record,
                        record.VehicleId is null ? null : _reconciliation?.FindBySource(record.VehicleId),
                        observation));
                    collectionChanged = true;
                }
                else
                {
                    item.Update(
                        record,
                        record.VehicleId is null ? null : _reconciliation?.FindBySource(record.VehicleId),
                        FindObservation(record));
                }
            }

            var replacement = selectedId is null ? null : records.FirstOrDefault(item => item.Id == selectedId);
            if (replacement is not null)
            {
                _selectedUnit = replacement;
                _selectedUnitItem = Units.FirstOrDefault(item => item.Id == replacement.Id);
                OnPropertyChanged(nameof(SelectedUnitItem));

                // The runtime and vehicle stores are refreshed separately. If the
                // runtime row arrived first, promote it to the canonical vehicle
                // selection as soon as the vehicle record becomes available.
                var vehicle = replacement.VehicleId is not null &&
                              _vehicleStore?.TryGet(replacement.VehicleId, out var discoveredVehicle) == true
                    ? discoveredVehicle
                    : null;
                SelectUnitTarget(vehicle is not null
                    ? SelectionFactory.From(vehicle)
                    : SelectionFactory.From(replacement));
            }
            else if (selectedId is not null)
            {
                SelectedUnit = null;
            }

            RefreshDetails();
            if (collectionChanged)
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasUnits));
            }
            // Keep Avalonia's ListBox selection untouched during ordinary
            // telemetry refreshes. Clearing and re-adding SelectedItems here
            // makes a multi-selected list repeatedly remeasure and scroll,
            // which presents as vertical vibration once several Ghosts are
            // selected. Structural changes still request a selection sync.
            if (collectionChanged || !string.Equals(previousSelectedRowId, _selectedUnitItem?.Id, StringComparison.Ordinal))
                UnitSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isRefreshingUnits = false;
        }

        // The collection notifications raised above have completed. Reordering
        // Team members here is safe and cannot re-enter ObservableCollection's
        // CollectionChanged callback.
        ApplyTeamLayout();
    }

    private void ApplyTeamLayout()
    {
        if (_isApplyingTeamLayout) return;
        if (_teams is null)
        {
            RefreshRowSelectionStates();
            return;
        }
        _isApplyingTeamLayout = true;
        try
        {
            var snapshot = _teams.Current;
            var teamById = snapshot.Teams.ToDictionary(team => team.Id, StringComparer.Ordinal);
            var membership = snapshot.TeamByUnitId;
            var existing = Units.ToArray();
            var ordered = snapshot.Teams
                .SelectMany(team => team.Members.Select(member => existing.FirstOrDefault(row => UnitKey(row) == member.UnitId)))
                .Where(row => row is not null)
                .Cast<UnitListItemViewModel>()
                .Concat(existing.Where(row => !membership.ContainsKey(UnitKey(row))))
                .Distinct()
                .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                var current = Units.IndexOf(ordered[index]);
                if (current >= 0 && current != index) Units.Move(current, index);
            }

            foreach (var row in Units)
            {
                if (!membership.TryGetValue(UnitKey(row), out var teamId) || !teamById.TryGetValue(teamId, out var team))
                {
                    row.ClearTeam();
                    row.ClearFormationSlots();
                    continue;
                }

                var member = team.Members.First(item => item.UnitId == UnitKey(row));
                row.SetTeam(team.Id, team.Name, member.Order, team.Members.Count);
                row.FormationSlotChanged = OnFormationSlotChanged;
                if (_formationAssignment?.TryGet(team.Id, out var assignment) == true && assignment is not null)
                {
                    var slots = assignment.Assignments
                        .Select(item => new FormationSlotOption(item.SlotId, item.SlotName))
                        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var assigned = assignment.Assignments.FirstOrDefault(item => item.UnitId == UnitKey(row));
                    row.SetFormationSlots(slots, assigned?.SlotId);
                }
                else
                {
                    row.ClearFormationSlots();
                }
            }

            RefreshRowSelectionStates();
        }
        finally
        {
            _isApplyingTeamLayout = false;
        }
    }

    private static string UnitKey(UnitListItemViewModel row)
        => row.Record.VehicleId ?? row.Record.Id;

    private UnitObservationSnapshot? FindObservation(RuntimeRecord record)
        => _observations.TryGet(record.VehicleId ?? record.Id, out var observation) ? observation : null;

    private void RefreshRowSelectionStates()
    {
        var selected = _selection.SelectedUnitIds.ToHashSet(StringComparer.Ordinal);
        var teamSelected = _teamSelection?.Current.MemberUnitIds.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var row in Units)
        {
            var id = UnitKey(row);
            row.SetSelected(selected.Contains(id));
            row.SetTeamSelected(teamSelected.Contains(id));
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        RefreshRowSelectionStates();
        UnitSelectionChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(HasSelectedUnit));
        OnPropertyChanged(nameof(HasSingleSelectedUnit));
        OnPropertyChanged(nameof(HasMultipleSelectedUnits));
        OnPropertyChanged(nameof(SelectedUnitCount));
        OnPropertyChanged(nameof(SelectionSummary));
        var records = _reconciliation?.ProjectRuntimes(
                          _runtimeStore.Items,
                          _vehicleStore?.Items is { } vehicles ? vehicles : Array.Empty<VehicleRecord>())
                      ?? _runtimeStore.Items;
        var selectedId = SelectedRuntimeId(records);
        if (selectedId is null)
        {
            if (_selectedUnit is null && _selectedUnitItem is null) return;
            _selectedUnit = null;
            _selectedUnitItem = null;
            OnPropertyChanged(nameof(SelectedUnitItem));
            RefreshDetails();
            return;
        }

        var replacement = records.FirstOrDefault(item => item.Id == selectedId);
        if (replacement is null || string.Equals(_selectedUnit?.Id, replacement.Id, StringComparison.Ordinal))
        {
            return;
        }

        _selectedUnit = replacement;
        _selectedUnitItem = Units.FirstOrDefault(item => item.Id == replacement.Id);
        OnPropertyChanged(nameof(SelectedUnitItem));
        RefreshDetails();
    }

    private void OnTeamSelectionChanged(object? sender, EventArgs e)
    {
        RefreshRowSelectionStates();
        OnPropertyChanged(nameof(IsTeamSelected));
        OnPropertyChanged(nameof(ShowUnitCards));
        OnPropertyChanged(nameof(SelectedTeam));
        OnPropertyChanged(nameof(SelectedTeamTitle));
        OnPropertyChanged(nameof(SelectedTeamMemberSummary));
        OnPropertyChanged(nameof(SelectedTeamOnlineSummary));
        OnPropertyChanged(nameof(SelectedTeamReadiness));
        OnPropertyChanged(nameof(SelectedTeamBlockers));
        OnPropertyChanged(nameof(SelectedTeamMembers));
        if (_formationAssignment is { } assignments && SelectedTeam?.TeamId is { } teamId &&
            assignments.TryGet(teamId, out var teamAssignment) && teamAssignment?.FormationId is { } formationId)
            SelectedAuthoredFormation = AuthoredFormations.FirstOrDefault(item => item.Id == formationId);
        OnFormationChanged();
        OnPropertyChanged(nameof(HasSelectedUnit));
        OnPropertyChanged(nameof(HasSingleSelectedUnit));
        OnPropertyChanged(nameof(HasMultipleSelectedUnits));
        UnitSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LockTeamFormationAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || SelectedTeam?.TeamId is not { } teamId) return;
        try { await _formation.LockAsync(teamId, cancellationToken); SetTeamStatus(string.Empty); }
        catch (Exception exception) { SetTeamStatus(exception.Message); }
        OnFormationChanged();
    }

    private async Task UnlockTeamFormationAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || SelectedTeam?.TeamId is not { } teamId) return;
        await _formation.UnlockAsync(teamId, "Operator unlocked the formation.", cancellationToken);
        OnFormationChanged();
    }

    private bool CanMoveTeamFormation()
        => IsTeamFormationLocked && double.TryParse(TeamGoToLatitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var latitude) && latitude is >= -90 and <= 90 &&
           double.TryParse(TeamGoToLongitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var longitude) && longitude is >= -180 and <= 180;

    private bool CanPrepareMapTeamGoTo(object? parameter)
        => IsTeamFormationLocked && parameter is MapCommandTarget;

    private async Task MoveTeamFormationAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || _reviewed is null || SelectedTeam?.TeamId is not { } teamId || !CanMoveTeamFormation()) return;
        await PlanTeamFormationMoveAsync(teamId,
            double.Parse(TeamGoToLatitude, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(TeamGoToLongitude, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    private async Task PrepareMapTeamGoToAsync(object? parameter)
    {
        if (parameter is not MapCommandTarget target || !CanPrepareMapTeamGoTo(parameter) || SelectedTeam?.TeamId is not { } teamId)
            return;

        await PlanTeamFormationMoveAsync(teamId, target.LatitudeDegrees, target.LongitudeDegrees, CancellationToken.None);
    }

    private async Task PlanTeamFormationMoveAsync(string teamId, double latitude, double longitude, CancellationToken cancellationToken)
    {
        if (_formation is null || _reviewed is null)
            return;

        try
        {
            PendingFormationOperation = await _formation.PlanMoveToAsync(teamId, latitude, longitude, cancellationToken);
            SetTeamStatus(PendingFormationOperation.Summary);
        }
        catch (Exception exception)
        {
            SetTeamStatus(exception.Message);
        }

        OnFormationChanged();
    }

    private bool CanChangeTeamFormationAltitude()
        => IsTeamFormationLocked && double.TryParse(TeamAltitude, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var altitude) && altitude >= 0;

    private async Task ChangeTeamFormationAltitudeAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || _reviewed is null || SelectedTeam?.TeamId is not { } teamId || !CanChangeTeamFormationAltitude()) return;
        PendingFormationOperation = await _formation.PlanChangeAltitudeAsync(teamId, double.Parse(TeamAltitude, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        SetTeamStatus(PendingFormationOperation.Summary);
        OnFormationChanged();
    }

    private bool CanRotateTeamFormation()
        => IsTeamFormationLocked && double.TryParse(TeamRotation, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var degrees) && degrees is >= -360d and <= 360d;

    private async Task RotateTeamFormationAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || SelectedTeam?.TeamId is not { } teamId || !CanRotateTeamFormation()) return;
        PendingFormationOperation = await _formation.PlanRotateAsync(teamId, double.Parse(TeamRotation, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        SetTeamStatus(PendingFormationOperation.Summary);
        OnFormationChanged();
    }

    private bool CanScaleTeamFormation()
        => IsTeamFormationLocked && double.TryParse(TeamScale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent is >= 50d and <= 200d;

    private async Task ScaleTeamFormationAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || SelectedTeam?.TeamId is not { } teamId || !CanScaleTeamFormation()) return;
        PendingFormationOperation = await _formation.PlanScaleAsync(teamId, double.Parse(TeamScale, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        SetTeamStatus(PendingFormationOperation.Summary);
        OnFormationChanged();
    }

    private async Task HoldTeamFormationAsync(CancellationToken cancellationToken)
    {
        if (_formation is null || SelectedTeam?.TeamId is not { } teamId) return;

        try
        {
            // Hold is the formation's safety stop. It must take effect from the
            // button itself rather than creating another review card that can
            // be mistaken for a no-op while a move, rotation, or resize is in
            // progress. Cancel any unexecuted formation plan first so it cannot
            // be applied after the hold.
            if (_reviewed is not null && PendingFormationOperation is { } pending)
                await _reviewed.CancelAsync(pending.Id, "Formation movement cancelled by Hold.", cancellationToken);

            PendingFormationOperation = null;
            await _formation.HoldAsync(teamId, cancellationToken);
            SetTeamStatus(string.Empty);
        }
        catch (Exception exception)
        {
            SetTeamStatus(exception.Message);
        }

        OnFormationChanged();
    }

    private async Task ExecuteFormationOperationAsync(CancellationToken cancellationToken)
    {
        if (_reviewed is null || PendingFormationOperation is not { } operation) return;
        try
        {
            var result = await _reviewed.ExecuteAsync(operation.Id, cancellationToken);
            SetTeamStatus(result.Summary);
            if (result.Succeeded)
            {
                PendingFormationOperation = null;
            }
        }
        catch (Exception exception)
        {
            // Keep the failure visible instead of allowing an async UI
            // command exception to look like a button that did nothing.
            SetTeamStatus(exception.Message);
        }
        OnFormationChanged();
    }

    private async Task CancelFormationOperationAsync(CancellationToken cancellationToken)
    {
        if (_reviewed is null || PendingFormationOperation is not { } operation) return;
        await _reviewed.CancelAsync(operation.Id, cancellationToken: cancellationToken);
        PendingFormationOperation = null;
        SetTeamStatus(string.Empty);
    }

    private void OnFormationChanged()
    {
        OnPropertyChanged(nameof(Formation));
        OnPropertyChanged(nameof(IsTeamFormationLocked));
        OnPropertyChanged(nameof(TeamFormationStatus));
        OnPropertyChanged(nameof(TeamFormationPosition));
        OnPropertyChanged(nameof(TeamFormationTransform));
        OnPropertyChanged(nameof(PendingFormationOperation));
        OnPropertyChanged(nameof(TeamStatus));
        OnPropertyChanged(nameof(HasTeamStatus));
        (LockTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UnlockTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MoveTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ChangeTeamFormationAltitudeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RotateTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ScaleTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (HoldTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EnterAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AssignAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnFormationAssignmentChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AuthoredFormationAssignment));
        OnPropertyChanged(nameof(IsTeamFormationAssigned));
        OnPropertyChanged(nameof(IsTeamFormationActive));
        OnPropertyChanged(nameof(TeamFormationAssignmentStatus));
        ApplyTeamLayout();
        (LockTeamFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EnterAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearAuthoredFormationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnFormationChanged();
    }

    private void OnFormationAssignmentWorkflowChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            OnFormationAssignmentChanged(sender, e);
            return;
        }
        _ = _dispatcher.InvokeAsync(() => OnFormationAssignmentChanged(sender, e));
    }

    private void OnFormationAuthoringChanged(object? sender, EventArgs e)
    {
        AuthoredFormations.Clear();
        foreach (var formation in _formationAuthoring?.Formations ?? []) AuthoredFormations.Add(formation);
        if (SelectedAuthoredFormation is not null)
            SelectedAuthoredFormation = AuthoredFormations.FirstOrDefault(item => item.Id == SelectedAuthoredFormation.Id);
    }

    private void OnFormationWorkflowChanged(object? sender, EventArgs e)
    {
        // Formation state is published by the Runtime controller thread. Keep
        // all Avalonia-bound property notifications on the UI dispatcher so a
        // 60 Hz formation update cannot freeze the desktop or race the unit
        // list while the Ghost physics loop is running.
        if (_dispatcher.CheckAccess())
        {
            OnFormationChanged();
            return;
        }

        _ = _dispatcher.InvokeAsync(OnFormationChanged);
    }

    private string? SelectedRuntimeId(IReadOnlyList<RuntimeRecord> records)
    {
        var selectedVehicleId = _selection.SelectedUnitIds.Count > 0 ? _selection.SelectedUnitIds[0] : null;
        if (!string.IsNullOrWhiteSpace(selectedVehicleId))
        {
            selectedVehicleId = _reconciliation?.ResolveCommandSource(selectedVehicleId) ?? selectedVehicleId;
            return records.FirstOrDefault(item =>
                string.Equals(item.VehicleId, selectedVehicleId, StringComparison.Ordinal))?.Id;
        }

        return _selection.Current.Kind switch
        {
            SelectionKind.Runtime => records.Any(item => item.Id == _selection.Current.Id)
                ? _selection.Current.Id
                : null,
            SelectionKind.Vehicle => records.FirstOrDefault(item =>
                string.Equals(item.VehicleId, _selection.Current.Id, StringComparison.Ordinal))?.Id,
            _ => null
        };
    }

    private void RefreshDetails()
    {
        ConnectionSummaries.Clear();
        SelectedStatusIndicators.Clear();
        SelectedConnectionChips.Clear();
        if (SelectedUnit is not null)
            foreach (var id in SelectedUnit.ConnectionIds ?? [])
            {
                var connection = _connectionStore.Items.FirstOrDefault(item => item.Id == id);
                ConnectionSummaries.Add(connection is null ? id : $"{connection.Name} — {connection.Target}");
            }
        if (SelectedUnit is not null)
        {
            var observation = SelectedObservation;
            foreach (var indicator in BuildStatusIndicators(observation))
                SelectedStatusIndicators.Add(indicator);
            foreach (var chip in BuildConnectionChips(observation))
                SelectedConnectionChips.Add(chip);
        }
        OnPropertyChanged(nameof(SelectedTitle)); OnPropertyChanged(nameof(SelectedSubtitle)); OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(HasSelectedUnit)); OnPropertyChanged(nameof(HasSingleSelectedUnit));
        OnPropertyChanged(nameof(HasMultipleSelectedUnits));
        OnPropertyChanged(nameof(IsSelectedRemoteObserver));
        OnPropertyChanged(nameof(CanManageUnitConnections));
        OnPropertyChanged(nameof(SelectedConnectionAssociationSummary));
        OnPropertyChanged(nameof(SelectedUnitCount)); OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedIdentity)); OnPropertyChanged(nameof(SelectedHealth)); OnPropertyChanged(nameof(SelectedReadiness));
        OnPropertyChanged(nameof(SelectedOverallStatus)); OnPropertyChanged(nameof(SelectedBlockerSummary));
        OnPropertyChanged(nameof(SelectedPosition)); OnPropertyChanged(nameof(SelectedHeading)); OnPropertyChanged(nameof(SelectedVehicleState)); OnPropertyChanged(nameof(SelectedCurrentAction));
        OnPropertyChanged(nameof(SelectedVelocity)); OnPropertyChanged(nameof(SelectedVerticalSpeed)); OnPropertyChanged(nameof(SelectedOperatorDistance));
        OnPropertyChanged(nameof(SelectedBattery)); OnPropertyChanged(nameof(SelectedBatteryTooltip)); OnPropertyChanged(nameof(SelectedSignalTooltip));
        OnPropertyChanged(nameof(HasSelectedConnectionChips));
        OnPropertyChanged(nameof(SelectedMissionExecution)); OnPropertyChanged(nameof(HasSelectedMissionExecution));
        OnPropertyChanged(nameof(SelectedMissionExecutionState)); OnPropertyChanged(nameof(SelectedMissionExecutionSummary));
        OnPropertyChanged(nameof(SelectedMissionExecutionProgress)); OnPropertyChanged(nameof(SelectedMissionExecutionStep));
        OnPropertyChanged(nameof(SelectedManualControlStatus));
        OnPropertyChanged(nameof(IsSelectedUnitUnderManualControl));
        OnPropertyChanged(nameof(SelectedManualControlMode));
        OnPropertyChanged(nameof(SelectedManualControlDetail));
        OnPropertyChanged(nameof(HasSelectedManualPendingCommand));
        OnPropertyChanged(nameof(SelectedManualPendingAction));
        OnPropertyChanged(nameof(SelectedManualPendingExpiry));
        OnPropertyChanged(nameof(SelectedQueuedAction)); OnPropertyChanged(nameof(SelectedQueuedAvailability)); OnPropertyChanged(nameof(HasQueuedCommandsForSelection));
        OnPropertyChanged(nameof(HasExecutingCommand)); OnPropertyChanged(nameof(ShowClearQueuedCommand));
        OnPropertyChanged(nameof(SelectedConnections)); OnPropertyChanged(nameof(SelectedPlatform)); OnPropertyChanged(nameof(SelectedVersion));
        (ManageUnitConnectionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private IEnumerable<UnitStatusIndicatorViewModel> BuildStatusIndicators(UnitObservationSnapshot? observation)
    {
        var state = observation?.State ?? (ManagedConnectionState)(int)(SelectedUnit?.State ?? AvailabilityState.Unknown);
        yield return UnitStatusIndicatorViewModel.FromState("Connection", state.ToString(), state);

        var diagnostics = observation?.Diagnostics;
        var readiness = diagnostics?.OverallStatus ?? SelectedOverallStatus;
        yield return UnitStatusIndicatorViewModel.FromDiagnostic("Ready", readiness);

        var vehicleState = SelectedVehicleState;
        yield return new UnitStatusIndicatorViewModel(
            vehicleState is "Armed" or "Flying" ? "●" : "—",
            vehicleState,
            vehicleState is "Armed" or "Flying" ? "#32D583" : "#8A96A8",
            $"Vehicle state: {vehicleState}.");

        var battery = SelectedBattery;
        yield return new UnitStatusIndicatorViewModel(
            battery == "—" ? "?" : "▣",
            battery,
            battery == "—" ? "#8A96A8" : battery.TrimEnd('%') is { } raw && double.TryParse(raw, out var percent) && percent <= 20 ? "#F5C451" : "#32D583",
            SelectedBatteryTooltip);

        var signal = BuildSignalIndicator(observation);
        yield return signal;
    }

    private UnitStatusIndicatorViewModel BuildSignalIndicator(UnitObservationSnapshot? observation)
    {
        var links = observation?.Links ?? [];
        if (links.Count == 0)
        {
            if (observation?.IsGhost == true)
                return new UnitStatusIndicatorViewModel("—", "SIM", "#8A96A8", "Simulator link; RF signal strength is not applicable.");
            return new UnitStatusIndicatorViewModel("?", "—", "#8A96A8", "No connection signal metrics were reported.");
        }

        var degraded = links.Any(item => item.IsStale || !item.Connected || item.PacketLoss is >= 10 || item.SnrDb is < 10);
        var severe = links.Any(item => !item.Connected || item.PacketLoss is >= 30 || item.SnrDb is < 6);
        var brush = severe ? "#F05252" : degraded ? "#F5C451" : "#32D583";
        var text = links.Count == 1
            ? (links[0].Quality is double quality ? $"{Math.Clamp(quality <= 1 ? quality * 100 : quality, 0, 100):0}%" : "✓")
            : $"{links.Count} links";
        return new UnitStatusIndicatorViewModel(severe ? "✕" : degraded ? "!" : "✓", text, brush, SelectedSignalTooltip);
    }

    private IEnumerable<UnitConnectionChipViewModel> BuildConnectionChips(UnitObservationSnapshot? observation)
    {
        if (observation?.AssociatedConnections is { Count: > 0 } connections)
        {
            foreach (var connection in connections)
            {
                var link = observation.Links.FirstOrDefault(item => item.ConnectionId == connection.ConnectionId);
                yield return UnitConnectionChipViewModel.Create(connection, link);
            }
            yield break;
        }

        foreach (var summary in ConnectionSummaries)
            yield return new UnitConnectionChipViewModel(summary, "#8A96A8", summary);
    }

    private static string NormalizeVehicleState(string? armState, string? lifecycle)
        => string.Equals(armState, "Armed", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(lifecycle, "Flying", StringComparison.OrdinalIgnoreCase) ? "Flying" : "Landed"
            : "Disarmed";

    private static string ResolvePlatformLabel(string? platform)
        => string.Equals(platform, "Ghost", StringComparison.OrdinalIgnoreCase)
            ? LocalizationService.Current.Get("UnitKindGhost")
            : platform ?? string.Empty;

    private static string LocalizeUnitState(string state)
        => state switch
        {
            "Online" => LocalizationService.Current.Get("UnitOnline"),
            "Offline" => LocalizationService.Current.Get("UnitOffline"),
            "Degraded" => LocalizationService.Current.Get("UnitDegraded"),
            "Reconnecting" => LocalizationService.Current.Get("UnitReconnecting"),
            "Stale" => LocalizationService.Current.Get("UnitStale"),
            "Faulted" => LocalizationService.Current.Get("UnitFaulted"),
            _ => state
        };

    private static string FormatAge(DateTimeOffset observedAt)
    {
        var age = DateTimeOffset.UtcNow - observedAt;
        return age.TotalSeconds < 1 ? "just now" : $"{Math.Max(1, Math.Round(age.TotalSeconds)):0}s ago";
    }

    private void SelectUnitTarget(OperationalSelection target)
    {
        // Runtime records are refreshed frequently. Do not publish a new
        // selection merely because state/health fields changed; that causes
        // Operate and Quick Run to reset their target and flicker.
        if (_selection.Current.Kind != target.Kind ||
            !string.Equals(_selection.Current.Id, target.Id, StringComparison.Ordinal))
        {
            _selection.Select(target);
        }
    }
}

internal sealed class EmptyUnitObservationWorkflow : IUnitObservationWorkflow
{
    public static readonly EmptyUnitObservationWorkflow Instance = new();
    public event EventHandler? Changed { add { } remove { } }
    public IReadOnlyList<UnitObservationSnapshot> Units => [];
    public bool TryGet(string unitId, out UnitObservationSnapshot? unit) { unit = null; return false; }
}

public sealed record FormationSlotOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Stable visual identity for a unit list row during live telemetry refreshes.</summary>
public sealed class UnitListItemViewModel : ObservableObject
{
    private RuntimeRecord _record;
    private ManualUnitDefinition? _association;
    private UnitObservationSnapshot? _observation;
    private string? _teamId;
    private string? _teamName;
    private int _teamOrder = -1;
    private int _teamMemberCount;
    private bool _isSelected;
    private bool _isTeamSelected;
    private IReadOnlyList<FormationSlotOption> _formationSlots = [];
    private FormationSlotOption? _selectedFormationSlot;
    private readonly ObservableCollection<UnitStatusIndicatorViewModel> _listStatusIndicators = [];
    private static readonly IBrush SelectedRowBrush = new SolidColorBrush(Color.Parse("#8F0B19"));
    private static readonly IBrush TeamSelectedRowBrush = new SolidColorBrush(Color.Parse("#244C68"));

    public UnitListItemViewModel(RuntimeRecord record, ManualUnitDefinition? association = null, UnitObservationSnapshot? observation = null)
    {
        _record = record;
        _association = association;
        _observation = observation;
        UpdateStatusIndicators();
    }

    public string Id => _record.Id;
    public RuntimeRecord Record => _record;
    public string? VehicleName => _record.VehicleName;
    public string Name => _record.Name;
    public IReadOnlyList<string> ConnectionIds => _record.ConnectionIds;
    public AvailabilityState State => _record.State;
    public bool IsGhost => _record.IsGhost;
    public bool IsSelected => _isSelected;
    public IBrush RowBackground => _isSelected ? SelectedRowBrush : _isTeamSelected ? TeamSelectedRowBrush : Brushes.Transparent;
    public bool IsTeamSelected => _isTeamSelected;
    public bool IsRemoteObserver => string.Equals(_record.Role, "Remote observer", StringComparison.Ordinal);
    public bool IsManuallyDefined => _association is not null;
    public string KindLabel => ResolveKindLabel(_observation, IsGhost);
    // Keep the item instances stable. Replacing this collection on every
    // telemetry batch forces the row's ItemsControl to tear down and recreate
    // its pills, which can make a multi-selected list visibly jump.
    public IReadOnlyList<UnitStatusIndicatorViewModel> ListStatusIndicators
        => _listStatusIndicators;
    public int ConnectionCount => _association?.Connections.Count ?? _record.ConnectionIds.Count;
    public string ConnectionLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("UnitConnectionLabel"), ConnectionCount);
    public string? TeamId => _teamId;
    public string? TeamName => _teamName;
    public int TeamOrder => _teamOrder;
    public bool IsInTeam => _teamId is not null;
    public bool IsTeamFirst => IsInTeam && _teamOrder == 0;
    public bool IsTeamLast => IsInTeam && _teamOrder == _teamMemberCount - 1;
    public IBrush TeamBorderBrush => IsInTeam ? new SolidColorBrush(Color.Parse("#547da8")) : Brushes.Transparent;
    public Thickness TeamBorderThickness => IsInTeam
        ? new Thickness(1, IsTeamFirst ? 1 : 0, 1, IsTeamLast ? 1 : 0)
        : new Thickness(0);
    public CornerRadius TeamCornerRadius => IsInTeam && IsTeamFirst && IsTeamLast
        ? new CornerRadius(6)
        : new CornerRadius(IsTeamFirst ? 6 : 0, IsTeamFirst ? 6 : 0, IsTeamLast ? 6 : 0, IsTeamLast ? 6 : 0);
    public string? TeamTooltip => IsInTeam
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Current.Get("TeamTooltip"), _teamName, _teamOrder + 1, _teamMemberCount)
        : null;
    public IReadOnlyList<FormationSlotOption> FormationSlots => _formationSlots;
    public bool HasFormationSlots => _formationSlots.Count > 0;
    public Action<UnitListItemViewModel, FormationSlotOption>? FormationSlotChanged { get; set; }
    public FormationSlotOption? SelectedFormationSlot
    {
        get => _selectedFormationSlot;
        set
        {
            if (ReferenceEquals(_selectedFormationSlot, value) || value?.Id == _selectedFormationSlot?.Id) return;
            _selectedFormationSlot = value;
            OnPropertyChanged(nameof(SelectedFormationSlot));
            if (value is not null) FormationSlotChanged?.Invoke(this, value);
        }
    }

    public void SetTeam(string teamId, string teamName, int order, int memberCount)
    {
        _teamId = teamId;
        _teamName = teamName;
        _teamOrder = order;
        _teamMemberCount = memberCount;
        NotifyTeamProperties();
    }

    public void ClearTeam()
    {
        if (_teamId is null) return;
        _teamId = null;
        _teamName = null;
        _teamOrder = -1;
        _teamMemberCount = 0;
        NotifyTeamProperties();
    }

    public void SetFormationSlots(IReadOnlyList<FormationSlotOption> slots, string? selectedId)
    {
        _formationSlots = slots;
        _selectedFormationSlot = slots.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(FormationSlots));
        OnPropertyChanged(nameof(HasFormationSlots));
        OnPropertyChanged(nameof(SelectedFormationSlot));
    }

    public void ClearFormationSlots() => SetFormationSlots([], null);

    public void NotifyTeamProperties()
    {
        OnPropertyChanged(nameof(TeamId));
        OnPropertyChanged(nameof(TeamName));
        OnPropertyChanged(nameof(TeamOrder));
        OnPropertyChanged(nameof(IsInTeam));
        OnPropertyChanged(nameof(IsTeamFirst));
        OnPropertyChanged(nameof(IsTeamLast));
        OnPropertyChanged(nameof(TeamBorderBrush));
        OnPropertyChanged(nameof(TeamBorderThickness));
        OnPropertyChanged(nameof(TeamCornerRadius));
        OnPropertyChanged(nameof(TeamTooltip));
    }

    public void RefreshLocalizedPresentation()
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(ConnectionLabel));
        UpdateStatusIndicators();
    }

    public void SetSelected(bool selected)
    {
        if (_isSelected == selected) return;
        _isSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(RowBackground));
    }

    public void SetTeamSelected(bool selected)
    {
        if (_isTeamSelected == selected) return;
        _isTeamSelected = selected;
        OnPropertyChanged(nameof(IsTeamSelected));
        OnPropertyChanged(nameof(RowBackground));
    }

    public void Update(RuntimeRecord record, ManualUnitDefinition? association = null, UnitObservationSnapshot? observation = null)
    {
        var previousRecord = _record;
        var previousAssociation = _association;
        var previousKind = KindLabel;
        _record = record;
        _association = association;
        _observation = observation;
        if (!Equals(previousRecord, record)) OnPropertyChanged(nameof(Record));
        if (!string.Equals(previousRecord.VehicleName, record.VehicleName, StringComparison.Ordinal)) OnPropertyChanged(nameof(VehicleName));
        if (!string.Equals(previousRecord.Name, record.Name, StringComparison.Ordinal)) OnPropertyChanged(nameof(Name));
        if (!previousRecord.ConnectionIds.SequenceEqual(record.ConnectionIds, StringComparer.Ordinal)) OnPropertyChanged(nameof(ConnectionIds));
        if (previousRecord.State != record.State) OnPropertyChanged(nameof(State));
        if (previousRecord.IsGhost != record.IsGhost) OnPropertyChanged(nameof(IsGhost));
        if (!string.Equals(previousRecord.Role, record.Role, StringComparison.Ordinal)) OnPropertyChanged(nameof(IsRemoteObserver));
        if ((previousAssociation is null) != (association is null)) OnPropertyChanged(nameof(IsManuallyDefined));
        if (!string.Equals(previousKind, KindLabel, StringComparison.Ordinal)) OnPropertyChanged(nameof(KindLabel));
        if (previousAssociation?.Connections.Count != association?.Connections.Count || previousRecord.ConnectionIds.Count != record.ConnectionIds.Count)
        {
            OnPropertyChanged(nameof(ConnectionCount));
            OnPropertyChanged(nameof(ConnectionLabel));
        }
        UpdateStatusIndicators();
    }

    private void UpdateStatusIndicators()
    {
        var next = BuildStatusIndicators(_observation);
        while (_listStatusIndicators.Count > next.Count)
            _listStatusIndicators.RemoveAt(_listStatusIndicators.Count - 1);
        for (var index = 0; index < next.Count; index++)
        {
            if (index == _listStatusIndicators.Count)
                _listStatusIndicators.Add(next[index]);
            else
                _listStatusIndicators[index].Update(next[index]);
        }
    }

    private static string ResolveKindLabel(UnitObservationSnapshot? observation, bool isGhost)
    {
        if (isGhost) return LocalizationService.Current.Get("UnitKindGhost");

        var profile = observation?.ProfileKey ?? string.Empty;
        if (profile.Contains("ardupilot", StringComparison.OrdinalIgnoreCase)) return "ArduPilot";
        if (profile.Contains("px4", StringComparison.OrdinalIgnoreCase)) return "PX4";
        if (profile.Contains("logos", StringComparison.OrdinalIgnoreCase)) return "Logos";
        return string.IsNullOrWhiteSpace(profile) ? string.Empty : profile;
    }

    private static IReadOnlyList<UnitStatusIndicatorViewModel> BuildStatusIndicators(UnitObservationSnapshot? observation)
    {
        var localization = LocalizationService.Current;
        var telemetry = observation?.Telemetry;
        var battery = telemetry?.BatteryRemainingPercent is double percent && double.IsFinite(percent)
            ? $"{Math.Clamp(percent, 0, 100):0}%"
            : "—";
        var batteryBrush = battery == "—" ? "#8A96A8" : battery.TrimEnd('%') is { } raw && double.TryParse(raw, out var value) && value <= 20 ? "#F5C451" : "#32D583";
        var batteryTooltip = battery == "—" ? localization.Get("UnitBatteryNotReported") : string.Format(CultureInfo.CurrentCulture, localization.Get("UnitBatteryTooltipShort"), battery);

        var links = observation?.Links ?? [];
        var degraded = links.Any(item => item.IsStale || !item.Connected || item.PacketLoss is >= 10 || item.SnrDb is < 10);
        var severe = links.Any(item => !item.Connected || item.PacketLoss is >= 30 || item.SnrDb is < 6);
        var signal = links.Count == 0
            ? (observation?.IsGhost == true ? "100%" : "—")
            : links.Count == 1 && links[0].Quality is double quality
                ? $"{Math.Clamp(quality <= 1 ? quality * 100 : quality, 0, 100):0}%"
                : links.Count == 1 ? "—" : string.Format(CultureInfo.CurrentCulture, localization.Get("UnitLinkCount"), links.Count);
        var signalBrush = links.Count == 0 && observation?.IsGhost != true ? "#8A96A8" : severe ? "#F05252" : degraded ? "#F5C451" : "#32D583";
        var signalSymbol = links.Count == 0 && observation?.IsGhost != true ? "?" : severe ? "✕" : degraded ? "!" : "✓";

        return
        [
            new UnitStatusIndicatorViewModel(signalSymbol, signal, signalBrush, links.Count == 0 ? localization.Get("UnitSimulatorLinkQuality") : localization.Get("UnitSignalQuality")),
            new UnitStatusIndicatorViewModel("▣", battery, batteryBrush, batteryTooltip)
        ];
    }
}

public sealed class UnitStatusIndicatorViewModel : ObservableObject
{
    private string _symbol;
    private string _value;
    private string _brush;
    private string _tooltip;

    public UnitStatusIndicatorViewModel(string symbol, string value, string brush, string tooltip)
    {
        _symbol = symbol;
        _value = value;
        _brush = brush;
        _tooltip = tooltip;
    }

    public string Symbol { get => _symbol; private set => SetProperty(ref _symbol, value); }
    public string Value { get => _value; private set => SetProperty(ref _value, value); }
    public string Brush { get => _brush; private set => SetProperty(ref _brush, value); }
    public string Tooltip { get => _tooltip; private set => SetProperty(ref _tooltip, value); }

    public void Update(UnitStatusIndicatorViewModel other)
    {
        Symbol = other.Symbol;
        Value = other.Value;
        Brush = other.Brush;
        Tooltip = other.Tooltip;
    }

    public static UnitStatusIndicatorViewModel FromState(string label, string value, ManagedConnectionState state)
    {
        var (symbol, brush) = state switch
        {
            ManagedConnectionState.Online => ("✓", "#32D583"),
            ManagedConnectionState.Degraded or ManagedConnectionState.Reconnecting or ManagedConnectionState.Stale => ("!", "#F5C451"),
            ManagedConnectionState.Offline or ManagedConnectionState.Faulted => ("✕", "#F05252"),
            _ => ("?", "#8A96A8")
        };
        return new UnitStatusIndicatorViewModel(symbol, value, brush, $"{label}: {value}.");
    }

    public static UnitStatusIndicatorViewModel FromDiagnostic(string label, string value)
    {
        var normalized = value.ToLowerInvariant();
        var (symbol, brush) = normalized switch
        {
            "ready" => ("✓", "#32D583"),
            "limited" or "stale" => ("!", "#F5C451"),
            "blocked" or "offline" => ("✕", "#F05252"),
            _ => ("?", "#8A96A8")
        };
        return new UnitStatusIndicatorViewModel(symbol, value, brush, $"{label}: {value}. Current diagnostic details are available in the Unit view.");
    }
}

public sealed class UnitConnectionChipViewModel
{
    public UnitConnectionChipViewModel(string label, string brush, string tooltip)
    {
        Label = label;
        Brush = brush;
        Tooltip = tooltip;
    }

    public string Label { get; }
    public string Brush { get; }
    public string Tooltip { get; }

    public static UnitConnectionChipViewModel Create(UnitConnectionObservation connection, UnitLinkObservation? link)
    {
        var brush = connection.State switch
        {
            ManagedConnectionState.Online => "#32D583",
            ManagedConnectionState.Degraded or ManagedConnectionState.Reconnecting or ManagedConnectionState.Stale => "#F5C451",
            ManagedConnectionState.Offline or ManagedConnectionState.Faulted => "#F05252",
            _ => "#8A96A8"
        };
        var roles = string.Join(", ", new[]
        {
            connection.IsCommandAuthority ? "command" : null,
            connection.IsTelemetryAuthority ? "telemetry" : null,
            connection.IsDiagnosticsAuthority ? "diagnostics" : null
        }.Where(item => item is not null));
        var lastSeen = connection.LastSeen is { } seen ? $" Last seen: {seen:yyyy-MM-dd HH:mm:ss} UTC." : " Last seen: not reported.";
        var linkDetails = link is null
            ? "Signal metrics were not reported."
            : $"RSSI {Format(link.RssiDbm, "dBm")}; SNR {Format(link.SnrDb, "dB")}; quality {FormatQuality(link.Quality)}; packet loss {Format(link.PacketLoss, "%")}; latency {Format(link.LatencyMilliseconds, "ms")}.";
        var tooltip = $"{connection.Name} · {connection.Backend} · {connection.State}. " +
                      (string.IsNullOrWhiteSpace(roles) ? "No authority role assigned. " : $"Authority: {roles}. ") +
                      $"Target: {connection.Target}. {linkDetails}{lastSeen}";
        return new UnitConnectionChipViewModel(connection.Name, brush, tooltip);
    }

    private static string Format(double? value, string suffix)
        => value is double number && double.IsFinite(number) ? $"{number:0.#} {suffix}" : "not reported";

    private static string FormatQuality(double? value)
        => value is double number && double.IsFinite(number)
            ? $"{(number <= 1 ? number * 100 : number):0.#}%"
            : "not reported";
}
