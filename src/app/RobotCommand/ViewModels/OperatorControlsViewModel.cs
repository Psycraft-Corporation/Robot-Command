using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Terrain;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class OperatorControlsViewModel : ObservableObject
{
    private readonly IOperatorCommandWorkflow _workflow;
    private readonly ISelectionService _selection;
    private readonly IOperatorTargetScopeWorkflow? _targetScope;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IReadOnlyList<IFormationProvider> _formationProviders;
    private readonly AsyncRelayCommand _executePendingCommand;
    private readonly AsyncRelayCommand _cancelExecutingCommandsCommand;
    private OperatorCommandQueueSnapshot? _pendingPlan;
    private IReadOnlyList<OperatorCommandQueueSnapshot> _pendingPlans = [];
    private string? _selectedVehicleId;
    private string _selectedVehicleText = "No vehicle selected";
    private string _reason = "Operator request";
    private string _confirmationText = "";
    private string _statusMessage;
    private string _parameterValidationMessage = "";
    private string _takeoffAltitudeText = "5";
    private string _altitudeTargetText = "20";
    private string _headingTargetText = "";
    private string _goToLatitudeText = "";
    private string _goToLongitudeText = "";
    private string? _pendingCommandLabel;
    private bool _pendingAssembly;
    private bool _isPreparingMapCommand;
    private bool _showPendingFindings;
    private bool _showAirborneDisarmConfirmation;
    private OperatorCommandQueueSnapshot[] _airborneDisarmPlans = [];
    private string _rejectedCommandMessage = string.Empty;
    private string _commandOutcomeTitle = "Command rejected";
    private bool _isRejectedCommandExpanded;
    private readonly IUnitSettingsService? _unitSettings;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ITerrainElevationService? _terrain;

    public OperatorControlsViewModel(
        IOperatorCommandWorkflow workflow,
        ISelectionService selection,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, OperationalCommandRecord> commands,
        IEnumerable<IFormationProvider>? formationProviders = null,
        IUnitSettingsService? unitSettings = null,
        IUiDispatcher? dispatcher = null,
        IOperatorTargetScopeWorkflow? targetScope = null,
        ITerrainElevationService? terrain = null)
    {
        _workflow = workflow;
        _selection = selection;
        _targetScope = targetScope;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _connections = connections;
        _formationProviders = (formationProviders ?? []).ToArray();
        _unitSettings = unitSettings;
        _dispatcher = dispatcher;
        _terrain = terrain;
        if (_unitSettings is not null) _unitSettings.Changed += OnUnitSettingsChanged;
        _takeoffAltitudeText = FormatDisplayValue(TakeoffAltitudeDisplay);
        _altitudeTargetText = AltitudeTargetDisplay is double altitude ? FormatDisplayValue(altitude) : "";
        CommandHistory = commands.Items;
        Findings = new ObservableCollection<OperatorWorkflowFinding>();
        // Keep the Operations inspector quiet until an operator action produces
        // a status. The gateway description is implementation detail, not an
        // instruction the operator needs to read on every screen.
        _statusMessage = string.Empty;

        _selection.Changed += OnSelectionChanged;
        if (_targetScope is not null) _targetScope.Changed += OnSelectionChanged;
        Subscribe(_vehicles.Items);
        Subscribe(_telemetry.Items);
        Subscribe(_connections.Items);
        ((INotifyCollectionChanged)CommandHistory).CollectionChanged += OnCommandHistoryChanged;
        _workflow.Changed += OnWorkflowChanged;

        PrepareArmCommand = CreatePrepareCommand(OperatorCommandKind.Arm);
        PrepareDisarmCommand = CreatePrepareCommand(OperatorCommandKind.Disarm);
        PrepareHoldCommand = CreatePrepareCommand(OperatorCommandKind.Hold);
        PrepareTakeoffCommand = CreatePrepareCommand(OperatorCommandKind.Takeoff);
        PrepareGoToCommand = CreatePrepareCommand(OperatorCommandKind.GoTo);
        PrepareLandCommand = CreatePrepareCommand(OperatorCommandKind.Land);
        PrepareRecoverCommand = CreatePrepareCommand(OperatorCommandKind.Recover);
        PrepareChangeAltitudeCommand = CreatePrepareCommand(OperatorCommandKind.ChangeAltitude);
        PrepareSetHeadingCommand = CreatePrepareCommand(OperatorCommandKind.SetHeading);
        PrepareCapturePhotoCommand = CreatePrepareCommand(OperatorCommandKind.CapturePhoto);
        PrepareStartVideoCommand = CreatePrepareCommand(OperatorCommandKind.StartVideo);
        PrepareStopVideoCommand = CreatePrepareCommand(OperatorCommandKind.StopVideo);
        PrepareCenterGimbalCommand = CreatePrepareCommand(OperatorCommandKind.CenterGimbal);
        PrepareNadirGimbalCommand = CreatePrepareCommand(OperatorCommandKind.NadirGimbal);
        PrepareSetGimbalCommand = CreatePrepareCommand(OperatorCommandKind.SetGimbal);
        ToggleVideoCommand = new AsyncRelayCommand(ToggleVideoAsync, () => CanPrepareCommand(IsVideoRecording ? OperatorCommandKind.StopVideo : OperatorCommandKind.StartVideo));
        PrepareMapGoToCommand = new RelayCommand(
            parameter => _ = PrepareMapCommandAsync(OperatorCommandKind.GoTo, parameter),
            parameter => HasVehicleSelection && parameter is MapCommandTarget);
        PrepareMapSetHeadingCommand = new RelayCommand(
            parameter => _ = PrepareMapCommandAsync(OperatorCommandKind.SetHeading, parameter),
            parameter => HasCommandSelection && parameter is MapCommandTarget);
        PrepareMapAssembleCommand = new RelayCommand(
            parameter => _ = PrepareMapAssemblyCommandAsync(parameter),
            parameter => HasMultiUnitSelection && parameter is MapAssemblyRequest);
        PrepareMapPointGimbalCommand = new RelayCommand(
            parameter => _ = PrepareMapPointGimbalAsync(parameter),
            parameter => HasGimbalCameraSelection && parameter is MapCommandTarget);
        _executePendingCommand = new AsyncRelayCommand(ExecutePendingAsync, CanExecutePending);
        ExecutePendingCommand = _executePendingCommand;
        ConfirmAirborneDisarmCommand = new AsyncRelayCommand(
            ConfirmAirborneDisarmAsync,
            () => IsAirborneDisarmConfirmationVisible);
        CancelAirborneDisarmCommand = new RelayCommand(
            _ => HideAirborneDisarmConfirmation(),
            _ => IsAirborneDisarmConfirmationVisible);
        _cancelExecutingCommandsCommand = new AsyncRelayCommand(CancelExecutingAsync, CanCancelExecuting);
        CancelExecutingCommandsCommand = _cancelExecutingCommandsCommand;
        TogglePendingFindingsCommand = new RelayCommand(
            _ => ShowPendingFindings = !ShowPendingFindings,
            _ => HasPendingPlan);
        DismissRejectedCommand = new RelayCommand(
            _ => ClearRejectedCommand(),
            _ => HasRejectedCommand);
        ToggleRejectedCommand = new RelayCommand(
            _ => IsRejectedCommandExpanded = !IsRejectedCommandExpanded,
            _ => HasRejectedCommand);
        CancelPendingCommand = new AsyncRelayCommand(CancelPendingAsync, () => HasQueuedCommandsForSelection);
        ExecuteQueuedCommandsCommand = new AsyncRelayCommand(
            _ => ExecuteQueuedForSelectionAsync(),
            CanExecuteQueuedForSelection);
        ClearQueuedCommandsCommand = new AsyncRelayCommand(
            _ => ClearQueuedForSelectionAsync(),
            () => HasQueuedCommandsForSelection);
        ApplySelection();
    }

    public ObservableCollection<OperatorWorkflowFinding> Findings { get; }

    public ReadOnlyObservableCollection<OperationalCommandRecord> CommandHistory { get; }

    public ICommand PrepareArmCommand { get; }
    public ICommand PrepareDisarmCommand { get; }
    public ICommand PrepareHoldCommand { get; }
    public ICommand PrepareTakeoffCommand { get; }
    public ICommand PrepareGoToCommand { get; }
    public ICommand PrepareLandCommand { get; }
    public ICommand PrepareRecoverCommand { get; }
    public ICommand PrepareChangeAltitudeCommand { get; }
    public ICommand PrepareSetHeadingCommand { get; }
    public ICommand PrepareCapturePhotoCommand { get; }
    public ICommand PrepareStartVideoCommand { get; }
    public ICommand PrepareStopVideoCommand { get; }
    public ICommand PrepareCenterGimbalCommand { get; }
    public ICommand PrepareNadirGimbalCommand { get; }
    public ICommand PrepareSetGimbalCommand { get; }
    public ICommand ToggleVideoCommand { get; }
    public ICommand PrepareMapGoToCommand { get; }
    public ICommand PrepareMapSetHeadingCommand { get; }
    public ICommand PrepareMapAssembleCommand { get; }
    public ICommand PrepareMapPointGimbalCommand { get; }
    public ICommand ExecutePendingCommand { get; }
    public ICommand ConfirmAirborneDisarmCommand { get; }
    public ICommand CancelAirborneDisarmCommand { get; }

    public bool IsAirborneDisarmConfirmationVisible
    {
        get => _showAirborneDisarmConfirmation;
        private set
        {
            if (!SetProperty(ref _showAirborneDisarmConfirmation, value))
                return;
            (ConfirmAirborneDisarmCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelAirborneDisarmCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public ICommand CancelExecutingCommandsCommand { get; }
    public ICommand ClearQueuedCommandsCommand { get; }
    public ICommand CancelPendingCommand { get; }
    public ICommand ExecuteQueuedCommandsCommand { get; }
    public ICommand TogglePendingFindingsCommand { get; }
    public ICommand DismissRejectedCommand { get; }
    public ICommand ToggleRejectedCommand { get; }

    public OperatorCommandWorkflowStatus GatewayStatus => _workflow.Status;

    public MapGoToTargetVisual? ActiveGoToTarget => ActiveGoToTargets.Count > 0 ? ActiveGoToTargets[0] : null;

    public IReadOnlyList<MapGoToTargetVisual> ActiveGoToTargets => _workflow.ActiveCommands
        .Where(plan => plan.Command == OperatorWorkflowCommandKind.GoTo &&
                       plan.Parameters.GoToLatitudeDegrees is not null &&
                       plan.Parameters.GoToLongitudeDegrees is not null)
        .Select(plan => new MapGoToTargetVisual(plan.CommandAuthorityUnitId,
            plan.Parameters.GoToLatitudeDegrees!.Value, plan.Parameters.GoToLongitudeDegrees!.Value))
        .ToArray();

    public IReadOnlyList<MapGoToTargetVisual> QueuedGoToTargets
        => _workflow.QueuedCommands
            .Where(plan => plan.Command == OperatorWorkflowCommandKind.GoTo &&
                           plan.Parameters.GoToLatitudeDegrees is double &&
                           plan.Parameters.GoToLongitudeDegrees is double)
            .Select(plan => new MapGoToTargetVisual(
                plan.CommandAuthorityUnitId,
                plan.Parameters.GoToLatitudeDegrees!.Value,
                plan.Parameters.GoToLongitudeDegrees!.Value)
            {
                Selected = TargetUnitIds.Contains(plan.UnitId, StringComparer.Ordinal),
                PreviewOnly = true
            })
            .ToArray();

    public event EventHandler? ActiveGoToTargetChanged;
    public event EventHandler? FormationPreviewChanged;
    public event EventHandler? QueuedCommandsChanged;

    public IReadOnlyList<IFormationProvider> FormationProviders => _formationProviders;
    public IReadOnlyList<MapGoToTargetVisual> FormationPreviewTargets { get; private set; } = [];
    public IReadOnlyList<FormationPreviewPath> FormationPreviewPaths { get; private set; } = [];

    public IReadOnlyDictionary<string, OperatorCommandQueueSnapshot> QueuedPlans => _workflow.QueuedCommands
        .ToDictionary(plan => plan.UnitId, StringComparer.Ordinal);

    public bool HasQueuedCommandsForSelection => GetSelectedQueuedPlans().Length > 0;

    public bool HasQueuedCommand => GetSelectedQueuedPlans().Length > 0;

    public bool HasExecutingCommand => GetSelectedExecutingPlans().Length > 0;

    public bool ShowCancelExecutingCommand => HasExecutingCommand;

    public bool ShowClearQueuedCommand => HasQueuedCommand && !HasExecutingCommand;

    public bool ShowClearQueuedForSelection => HasQueuedCommand && !HasExecutingCommand;

    public string QueuedActionFor(string vehicleId)
        => _workflow.QueuedCommands.FirstOrDefault(plan => string.Equals(plan.UnitId, vehicleId, StringComparison.Ordinal)) is { } plan
            ? plan.DisplayName
            : "None queued";

    public string SelectedQueuedAction
    {
        get
        {
            var plans = GetSelectedQueuedPlans();
            if (plans.Length == 0)
                return "None queued";
            return plans.Length == 1
                    ? $"{plans[0].DisplayName} · {plans[0].Availability}"
                    : $"{plans.Length} queued actions · {QueueAvailability(plans)}";
        }
    }

    public string SelectedQueuedAvailability
    {
        get
        {
            var plans = GetSelectedQueuedPlans();
            if (plans.Length == 0)
                return "No queued command";

            if (plans.Length == 1)
                return DescribeQueuedAvailability(plans[0]);

            var unavailable = plans.Count(plan => plan.Availability is
                OperatorWorkflowAvailability.Blocked or OperatorWorkflowAvailability.Unavailable);
            var warnings = plans.Count(plan => plan.Availability == OperatorWorkflowAvailability.Warning);
            return unavailable > 0
                ? $"Unavailable for {unavailable} of {plans.Length} units: {plans.First(plan => plan.Availability is OperatorWorkflowAvailability.Blocked or OperatorWorkflowAvailability.Unavailable).Findings.FirstOrDefault(item => item.Severity == OperatorWorkflowSeverity.Blocking)?.Message ?? "readiness or policy check failed"}"
                : warnings > 0
                    ? $"Available with warnings for {warnings} of {plans.Length} units"
                    : $"Available for {plans.Length} units";
        }
    }

    private static string DescribeQueuedAvailability(OperatorCommandQueueSnapshot plan)
    {
        return plan.Availability switch
        {
            OperatorWorkflowAvailability.Ready => "Available",
            OperatorWorkflowAvailability.Warning => "Available with warnings",
            _ => DescribeUnavailable(plan)
        };
    }

    private static string DescribeUnavailable(OperatorCommandQueueSnapshot plan)
    {
        var blocker = plan.Findings.FirstOrDefault(item => item.Severity == OperatorWorkflowSeverity.Blocking);
        return blocker is null
            ? "Unavailable"
            : $"Unavailable: {blocker.Message}";
    }

    public void UpdateFormationPreview(MapAssemblyRequest request)
    {
        var provider = _formationProviders.FirstOrDefault(item =>
            string.Equals(item.Id, request.FormationId, StringComparison.OrdinalIgnoreCase));
        var units = TargetUnitIds
            .Select(id => _vehicles.TryGet(id, out var vehicle) ? vehicle : null)
            .Where(vehicle => vehicle is not null)
            .Cast<VehicleRecord>()
            .ToArray();
        if (provider is null || !provider.CanCalculate(units))
        {
            ClearFormationPreview();
            return;
        }

        try
        {
            var calculation = provider.Calculate(units, request.Start, request.End);
            FormationPreviewTargets = calculation.Destinations
                .Select(item => new MapGoToTargetVisual(item.VehicleId, item.LatitudeDegrees, item.LongitudeDegrees)
                {
                    Selected = true
                })
                .ToArray();
            FormationPreviewPaths = calculation.PreviewPaths;
            FormationPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            ClearFormationPreview();
        }
    }

    public void UpdateFormationStartPreview(MapCommandTarget point)
    {
        var vehicleId = TargetUnitIds.Count > 0 ? TargetUnitIds[0] : null;
        if (vehicleId is null)
        {
            ClearFormationPreview();
            return;
        }

        FormationPreviewTargets =
        [
            new MapGoToTargetVisual(vehicleId, point.LatitudeDegrees, point.LongitudeDegrees)
            {
                Selected = true,
                PreviewOnly = true
            }
        ];
        FormationPreviewPaths = [];
        FormationPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearFormationPreview()
    {
        if (FormationPreviewTargets.Count == 0 && FormationPreviewPaths.Count == 0)
            return;
        FormationPreviewTargets = [];
        FormationPreviewPaths = [];
        FormationPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GatewayStatusText => GatewayStatus.GatewayAvailable
        ? "OPERATOR COMMAND BACKEND AVAILABLE"
        : "OPERATOR COMMAND BACKEND NOT YET AVAILABLE";

    public bool GatewayAvailable => GatewayStatus.GatewayAvailable;

    private int SelectedCommandTargetCount => TargetUnitIds.Count > 0
        ? TargetUnitIds.Count
        : _selectedVehicleId is null ? 0 : 1;

    private IReadOnlyList<string> SelectedCommandVehicleIds
        => TargetUnitIds.Count > 0
            ? TargetUnitIds
            : _selectedVehicleId is null ? [] : [_selectedVehicleId];

    private IReadOnlyList<string> TargetUnitIds => _targetScope?.Current.UnitIds is { Count: > 0 } ids
        ? ids
        : _selection.SelectedUnitIds;

    public bool HasVehicleSelection => SelectedCommandTargetCount == 1 &&
                                       !string.IsNullOrWhiteSpace(_selectedVehicleId);

    public bool HasMultiUnitSelection => TargetUnitIds.Count > 1;

    public bool HasCommandSelection => SelectedCommandTargetCount > 0 &&
                                       !string.IsNullOrWhiteSpace(_selectedVehicleId);

    public string SelectedVehicleText
    {
        get => _selectedVehicleText;
        private set => SetProperty(ref _selectedVehicleText, value);
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    public int SelectedUnitCount => TargetUnitIds.Count;
    public string SelectedUnitText => $"{SelectedUnitCount} units";

    public int GimbalCameraSupportedCount => SelectedCommandVehicleIds.Count(IsGimbalCameraSupported);

    public bool HasGimbalCameraSelection => GimbalCameraSupportedCount > 0;

    public string GimbalCameraSupportedSummary
        => HasMultiUnitSelection
            ? string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Current.Get("GimbalCameraAvailableCount"),
                GimbalCameraSupportedCount,
                SelectedUnitCount)
            : string.Empty;

    public string GimbalCameraSupportedTooltip
        => string.Join(Environment.NewLine,
            SelectedCommandVehicleIds
                .Where(IsGimbalCameraSupported)
                .Select(id => _vehicles.TryGet(id, out var vehicle) && vehicle is not null ? vehicle.Name : id));

    public string GimbalPitchText { get; set; } = "0";
    public string GimbalRollText { get; set; } = "0";
    public string GimbalYawText { get; set; } = "0";
    public string GimbalZoomText { get; set; } = "0";

    public string GimbalTelemetrySummary
    {
        get
        {
            var telemetry = _selectedVehicleId is null ? null : LatestTelemetry(_selectedVehicleId);
            if (telemetry is null)
                return "Telemetry: not reported";
            var pitch = FormatAngle(telemetry.GimbalPitchDegrees);
            var yaw = FormatAngle(telemetry.GimbalYawDegrees);
            var roll = FormatAngle(telemetry.GimbalRollDegrees);
            var zoom = telemetry.CameraZoomPercent is { } value ? $"{value:0.#}%" : "—";
            var recording = telemetry.CameraRecordingVideo == true ? " · Recording" :
                telemetry.CameraRecordingVideo == false ? " · Not recording" : string.Empty;
            return $"Pitch {pitch} · Yaw {yaw} · Roll {roll} · Zoom {zoom}{recording}";
        }
    }

    public string VideoToggleText
        => LatestTelemetry(_selectedVehicleId ?? string.Empty)?.CameraRecordingVideo == true
            ? "Queue stop video"
            : "Queue start video";

    private bool IsVideoRecording
        => LatestTelemetry(_selectedVehicleId ?? string.Empty)?.CameraRecordingVideo == true;

    private static string FormatAngle(double? value)
        => value is { } angle && double.IsFinite(angle) ? $"{angle:0.#}°" : "—";

    private bool IsGimbalCameraSupported(string vehicleId)
        => _vehicles.TryGet(vehicleId, out var vehicle) && vehicle is not null &&
           (vehicle.CapabilityKeys ?? []).Any(key =>
               key.Equals("gimbal", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("camera_", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("camera", StringComparison.OrdinalIgnoreCase));

    private double _takeoffAltitudeAglMetres = 5;
    public double TakeoffAltitudeAglMetres
    {
        get => _takeoffAltitudeAglMetres;
        set => SetProperty(ref _takeoffAltitudeAglMetres, value);
    }

    public double TakeoffAltitudeDisplay
    {
        get => Math.Round(
            TakeoffAltitudeAglMetres * (_unitSettings?.Current.VerticalDistance == DistanceUnit.Feet ? 3.280839895013123 : 1),
            2);
        set
        {
            TakeoffAltitudeAglMetres = UnitFormatting.ToMetres(value, CurrentVerticalDistanceUnit);
            _takeoffAltitudeText = FormatDisplayValue(value);
            OnPropertyChanged(nameof(TakeoffAltitudeText));
        }
    }

    public string TakeoffAltitudeText
    {
        get => _takeoffAltitudeText;
        set
        {
            if (!SetProperty(ref _takeoffAltitudeText, value))
                return;

            if (TryParseFinite(value, out var displayValue))
            {
                TakeoffAltitudeAglMetres = UnitFormatting.ToMetres(displayValue, CurrentVerticalDistanceUnit);
                OnPropertyChanged(nameof(TakeoffAltitudeDisplay));
            }

            UpdateParameterValidation();
        }
    }

    public string VerticalDistanceUnitSuffix => UnitFormatting.DistanceUnitLabel(CurrentVerticalDistanceUnit);
    public OperatorGoToTargetKind SelectedGoToTargetKind { get; set; } = OperatorGoToTargetKind.GlobalWgs84;
    private double? _goToLatitudeDegrees;
    public double? GoToLatitudeDegrees
    {
        get => _goToLatitudeDegrees;
        set => SetProperty(ref _goToLatitudeDegrees, value);
    }

    private double? _goToLongitudeDegrees;
    public double? GoToLongitudeDegrees
    {
        get => _goToLongitudeDegrees;
        set => SetProperty(ref _goToLongitudeDegrees, value);
    }

    public string GoToLatitudeText
    {
        get => _goToLatitudeText;
        set
        {
            if (!SetProperty(ref _goToLatitudeText, value))
                return;

            if (TryParseFinite(value, out var latitude))
                GoToLatitudeDegrees = latitude;

            UpdateParameterValidation();
        }
    }

    public string GoToLongitudeText
    {
        get => _goToLongitudeText;
        set
        {
            if (!SetProperty(ref _goToLongitudeText, value))
                return;

            if (TryParseFinite(value, out var longitude))
                GoToLongitudeDegrees = longitude;

            UpdateParameterValidation();
        }
    }

    public double? GoToAltitudeAmslMetres { get; set; }
    public double? GoToAcceptanceRadiusMetres { get; set; } = 2;
    public OperatorAltitudeTargetKind SelectedAltitudeTargetKind { get; set; } = OperatorAltitudeTargetKind.AltitudeAgl;
    private double? _altitudeTargetMetres = 20;
    public double? AltitudeTargetMetres
    {
        get => _altitudeTargetMetres;
        set => SetProperty(ref _altitudeTargetMetres, value);
    }

    public double? AltitudeTargetDisplay
    {
        get => AltitudeTargetMetres is double value
            ? Math.Round(value * (_unitSettings?.Current.VerticalDistance == DistanceUnit.Feet ? 3.280839895013123 : 1), 2)
            : null;
        set
        {
            AltitudeTargetMetres = value is double target ? UnitFormatting.ToMetres(target, CurrentVerticalDistanceUnit) : null;
            _altitudeTargetText = value is double displayValue ? FormatDisplayValue(displayValue) : "";
            OnPropertyChanged(nameof(AltitudeTargetText));
        }
    }

    public string AltitudeTargetText
    {
        get => _altitudeTargetText;
        set
        {
            if (!SetProperty(ref _altitudeTargetText, value))
                return;

            if (TryParseFinite(value, out var displayValue))
            {
                AltitudeTargetMetres = UnitFormatting.ToMetres(displayValue, CurrentVerticalDistanceUnit);
                OnPropertyChanged(nameof(AltitudeTargetDisplay));
            }

            UpdateParameterValidation();
        }
    }

    public OperatorHeadingTargetKind SelectedHeadingTargetKind { get; set; } = OperatorHeadingTargetKind.AbsoluteHeading;
    private double? _headingTargetDegrees;
    public double? HeadingTargetDegrees
    {
        get => _headingTargetDegrees;
        set => SetProperty(ref _headingTargetDegrees, value);
    }

    public string HeadingTargetText
    {
        get => _headingTargetText;
        set
        {
            if (!SetProperty(ref _headingTargetText, value))
                return;

            if (TryParseFinite(value, out var heading))
                HeadingTargetDegrees = heading;

            UpdateParameterValidation();
        }
    }

    public string ParameterValidationMessage
    {
        get => _parameterValidationMessage;
        private set
        {
            if (SetProperty(ref _parameterValidationMessage, value))
                OnPropertyChanged(nameof(HasParameterValidationMessage));
        }
    }

    public bool HasParameterValidationMessage => !string.IsNullOrWhiteSpace(ParameterValidationMessage);

    private void OnUnitSettingsChanged(object? sender, EventArgs e)
    {
        if (TryDispatchToUi(() => OnUnitSettingsChanged(sender, e)))
        {
            return;
        }

        _takeoffAltitudeText = FormatDisplayValue(TakeoffAltitudeDisplay);
        _altitudeTargetText = AltitudeTargetDisplay is double altitude ? FormatDisplayValue(altitude) : "";
        OnPropertyChanged(nameof(TakeoffAltitudeText));
        OnPropertyChanged(nameof(AltitudeTargetText));
        OnPropertyChanged(nameof(TakeoffAltitudeDisplay));
        OnPropertyChanged(nameof(AltitudeTargetDisplay));
        OnPropertyChanged(nameof(VerticalDistanceUnitSuffix));
        UpdateParameterValidation();
    }

    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (SetProperty(ref _confirmationText, value))
            {
                OnPropertyChanged(nameof(ConfirmationMatches));
                _executePendingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string RejectedCommandMessage
    {
        get => _rejectedCommandMessage;
        private set
        {
            if (!SetProperty(ref _rejectedCommandMessage, value))
                return;

            OnPropertyChanged(nameof(HasRejectedCommand));
            OnPropertyChanged(nameof(RejectedCommandSummary));
            OnPropertyChanged(nameof(ShowRejectedCommandSummary));
            OnPropertyChanged(nameof(ShowRejectedCommandDetails));
            (DismissRejectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleRejectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string CommandOutcomeTitle
    {
        get => _commandOutcomeTitle;
        private set => SetProperty(ref _commandOutcomeTitle, value);
    }

    public bool HasRejectedCommand => !string.IsNullOrWhiteSpace(RejectedCommandMessage);

    public bool IsRejectedCommandExpanded
    {
        get => _isRejectedCommandExpanded;
        private set
        {
            if (!SetProperty(ref _isRejectedCommandExpanded, value))
                return;
            OnPropertyChanged(nameof(ShowRejectedCommandSummary));
            OnPropertyChanged(nameof(ShowRejectedCommandDetails));
            OnPropertyChanged(nameof(RejectedCommandToggleText));
        }
    }

    public bool ShowRejectedCommandSummary => HasRejectedCommand && !IsRejectedCommandExpanded;

    public bool ShowRejectedCommandDetails => HasRejectedCommand && IsRejectedCommandExpanded;

    public string RejectedCommandSummary
    {
        get
        {
            const int maximumLength = 150;
            if (RejectedCommandMessage.Length <= maximumLength)
                return RejectedCommandMessage;
            return RejectedCommandMessage[..(maximumLength - 1)].TrimEnd() + "…";
        }
    }

    public string RejectedCommandToggleText => IsRejectedCommandExpanded ? "Hide details" : "Show details";

    public OperatorCommandQueueSnapshot? PendingPlan
    {
        get => _pendingPlan;
        private set
        {
            var previousBatchId = _pendingPlan?.BatchId;
            if (!SetProperty(ref _pendingPlan, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPendingPlan));
            if (!string.Equals(previousBatchId, value?.BatchId, StringComparison.Ordinal))
                ShowPendingFindings = false;
            OnPropertyChanged(nameof(PendingTitle));
            OnPropertyChanged(nameof(PendingDisplayTitle));
            OnPropertyChanged(nameof(PendingTarget));
            OnPropertyChanged(nameof(PendingAvailability));
            OnPropertyChanged(nameof(PendingSafety));
            OnPropertyChanged(nameof(ConfirmationPrompt));
            OnPropertyChanged(nameof(RequiresTypedConfirmation));
            OnPropertyChanged(nameof(ConfirmationMatches));
            OnPropertyChanged(nameof(PendingExecutionAvailable));
            OnPropertyChanged(nameof(PendingExecutionReady));
            OnPropertyChanged(nameof(PendingExecutionRequiresRefresh));
            OnPropertyChanged(nameof(PendingExecutionBlocked));
            RaiseCommandStates();
        }
    }

    public bool HasPendingPlan => PendingPlan is not null;

    public bool IsPreparingMapCommand
    {
        get => _isPreparingMapCommand;
        private set
        {
            if (SetProperty(ref _isPreparingMapCommand, value))
                RaiseCommandStates();
        }
    }

    public bool PendingExecutionAvailable => _pendingPlans.Count > 0 &&
        _pendingPlans.All(CanExecutePlan);

    public bool PendingExecutionReady => PendingExecutionAvailable &&
        _pendingPlans.All(plan => plan.ExpiresAt > DateTimeOffset.UtcNow);

    public bool PendingExecutionRequiresRefresh => PendingExecutionAvailable &&
        _pendingPlans.Any(plan => plan.ExpiresAt <= DateTimeOffset.UtcNow);

    public bool PendingExecutionBlocked => HasPendingPlan && !PendingExecutionAvailable;

    public bool ShowPendingFindings
    {
        get => _showPendingFindings;
        private set
        {
            if (SetProperty(ref _showPendingFindings, value))
            {
                (TogglePendingFindingsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string PendingDisplayTitle => PendingPlan is null
        ? "No command prepared"
        : _pendingCommandLabel is not null
            ? _pendingCommandLabel
        : _pendingPlans.Count > 1
            ? $"{PendingPlan.DisplayName} {_pendingPlans.Count} units"
            : PendingPlan.DisplayName + " - " + PendingPlan.TargetName;

    public string PendingTitle => PendingPlan is null
        ? "No command prepared"
        : $"{PendingPlan.DisplayName} · {PendingPlan.UnitName}";

    public string PendingTarget => PendingPlan is null
        ? "-"
        : _pendingPlans.Count > 1
            ? $"{_pendingPlans.Count} selected units"
            : PendingPlan.CommandAuthorityUnitId;

    public string PendingAvailability => PendingPlan?.Availability.ToString() ?? "None";

    public string PendingSafety => PendingPlan?.Safety ?? "None";

    // The operator has already explicitly initiated the command and reviewed the
    // Logos/local preflight. A normal Confirm button is sufficient; requiring the
    // operator to retype a generated phrase adds friction without adding safety.
    public static bool RequiresTypedConfirmation => false;

    public string ConfirmationPrompt => PendingPlan is null
        ? ""
        : _pendingPlans.All(IsAirborneDisarmConfirmationPlan)
            ? ""
        : _pendingPlans.Count > 1
            ? $"Review the readiness findings for {_pendingPlans.Count} units before executing."
            : "Review the readiness findings before executing.";

    public bool ConfirmationMatches
        => PendingPlan is not null;

    public string LatestCommandText
    {
        get
        {
            var latest = CommandHistory.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            return latest is null
                ? "No operational commands prepared."
                : $"{latest.Kind} · {latest.State} · {latest.Message}";
        }
    }

    private AsyncRelayCommand CreatePrepareCommand(OperatorCommandKind command)
        => new(token => QueueFromFormAsync(command, token), () => CanPrepareCommand(command));

    private Task QueueFromFormAsync(OperatorCommandKind command, CancellationToken cancellationToken)
    {
        if (TryGetFormCommandError(command, out var error))
        {
            ParameterValidationMessage = error;
            StatusMessage = error;
            return Task.CompletedTask;
        }

        return QueueAsync(command, ParametersFor(command), cancellationToken);
    }

    private Task ToggleVideoAsync(CancellationToken cancellationToken)
        => QueueAsync(
            IsVideoRecording ? OperatorCommandKind.StopVideo : OperatorCommandKind.StartVideo,
            OperatorCommandParameters.None,
            cancellationToken);

    private void ClearRejectedCommand()
    {
        IsRejectedCommandExpanded = false;
        CommandOutcomeTitle = "Command rejected";
        RejectedCommandMessage = string.Empty;
    }

    private void SetRejectedCommand(string? message)
    {
        CommandOutcomeTitle = "Command rejected";
        var detail = string.IsNullOrWhiteSpace(message)
            ? "The vehicle rejected the command without providing a reason."
            : message.Trim();
        // Preserve exact backend status text such as "PreArm: GPS not
        // healthy" so the expanded rejection card remains actionable. Older
        // generic workflow results may still be machine-coded; retain their
        // readable humanized summary for compatibility.
        RejectedCommandMessage = detail.Equals("Failed (0)", StringComparison.OrdinalIgnoreCase)
            ? "The vehicle rejected the command without providing a status reason."
            : IsVehicleStatusText(detail) || detail.StartsWith("ArduPilot rejected ", StringComparison.OrdinalIgnoreCase)
                ? detail
                : detail.Contains(':', StringComparison.Ordinal)
                    ? $"The vehicle rejected the command: {HumanizeCode(detail[(detail.IndexOf(':') + 1)..].Trim().Replace('_', ' '))}"
                    : $"The vehicle rejected the command: {detail}";
        IsRejectedCommandExpanded = false;
    }

    private void SetCommandOutcome(OperatorCommandResult result)
        => SetCommandOutcome(result.State, result.Message);

    private void SetCommandOutcome(OperatorCommandExecutionSnapshot result)
    {
        if (result.State == OperatorWorkflowState.TimedOut)
        {
            SetUnacknowledgedOutcome(result.Message);
            return;
        }

        if (result.State == OperatorWorkflowState.Failed)
        {
            SetFailedOutcome(result.Message);
            return;
        }

        SetRejectedCommand(result.Message);
    }

    private void SetCommandOutcome(OperationalCommandState state, string? message)
    {
        if (state == OperationalCommandState.TimedOut)
        {
            SetUnacknowledgedOutcome(message);
            return;
        }

        if (state == OperationalCommandState.Failed)
        {
            SetFailedOutcome(message);
            return;
        }

        SetRejectedCommand(message);
    }

    private void SetUnacknowledgedOutcome(string? message)
    {
        CommandOutcomeTitle = "No response";
        RejectedCommandMessage = string.IsNullOrWhiteSpace(message)
            ? "The vehicle did not acknowledge the command. Its outcome is unknown; check telemetry before retrying."
            : message.Trim();
        IsRejectedCommandExpanded = false;
    }

    private void SetFailedOutcome(string? message)
    {
        CommandOutcomeTitle = "Command failed";
        RejectedCommandMessage = string.IsNullOrWhiteSpace(message)
            ? "The command failed without a status reason."
            : message.Trim();
        IsRejectedCommandExpanded = false;
    }

    private static bool IsVehicleStatusText(string text)
        => text.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Arming denied:", StringComparison.OrdinalIgnoreCase) ||
           text.StartsWith("Preflight Fail:", StringComparison.OrdinalIgnoreCase);

    private bool TryGetFormCommandError(OperatorCommandKind command, out string error)
    {
        UpdateParameterValidation();
        if (HasParameterValidationMessage)
        {
            error = ParameterValidationMessage;
            return true;
        }

        error = command switch
        {
            OperatorCommandKind.Takeoff when string.IsNullOrWhiteSpace(TakeoffAltitudeText)
                => $"Enter a takeoff altitude in {VerticalDistanceUnitSuffix}.",
            OperatorCommandKind.ChangeAltitude when string.IsNullOrWhiteSpace(AltitudeTargetText)
                => $"Enter an altitude target in {VerticalDistanceUnitSuffix}.",
            OperatorCommandKind.SetHeading when string.IsNullOrWhiteSpace(HeadingTargetText)
                => "Enter a heading in degrees.",
            OperatorCommandKind.GoTo when string.IsNullOrWhiteSpace(GoToLatitudeText) || string.IsNullOrWhiteSpace(GoToLongitudeText)
                => "Enter both latitude and longitude before selecting Go to.",
            OperatorCommandKind.SetGimbal when ValidateGimbalForm(out var gimbalError) => gimbalError,
            _ => ""
        };
        return error.Length > 0;
    }

    private bool ValidateGimbalForm(out string error)
    {
        error = ValidateNumber(GimbalPitchText, -90, 90, "Enter a valid gimbal pitch.", "Gimbal pitch must be between -90° and 90°.")
            ?? ValidateNumber(GimbalRollText, -360, 360, "Enter a valid gimbal roll.", "Gimbal roll must be between -360° and 360°.")
            ?? ValidateNumber(GimbalYawText, -360, 360, "Enter a valid gimbal yaw.", "Gimbal yaw must be between -360° and 360°.")
            ?? ValidateNumber(GimbalZoomText, 0, 100, "Enter a valid camera zoom.", "Camera zoom must be between 0 and 100%.")
            ?? "";
        return error.Length > 0;
    }

    private DistanceUnit CurrentVerticalDistanceUnit
        => _unitSettings?.Current.VerticalDistance ?? DistanceUnit.Meters;

    private static string FormatDisplayValue(double value)
        => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool TryParseFinite(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
               double.IsFinite(value);
    }

    private void UpdateParameterValidation()
    {
        var unit = VerticalDistanceUnitSuffix;
        ParameterValidationMessage = ValidateNumber(TakeoffAltitudeText, 0.5, 500,
                                       "Enter a valid takeoff altitude.",
                                       $"Takeoff altitude must be between 0.5 and 500 {unit}.")
                                   ?? ValidateNumber(AltitudeTargetText, -500, 500,
                                       "Enter a valid altitude target.",
                                       $"Altitude target must be between -500 and 500 {unit}.")
                                   ?? ValidateNumber(HeadingTargetText, -360, 360,
                                       "Enter a valid heading.",
                                       "Heading must be between -360° and 360°.")
                                   ?? ValidateNumber(GoToLatitudeText, -90, 90,
                                       "Enter a valid latitude.",
                                       "Latitude must be between -90° and 90°.")
                                   ?? ValidateNumber(GoToLongitudeText, -180, 180,
                                       "Enter a valid longitude.",
                                       "Longitude must be between -180° and 180°.")
                                   ?? "";
    }

    private static string? ValidateNumber(
        string text,
        double minimum,
        double maximum,
        string invalidMessage,
        string rangeMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return !TryParseFinite(text, out var value)
            ? invalidMessage
            : value < minimum || value > maximum ? rangeMessage : null;
    }

    private static bool SupportsMultiUnitCommand(OperatorCommandKind command)
        => command is OperatorCommandKind.Arm or
            OperatorCommandKind.Disarm or
            OperatorCommandKind.Takeoff or
            OperatorCommandKind.Land or
            OperatorCommandKind.Hold or
            OperatorCommandKind.ChangeAltitude or
            OperatorCommandKind.SetHeading or
            OperatorCommandKind.CapturePhoto or
            OperatorCommandKind.StartVideo or
            OperatorCommandKind.StopVideo or
            OperatorCommandKind.CenterGimbal or
            OperatorCommandKind.NadirGimbal or
            OperatorCommandKind.SetGimbal;

    private static bool IsCameraCommand(OperatorCommandKind command)
        => command is OperatorCommandKind.CapturePhoto or OperatorCommandKind.StartVideo or
            OperatorCommandKind.StopVideo or OperatorCommandKind.CenterGimbal or
            OperatorCommandKind.NadirGimbal or OperatorCommandKind.SetGimbal;

    private bool CanPrepareCommand(OperatorCommandKind command)
        => HasCommandSelection && (!IsCameraCommand(command) || HasGimbalCameraSelection) &&
           (SelectedCommandTargetCount == 1 || SupportsMultiUnitCommand(command));

    private OperatorCommandParameters ParametersFor(OperatorCommandKind command)
        => command switch
        {
            OperatorCommandKind.Takeoff => new OperatorCommandParameters(TakeoffAltitudeAglMetres: TakeoffAltitudeAglMetres),
            OperatorCommandKind.GoTo => OperatorCommandParameters.GlobalGoTo(
                GoToLatitudeDegrees, GoToLongitudeDegrees, GoToAltitudeAmslMetres, GoToAcceptanceRadiusMetres),
            OperatorCommandKind.ChangeAltitude => SelectedAltitudeTargetKind switch
            {
                OperatorAltitudeTargetKind.AltitudeAmsl => OperatorCommandParameters.ChangeAltitudeAmsl(AltitudeTargetMetres),
                OperatorAltitudeTargetKind.RelativeDelta => OperatorCommandParameters.ChangeAltitudeRelative(AltitudeTargetMetres),
                _ => OperatorCommandParameters.ChangeAltitudeAgl(AltitudeTargetMetres)
            },
            OperatorCommandKind.SetHeading => SelectedHeadingTargetKind == OperatorHeadingTargetKind.RelativeYaw
                ? OperatorCommandParameters.RelativeYaw(HeadingTargetDegrees)
                : OperatorCommandParameters.AbsoluteHeading(HeadingTargetDegrees),
            OperatorCommandKind.SetGimbal => new OperatorCommandParameters(
                GimbalPitchDegrees: ParseOptional(GimbalPitchText),
                GimbalYawDegrees: ParseOptional(GimbalYawText),
                GimbalRollDegrees: ParseOptional(GimbalRollText),
                GimbalZoomPercent: ParseOptional(GimbalZoomText)),
            _ => OperatorCommandParameters.None
        };

    private static double? ParseOptional(string? text)
        => TryParseFinite(text, out var value) ? value : null;

    private async Task PrepareMapCommandAsync(OperatorCommandKind command, object? parameter)
    {
        if (parameter is not MapCommandTarget target || _selectedVehicleId is null)
        {
            return;
        }

        IsPreparingMapCommand = true;
        try
        {
            var vehicleIds = TargetUnitIds.Count > 0
                ? TargetUnitIds
                : [_selectedVehicleId];
            var parameters = vehicleIds.ToDictionary(
                id => id,
                id => command == OperatorCommandKind.GoTo
                    ? OperatorCommandParameters.GlobalGoTo(target.LatitudeDegrees, target.LongitudeDegrees,
                        LatestTelemetry(id)?.AltitudeMslMetres ?? 0, 2)
                    : OperatorCommandParameters.AbsoluteHeading(MapCommandMath.BearingDegrees(
                        LatestTelemetry(id)?.LatitudeDegrees, LatestTelemetry(id)?.LongitudeDegrees,
                        target.LatitudeDegrees, target.LongitudeDegrees)),
                StringComparer.Ordinal);
            await QueueAsync(command, parameters, null, CancellationToken.None);
        }
        finally
        {
            IsPreparingMapCommand = false;
        }
    }

    private async Task PrepareMapPointGimbalAsync(object? parameter)
    {
        if (parameter is not MapCommandTarget target || !HasGimbalCameraSelection)
            return;

        IsPreparingMapCommand = true;
        try
        {
            var targetIds = TargetUnitIds.Where(IsGimbalCameraSupported).ToArray();
            if (targetIds.Length == 0)
            {
                StatusMessage = "No selected unit reports a gimbal or camera.";
                return;
            }

            double? targetElevation = null;
            if (_terrain is not null)
            {
                try
                {
                    var result = await _terrain.GetElevationAsync(
                        target.LatitudeDegrees,
                        target.LongitudeDegrees,
                        new TerrainQueryOptions(AllowStale: true),
                        CancellationToken.None);
                    if (result.IsSuccess && result.Sample?.ElevationMetres is { } elevation && double.IsFinite(elevation))
                        targetElevation = elevation;
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Terrain unavailable; using flat-surface gimbal pointing. ({ex.Message})";
                }
            }

            var parameters = new Dictionary<string, OperatorCommandParameters>(StringComparer.Ordinal);
            foreach (var id in targetIds)
            {
                var telemetry = LatestTelemetry(id);
                if (telemetry?.LatitudeDegrees is not { } latitude || telemetry.LongitudeDegrees is not { } longitude ||
                    telemetry.AltitudeMslMetres is not { } altitude ||
                    !double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(altitude))
                    continue;

                var horizontalDistance = HorizontalDistanceMetres(latitude, longitude, target.LatitudeDegrees, target.LongitudeDegrees);
                var surfaceAltitude = targetElevation ?? altitude;
                var pitch = Math.Atan2(surfaceAltitude - altitude, Math.Max(horizontalDistance, 0.01)) * 180d / Math.PI;
                var hasHeading = telemetry.HeadingDegrees is { } heading && double.IsFinite(heading);
                parameters[id] = new OperatorCommandParameters(
                    GimbalPitchDegrees: Math.Clamp(pitch, -90, 90),
                    GimbalYawDegrees: hasHeading
                        ? MapCommandMath.RelativeBearingDegrees(
                            latitude,
                            longitude,
                            telemetry.HeadingDegrees,
                            target.LatitudeDegrees,
                            target.LongitudeDegrees)
                        : MapCommandMath.BearingDegrees(
                            latitude,
                            longitude,
                            target.LatitudeDegrees,
                            target.LongitudeDegrees),
                    GimbalEarthFrame: !hasHeading);
            }

            if (parameters.Count == 0)
            {
                StatusMessage = "Point gimbal here needs current position and altitude telemetry.";
                return;
            }

            await QueueAsync(OperatorCommandKind.SetGimbal, parameters, "Point gimbal at map location", CancellationToken.None);
            if (targetElevation is null)
                StatusMessage = "Gimbal commands queued using a flat-surface elevation fallback.";
        }
        finally
        {
            IsPreparingMapCommand = false;
        }
    }

    private static double HorizontalDistanceMetres(double originLatitude, double originLongitude, double targetLatitude, double targetLongitude)
    {
        const double earthRadiusMetres = 6_371_000;
        var dLatitude = (targetLatitude - originLatitude) * Math.PI / 180d;
        var dLongitude = (targetLongitude - originLongitude) * Math.PI / 180d;
        var meanLatitude = (originLatitude + targetLatitude) * Math.PI / 360d;
        return earthRadiusMetres * Math.Sqrt(Math.Pow(dLatitude, 2) + Math.Pow(dLongitude * Math.Cos(meanLatitude), 2));
    }

    private async Task PrepareMapAssemblyCommandAsync(object? parameter)
    {
        if (parameter is not MapAssemblyRequest request || !HasMultiUnitSelection)
            return;

        var provider = _formationProviders.FirstOrDefault(item =>
            string.Equals(item.Id, request.FormationId, StringComparison.OrdinalIgnoreCase));
        var units = TargetUnitIds
            .Select(id => _vehicles.TryGet(id, out var vehicle) ? vehicle : null)
            .Where(vehicle => vehicle is not null)
            .Cast<VehicleRecord>()
            .ToArray();
        if (provider is null || !provider.CanCalculate(units))
        {
            StatusMessage = "The selected formation is not available for the selected units.";
            return;
        }

        try
        {
            var calculation = provider.Calculate(units, request.Start, request.End);
            var parameters = calculation.Destinations.ToDictionary(
                destination => destination.VehicleId,
                destination => OperatorCommandParameters.GlobalGoTo(
                    destination.LatitudeDegrees,
                    destination.LongitudeDegrees,
                    LatestTelemetry(destination.VehicleId)?.AltitudeMslMetres ?? 0,
                    2),
                StringComparer.Ordinal);
            await QueueAsync(
                OperatorCommandKind.GoTo,
                parameters,
                 $"Assemble {TargetUnitIds.Count} units - {calculation.DisplayName}",
                 CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Formation preparation failed: {ex.Message}";
        }
    }

    private VehicleTelemetryRecord? LatestTelemetry(string vehicleId)
        => _telemetry.Items
            .Where(item => item.VehicleId == vehicleId)
            .OrderByDescending(item => item.ObservedAt)
            .FirstOrDefault();

    private async Task QueueAsync(
        OperatorCommandKind command,
        OperatorCommandParameters parameters,
        CancellationToken cancellationToken)
        => await QueueAsync(
            command,
            TargetIdsFor(command)
            .ToDictionary(id => id, _ => parameters, StringComparer.Ordinal),
             null,
             cancellationToken);

    private IReadOnlyList<string> TargetIdsFor(OperatorCommandKind command)
    {
        var ids = TargetUnitIds.Count > 0
            ? TargetUnitIds
            : _selectedVehicleId is null ? [] : [_selectedVehicleId];
        return IsCameraCommand(command) ? ids.Where(IsGimbalCameraSupported).ToArray() : ids;
    }

    private async Task QueueAsync(
        OperatorCommandKind command,
        IReadOnlyDictionary<string, OperatorCommandParameters> parametersByVehicle,
        string? pendingLabel,
        CancellationToken cancellationToken)
    {
        HideAirborneDisarmConfirmation();
        ClearRejectedCommand();
        try
        {
            _pendingAssembly = pendingLabel is not null;
            _pendingCommandLabel = pendingLabel;
            var batch = await _workflow.QueueAsync(new OperatorCommandQueueRequest(
                ToWorkflow(command),
                parametersByVehicle.Select(pair => new OperatorCommandQueueTarget(pair.Key, ToWorkflow(pair.Value))).ToArray(),
                Reason,
                pendingLabel,
                RollbackAcceptedGoToOnPartialFailure: pendingLabel is not null), cancellationToken);
            _pendingPlans = batch.Commands;
            PendingPlan = _pendingPlans.Count > 0 ? _pendingPlans[0] : null;
            ConfirmationText = "";
            ReplaceFindings(_pendingPlans.SelectMany(plan => plan.Findings));
            var blocked = HasOperatorFacingBlockingFinding(_pendingPlans);
            ShowPendingFindings = blocked;
            StatusMessage = blocked
                ? "Some selected units are not ready. Review the findings below."
                : _pendingPlans.Count > 1
                    ? $"Command queued for {_pendingPlans.Count} units."
                    : (_pendingPlans.Count > 0 ? (OperatorWorkflowAvailability?)_pendingPlans[0].Availability : null) switch
                    {
                        OperatorWorkflowAvailability.Warning => "Command queued with warnings.",
                        OperatorWorkflowAvailability.Unavailable => GatewayStatus.GatewayMessage,
                        _ => "Command queued."
                    };
            OnPropertyChanged(nameof(PendingTitle));
            OnPropertyChanged(nameof(PendingDisplayTitle));
            OnPropertyChanged(nameof(PendingTarget));
            OnPropertyChanged(nameof(ConfirmationPrompt));
            RaiseQueueState();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Command queueing failed: {ex.Message}";
        }
    }

    private async Task ExecutePendingAsync(CancellationToken cancellationToken)
    {
        if (PendingPlan is null)
        {
            return;
        }

        if (RequiresAirborneDisarmConfirmation(_pendingPlans))
        {
            ShowAirborneDisarmConfirmation(_pendingPlans);
            return;
        }

        ClearRejectedCommand();
        try
        {
            var results = await _workflow.ExecuteAsync(PendingPlan.BatchId, cancellationToken);
            var accepted = results.Count(result => result.Accepted);
            var rejected = results.FirstOrDefault(result => !result.Accepted);
            if (rejected is not null)
            {
                SetCommandOutcome(rejected);
            }
            StatusMessage = results.Count > 1
                ? $"{accepted} of {results.Count} unit commands accepted."
                : results.Count > 0 ? results[0].Message : "No queued commands were executed.";
            PendingPlan = null;
            _pendingPlans = [];
            _pendingAssembly = false;
            _pendingCommandLabel = null;
            ConfirmationText = "";
            Findings.Clear();
            RaiseQueueState();
        }
        catch (Exception ex)
        {
            SetRejectedCommand(ex.Message);
            StatusMessage = $"Command failed: {ex.Message}";
        }
    }

    private async Task CancelPendingAsync(CancellationToken cancellationToken)
    {
        HideAirborneDisarmConfirmation();
        if (PendingPlan is null)
        {
            return;
        }

        await _workflow.CancelAsync(PendingPlan.BatchId, "Cancelled before submission.", cancellationToken);
        PendingPlan = null;
        _pendingPlans = [];
        _pendingAssembly = false;
        _pendingCommandLabel = null;
        ConfirmationText = "";
        Findings.Clear();
        StatusMessage = "Prepared command cancelled.";
        RaiseQueueState();
    }

    private async Task ExecuteQueuedForSelectionAsync()
    {
        var queued = GetSelectedQueuedPlans();
        if (queued.Length == 0)
            return;

        if (RequiresAirborneDisarmConfirmation(queued))
        {
            ShowAirborneDisarmConfirmation(queued);
            return;
        }

        ClearRejectedCommand();
        foreach (var batchId in queued.Select(item => item.BatchId).Distinct(StringComparer.Ordinal))
        {
            var results = await _workflow.ExecuteAsync(batchId);
            var rejected = results.FirstOrDefault(result => !result.Accepted);
            if (rejected is not null)
                SetCommandOutcome(rejected);
        }
        StatusMessage = queued.Length > 1 ? $"Executed {queued.Length} queued unit commands." : "Queued command executed.";
        RefreshSelectedQueueProjection();
    }

    private void ShowAirborneDisarmConfirmation(IEnumerable<OperatorCommandQueueSnapshot> plans)
    {
        _airborneDisarmPlans = plans
            .Where(plan => plan.Command == OperatorWorkflowCommandKind.Disarm)
            .ToArray();
        IsAirborneDisarmConfirmationVisible = _airborneDisarmPlans.Length > 0;
    }

    private void HideAirborneDisarmConfirmation()
    {
        _airborneDisarmPlans = [];
        IsAirborneDisarmConfirmationVisible = false;
    }

    private async Task ConfirmAirborneDisarmAsync(CancellationToken cancellationToken)
    {
        var plans = _airborneDisarmPlans;
        if (plans.Length == 0)
        {
            HideAirborneDisarmConfirmation();
            return;
        }

        HideAirborneDisarmConfirmation();
        foreach (var batchId in plans.Select(plan => plan.BatchId).Distinct(StringComparer.Ordinal))
        {
            await _workflow.CancelAsync(batchId, "Replaced by confirmed airborne disarm.", cancellationToken);
        }

        var parameters = plans.ToDictionary(
            plan => plan.UnitId,
            plan => FromWorkflow(plan.Parameters) with { AirborneDisarmConfirmed = true },
            StringComparer.Ordinal);
        var label = plans.Length > 1 ? $"Disarm {plans.Length} units" : null;
        await QueueAsync(OperatorCommandKind.Disarm, parameters, label, cancellationToken);
        await ExecutePendingAsync(cancellationToken);
    }

    private static bool RequiresAirborneDisarmConfirmation(IEnumerable<OperatorCommandQueueSnapshot> plans)
    {
        var candidatePlans = plans.ToArray();
        return candidatePlans.Length > 0 && candidatePlans.All(IsAirborneDisarmConfirmationPlan);
    }

    private static bool IsAirborneDisarmConfirmationPlan(OperatorCommandQueueSnapshot plan)
        => plan.Command == OperatorWorkflowCommandKind.Disarm &&
           plan.Findings.Any(finding => finding.Code == "AIRBORNE_DISARM_CONFIRMATION_REQUIRED") &&
           plan.Findings.All(finding =>
               finding.Severity != OperatorWorkflowSeverity.Blocking ||
               finding.Code == "AIRBORNE_DISARM_CONFIRMATION_REQUIRED") &&
           !plan.Parameters.AirborneDisarmConfirmed;

    private async Task ClearQueuedForSelectionAsync()
    {
        HideAirborneDisarmConfirmation();
        foreach (var plan in GetSelectedQueuedPlans())
            await _workflow.CancelAsync(plan.QueueId, "Queued command cleared by operator.");

        if (PendingPlan is not null && !_workflow.TryGet(PendingPlan.QueueId, out _))
        {
            PendingPlan = null;
            _pendingPlans = [];
            _pendingCommandLabel = null;
            Findings.Clear();
        }
        StatusMessage = "Queued commands cleared.";
        RaiseQueueState();
    }

    private bool CanExecutePending()
        => GetSelectedQueuedPlans().Length > 0 && GetSelectedQueuedPlans().All(CanExecutePlan);

    private bool CanExecuteQueuedForSelection()
        => GetSelectedQueuedPlans().Length > 0 && GetSelectedQueuedPlans().All(CanExecutePlan);

    private OperatorCommandQueueSnapshot[] GetSelectedQueuedPlans()
        => _workflow.QueuedCommands.Where(plan => SelectedCommandVehicleIds.Contains(plan.UnitId, StringComparer.Ordinal)).ToArray();

    private static string QueueAvailability(IReadOnlyList<OperatorCommandQueueSnapshot> plans)
        => plans.Any(item => item.Availability is OperatorWorkflowAvailability.Blocked or OperatorWorkflowAvailability.Unavailable)
            ? "unavailable"
            : plans.Any(item => item.Availability == OperatorWorkflowAvailability.Warning)
                ? "warning"
                : "ready";

    private static bool CanExecutePlan(OperatorCommandQueueSnapshot plan)
        => plan.Availability is OperatorWorkflowAvailability.Ready or OperatorWorkflowAvailability.Warning ||
           IsAirborneDisarmConfirmationPlan(plan);

    private OperatorCommandQueueSnapshot[] GetSelectedExecutingPlans()
        => _workflow.ActiveCommands.Where(plan => SelectedCommandVehicleIds.Contains(plan.UnitId, StringComparer.Ordinal)).ToArray();

    private bool CanCancelExecuting() => GetSelectedExecutingPlans().Length > 0;

    private async Task CancelExecutingAsync(CancellationToken cancellationToken)
    {
        var plans = GetSelectedExecutingPlans();
        if (plans.Length == 0)
            return;
        var results = await _workflow.CancelActiveAsync(plans.Select(item => item.UnitId).ToArray(), cancellationToken);
        if (results.Count == 1)
        {
            StatusMessage = results[0].Message;
        }
        else
        {
            var cancelled = results.Count(result => result.Accepted);
            StatusMessage = cancelled == results.Count
                ? $"Active commands cancelled for {cancelled} unit(s)."
                : $"Active command cancellation completed for {cancelled} of {results.Count} unit(s).";
        }
        RaiseQueueState();
    }

    private void RefreshSelectedQueueProjection()
    {
        _pendingPlans = GetSelectedQueuedPlans();
        PendingPlan = _pendingPlans.Count > 0 ? _pendingPlans[0] : null;
        ReplaceFindings(_pendingPlans.SelectMany(plan => plan.Findings));
        if (HasOperatorFacingBlockingFinding(_pendingPlans))
        {
            ShowPendingFindings = true;
        }
        OnPropertyChanged(nameof(HasPendingPlan));
        OnPropertyChanged(nameof(PendingDisplayTitle));
        OnPropertyChanged(nameof(PendingTarget));
        OnPropertyChanged(nameof(PendingAvailability));
        OnPropertyChanged(nameof(SelectedQueuedAvailability));
        OnPropertyChanged(nameof(PendingSafety));
        OnPropertyChanged(nameof(ConfirmationPrompt));
    }


    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (TryDispatchToUi(ApplySelection))
        {
            return;
        }

        ApplySelection();
    }

    private void OnDataChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (TryDispatchToUi(ApplySelection))
        {
            return;
        }

        ApplySelection();
    }

    private void OnCommandHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (TryDispatchToUi(() => OnCommandHistoryChanged(sender, e)))
        {
            return;
        }

        OnPropertyChanged(nameof(LatestCommandText));
        RaiseQueueState();
    }

    private void OnWorkflowChanged(object? sender, EventArgs e)
    {
        if (TryDispatchToUi(HandleWorkflowChanged))
        {
            return;
        }
        HandleWorkflowChanged();
    }

    private bool TryDispatchToUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            return false;
        }

        _ = _dispatcher.InvokeAsync(action);
        return true;
    }

    private void HandleWorkflowChanged()
    {
        RefreshSelectedQueueProjection();
        ActiveGoToTargetChanged?.Invoke(this, EventArgs.Empty);
        RaiseQueueState();
    }

    private void ApplySelection()
    {
        // The selected-unit list is authoritative. Current is retained for
        // compatibility with older selection surfaces, but can briefly lag
        // while a unit row is being refreshed or reordered.
        var newVehicleId = TargetUnitIds.Count > 0 ? TargetUnitIds[0] : null;
        if (newVehicleId is null && _selection.Current.Kind == SelectionKind.Vehicle)
            newVehicleId = _selection.Current.Id;
        _selectedVehicleId = newVehicleId;
        var targetScope = _targetScope?.Current;
        if (targetScope is { Kind: OperatorTargetScopeKind.Team })
        {
            _selectedVehicleId = targetScope.UnitIds.Count > 0 ? targetScope.UnitIds[0] : null;
            SelectedVehicleText = $"{targetScope.TargetName ?? "Team"} · {targetScope.TargetCount} units";
        }
        else if (_selectedVehicleId is not null &&
            _vehicles.TryGet(_selectedVehicleId, out var vehicle) &&
            vehicle is not null)
        {
            SelectedVehicleText = $"{vehicle.Name} · {vehicle.Domain} · {vehicle.State}";
        }
        else
        {
            _selectedVehicleId = null;
            SelectedVehicleText = "No vehicle selected";
        }

        OnPropertyChanged(nameof(HasVehicleSelection));
        OnPropertyChanged(nameof(HasMultiUnitSelection));
        OnPropertyChanged(nameof(SelectedUnitCount));
        OnPropertyChanged(nameof(SelectedUnitText));
        OnPropertyChanged(nameof(GimbalCameraSupportedCount));
        OnPropertyChanged(nameof(HasGimbalCameraSelection));
        OnPropertyChanged(nameof(GimbalCameraSupportedSummary));
        OnPropertyChanged(nameof(GimbalCameraSupportedTooltip));
        OnPropertyChanged(nameof(GimbalTelemetrySummary));
        OnPropertyChanged(nameof(VideoToggleText));
        OnPropertyChanged(nameof(SelectedQueuedAction));
        OnPropertyChanged(nameof(SelectedQueuedAvailability));
        RefreshSelectedQueueProjection();
        RaiseCommandStates();
        RaiseQueueState();
    }

    private void Subscribe(System.Collections.IEnumerable collection)
        => ((INotifyCollectionChanged)collection).CollectionChanged += OnDataChanged;

    private void ReplaceFindings(IEnumerable<OperatorWorkflowFinding> findings)
    {
        Findings.Clear();
        foreach (var finding in findings.Where(item =>
                     item.Severity != OperatorWorkflowSeverity.Info &&
                     item.Code != "AIRBORNE_DISARM_CONFIRMATION_REQUIRED"))
        {
            Findings.Add(finding with { Message = HumanizeFinding(finding) });
        }
    }

    private static bool HasOperatorFacingBlockingFinding(IEnumerable<OperatorCommandQueueSnapshot> plans)
        => plans.SelectMany(plan => plan.Findings).Any(finding =>
            finding.Severity == OperatorWorkflowSeverity.Blocking &&
            finding.Code != "AIRBORNE_DISARM_CONFIRMATION_REQUIRED");

    private static string HumanizeFinding(OperatorWorkflowFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(finding.Message) && finding.Message.Any(char.IsWhiteSpace))
        {
            return finding.Message;
        }

        return finding.Code switch
        {
            "VEHICLE_NOT_READY" => "The vehicle is not ready for this operation.",
            "TELEMETRY_STALE" or "MAVLINK_TELEMETRY_STALE" => "Vehicle telemetry is too old to safely send this command.",
            "TELEMETRY_MISSING" => "Current vehicle telemetry is not available.",
            "VEHICLE_DIAGNOSTICS_INCOMPLETE" => "Vehicle health information is incomplete.",
            "VEHICLE_DIAGNOSTICS_NOT_CURRENT" => "Vehicle health information is out of date.",
            "CAPABILITY_NOT_ADVERTISED" => "This vehicle does not report support for this operation.",
            "OPERATOR_API_UNAVAILABLE" or "VEHICLE_OPERATIONS_UNAVAILABLE" => "The vehicle command service is unavailable.",
            "MANUAL_CONTROL_ACTIVE" => "Manual control currently owns this vehicle.",
            "VEHICLE_MISSING" => "The selected vehicle is no longer available.",
            "POLICY_DENIED" or "POLICY_PREFLIGHT_FAILED" => "The operation was denied by vehicle policy.",
            "MAVLINK_CONNECTION_UNAVAILABLE" => "The MAVLink connection is unavailable.",
            _ => HumanizeCode(finding.Code)
        };
    }

    private static string HumanizeCode(string code)
    {
        var words = code.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return "This command is not available.";
        }

        var text = string.Join(' ', words).ToLowerInvariant();
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private void RaiseQueueState()
    {
        OnPropertyChanged(nameof(QueuedPlans));
        OnPropertyChanged(nameof(HasQueuedCommandsForSelection));
        OnPropertyChanged(nameof(SelectedQueuedAction));
        OnPropertyChanged(nameof(HasExecutingCommand));
        OnPropertyChanged(nameof(HasPendingPlan));
        OnPropertyChanged(nameof(PendingExecutionAvailable));
        OnPropertyChanged(nameof(PendingExecutionReady));
        OnPropertyChanged(nameof(PendingExecutionRequiresRefresh));
        OnPropertyChanged(nameof(PendingExecutionBlocked));
        OnPropertyChanged(nameof(ShowPendingFindings));
        OnPropertyChanged(nameof(ShowClearQueuedCommand));
        OnPropertyChanged(nameof(ShowClearQueuedForSelection));
        QueuedCommandsChanged?.Invoke(this, EventArgs.Empty);
        (ExecuteQueuedCommandsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ClearQueuedCommandsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        _cancelExecutingCommandsCommand.RaiseCanExecuteChanged();
        (TogglePendingFindingsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        _executePendingCommand.RaiseCanExecuteChanged();
    }

    private static OperatorWorkflowCommandKind ToWorkflow(OperatorCommandKind command) => command switch
    {
        OperatorCommandKind.Recover => OperatorWorkflowCommandKind.ReturnHome,
        _ => Enum.Parse<OperatorWorkflowCommandKind>(command.ToString(), true)
    };

    private static OperatorWorkflowParameters ToWorkflow(OperatorCommandParameters parameters) => new(
        parameters.TakeoffAltitudeAglMetres,
        parameters.GoToTargetKind is null ? null : (OperatorWorkflowGoToTargetKind)parameters.GoToTargetKind.Value,
        parameters.GoToLatitudeDegrees, parameters.GoToLongitudeDegrees, parameters.GoToAltitudeAmslMetres,
        parameters.GoToNorthMetres, parameters.GoToEastMetres, parameters.GoToDownMetres, parameters.GoToYawDegrees,
        parameters.GoToAcceptanceRadiusMetres,
        parameters.AltitudeTargetKind is null ? null : (OperatorWorkflowAltitudeTargetKind)parameters.AltitudeTargetKind.Value,
        parameters.AltitudeAmslMetres, parameters.AltitudeAglMetres, parameters.AltitudeRelativeDeltaMetres,
        parameters.HeadingTargetKind is null ? null : (OperatorWorkflowHeadingTargetKind)parameters.HeadingTargetKind.Value,
        parameters.HeadingDegrees, parameters.RelativeYawDegrees, parameters.AirborneDisarmConfirmed,
        parameters.GimbalPitchDegrees, parameters.GimbalYawDegrees, parameters.GimbalRollDegrees,
        parameters.GimbalZoomPercent, parameters.GimbalEarthFrame);

    private static OperatorCommandParameters FromWorkflow(OperatorWorkflowParameters parameters) => new(
        parameters.TakeoffAltitudeAglMetres,
        parameters.GoToTargetKind is null ? null : (OperatorGoToTargetKind)parameters.GoToTargetKind.Value,
        parameters.GoToLatitudeDegrees, parameters.GoToLongitudeDegrees, parameters.GoToAltitudeAmslMetres,
        parameters.GoToNorthMetres, parameters.GoToEastMetres, parameters.GoToDownMetres, parameters.GoToYawDegrees,
        parameters.GoToAcceptanceRadiusMetres,
        parameters.AltitudeTargetKind is null ? null : (OperatorAltitudeTargetKind)parameters.AltitudeTargetKind.Value,
        parameters.AltitudeAmslMetres, parameters.AltitudeAglMetres, parameters.AltitudeRelativeDeltaMetres,
        parameters.HeadingTargetKind is null ? null : (OperatorHeadingTargetKind)parameters.HeadingTargetKind.Value,
        parameters.HeadingDegrees, parameters.RelativeYawDegrees, parameters.AirborneDisarmConfirmed,
        parameters.GimbalPitchDegrees, parameters.GimbalYawDegrees, parameters.GimbalRollDegrees,
        parameters.GimbalZoomPercent, parameters.GimbalEarthFrame);

    private void RaiseCommandStates()
    {
        if (TryDispatchToUi(RaiseCommandStates))
        {
            return;
        }

        foreach (var command in new[]
        {
            PrepareArmCommand,
            PrepareDisarmCommand,
            PrepareHoldCommand,
            PrepareTakeoffCommand,
            PrepareGoToCommand,
            PrepareLandCommand,
            PrepareRecoverCommand,
            PrepareChangeAltitudeCommand,
            PrepareSetHeadingCommand,
            PrepareCapturePhotoCommand,
            PrepareStartVideoCommand,
            PrepareStopVideoCommand,
            PrepareCenterGimbalCommand,
            PrepareNadirGimbalCommand,
            PrepareSetGimbalCommand,
            ToggleVideoCommand,
            PrepareMapGoToCommand,
            PrepareMapSetHeadingCommand,
            PrepareMapAssembleCommand,
            ExecutePendingCommand,
            CancelPendingCommand,
            ExecuteQueuedCommandsCommand,
            ClearQueuedCommandsCommand,
            CancelExecutingCommandsCommand
        }.OfType<AsyncRelayCommand>())
        {
            command.RaiseCanExecuteChanged();
        }
        (PrepareMapAssembleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PrepareMapPointGimbalCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
