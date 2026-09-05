using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services.Location;

namespace RobotCommand.ViewModels;

/// <summary>Presentation adapter for the local, backend-neutral flight-mission workflow.</summary>
public sealed class FlightMissionViewModel : ObservableObject, IDisposable
{
    private readonly IFlightMissionWorkflow _workflow;
    private readonly IGeometryWorkflow _geometry;
    private readonly IUnitObservationWorkflow _units;
    private readonly IFenceWorkflow _fences;
    private readonly IReviewedOperationWorkflow _reviewed;
    private readonly IOperatorLocationService _operatorLocation;
    private readonly IUiDispatcher _dispatcher;
    private readonly LocalizationService _localization = LocalizationService.Current;
    private FlightMissionSnapshot? _selectedMission;
    private FlightMissionStep? _selectedStep;
    private GeometryWorkflowSnapshot? _selectedStepGeometry;
    private UnitObservationSnapshot? _selectedTarget;
    private FenceSnapshot? _selectedFence;
    private ReviewedOperationSnapshot? _pendingOperation;
    private string _newName = LocalizationService.Current.Get("FlightMissionDefaultName");
    private double _altitude = 20;
    private FlightMissionEndAction _endAction = FlightMissionEndAction.Hold;
    private double _cruiseSpeed = 5;
    private double _loiterSeconds = 30;
    private double _surveySpacing = 25;
    private double _surveyBearing;
    private double _surveyTurnaround;
    private bool _surveyReverseEntry;
    private double _corridorWidth = 50;
    private double _corridorSpacing = 25;
    private double _corridorTurnaround;
    private double _corridorFrontLap = 70;
    private double _corridorSideLap = 70;
    private bool _corridorReverseDirection;
    private FlightMissionCorridorEntrySide _corridorEntrySide;
    private bool _corridorImagesInTurnarounds;
    private bool _automaticPhotoCaptureEnabled;
    private double? _cameraDistance;
    private double? _cameraInterval;
    private FlightMissionCameraActionKind _cameraActionKind = FlightMissionCameraActionKind.PhotoOnce;
    private double _cameraActionInterval = 5;
    private double _cameraActionDistance = 10;
    private FlightMissionCameraMode _cameraActionMode = FlightMissionCameraMode.Photo;
    private string _cameraActionCameraName = string.Empty;
    private double? _cameraActionRoiLatitude;
    private double? _cameraActionRoiLongitude;
    private double? _cameraActionGimbalPitch;
    private double? _cameraActionGimbalYaw;
    private double? _cameraActionGimbalRoll;
    private FlightMissionGimbalFrame _cameraActionGimbalFrame = FlightMissionGimbalFrame.Vehicle;
    private bool _terrainFollowing;
    private string _status = string.Empty;
    private string _missionStartStatus = string.Empty;
    private string _missionOperationStatus = string.Empty;
    private bool _missionStartInProgress;
    private bool _missionOperationInProgress;
    private bool _isCreatingMission;
    private bool _isEditingMissionName;
    private bool _deleteConfirmationPending;
    private bool _deleteAssociatedGeometry;
    private string _deleteConflictMessage = string.Empty;
    private bool _refreshing;
    private bool _refreshQueued;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _stepOptionsCancellation;
    private string? _stepOptionsMissionId;
    private string? _stepOptionsStepId;
    private bool _pendingAutomaticPhotoCaptureEnabled;
    private double? _pendingCameraDistance;
    private double? _pendingCameraInterval;
    private CancellationTokenSource? _stepGeometryCancellation;
    private bool _loadingStepOptions;
    private bool _loadingStepGeometry;

    public FlightMissionViewModel(IFlightMissionWorkflow workflow, IGeometryWorkflow geometry, IUnitObservationWorkflow units, IFenceWorkflow fences,
        IReviewedOperationWorkflow reviewed, IOperatorLocationService operatorLocation, IUiDispatcher dispatcher)
    {
        _workflow = workflow; _geometry = geometry; _units = units; _fences = fences; _reviewed = reviewed; _operatorLocation = operatorLocation; _dispatcher = dispatcher;
        Missions = []; Geometry = []; Targets = []; Fences = [];
        CreateCommand = new AsyncRelayCommand(CreateAsync);
        NewMissionCommand = new AsyncRelayCommand(BeginOrCreateMissionAsync, () => !IsCreatingMission || !string.IsNullOrWhiteSpace(NewName));
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => SelectedMission is not null && (!IsEditingMissionName || !string.IsNullOrWhiteSpace(NewName)));
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => SelectedMission is not null);
        DeleteCommand = new AsyncRelayCommand(BeginDeleteAsync, () => SelectedMission is not null && !DeleteConfirmationPending);
        ConfirmDeleteCommand = new AsyncRelayCommand(ConfirmDeleteAsync, () => SelectedMission is not null && DeleteConfirmationPending && !HasDeleteConflict);
        DeleteMissionOnlyCommand = new AsyncRelayCommand(DeleteMissionOnlyAsync, () => SelectedMission is not null && HasDeleteConflict);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, HasMissionSelected);
        AddTakeoffCommand = new AsyncRelayCommand(token => AddAsync(() => _workflow.AddTakeoffAsync(Require().Id, token)), HasMissionSelected);
        AddRtlCommand = new AsyncRelayCommand(token => AddAsync(() => _workflow.AddReturnToLaunchAsync(Require().Id, token)), HasMissionSelected);
        AddLandCommand = new AsyncRelayCommand(token => AddAsync(() => _workflow.AddLandAsync(Require().Id, token)), HasMissionSelected);
        AddSurveyCommand = new AsyncRelayCommand(AddSurveyAsync, HasMissionSelected);
        AddCorridorCommand = new AsyncRelayCommand(AddCorridorAsync, HasMissionSelected);
        AddLoiterCommand = new AsyncRelayCommand(AddLoiterAsync, () => HasMissionSelected() && LoiterSeconds > 0);
        AddCameraIntentCommand = new AsyncRelayCommand(token => AddAsync(() => _workflow.AddCameraIntentAsync(
            Require().Id,
            BuildAutomaticPhotoCaptureIntent(),
            token)), HasMissionSelected);
        AddMissionStartCameraActionCommand = new AsyncRelayCommand(AddMissionStartCameraActionAsync, HasMissionSelected);
        AddSelectedStepCameraActionCommand = new AsyncRelayCommand(AddSelectedStepCameraActionAsync, () => HasMissionSelected() && SelectedStep is not null);
        RemoveMissionStartCameraActionCommand = new RelayCommand(parameter => _ = RemoveMissionStartCameraActionAsync(parameter), parameter => HasMissionSelected() && parameter is FlightMissionCameraAction);
        RemoveSelectedStepCameraActionCommand = new RelayCommand(parameter => _ = RemoveSelectedStepCameraActionAsync(parameter), parameter => HasMissionSelected() && SelectedStep is not null && parameter is FlightMissionCameraAction);
        SetAltitudeCommand = new AsyncRelayCommand(SetAltitudeAsync, HasMissionSelected);
        SetSpeedCommand = new AsyncRelayCommand(SetSpeedAsync, () => HasMissionSelected() && CruiseSpeed > 0);
        SetStepOverridesCommand = new AsyncRelayCommand(SetStepOverridesAsync, () => SelectedMission is not null && SelectedStep is not null);
        SetTakeoffAltitudeCommand = new AsyncRelayCommand(SetTakeoffAltitudeAsync, () => SelectedStep?.Kind == FlightMissionStepKind.Takeoff);
        SetFenceCommand = new AsyncRelayCommand(SetFenceAsync, HasMissionSelected);
        RemoveStepCommand = new AsyncRelayCommand(RemoveStepAsync, () => SelectedMission?.Steps.Count > 0);
        MoveStepUpCommand = new AsyncRelayCommand(token => MoveStepAsync(-1, token), () => SelectedMission is not null && SelectedStep is not null && StepIndex(SelectedMission, SelectedStep) > 0);
        MoveStepDownCommand = new AsyncRelayCommand(token => MoveStepAsync(1, token), () => SelectedMission is not null && SelectedStep is not null && StepIndex(SelectedMission, SelectedStep) < SelectedMission.Steps.Count - 1);
        // Upload must always provide feedback. Preconditions are evaluated in
        // the handler so an unavailable target/mission is explained instead of
        // presenting an inert command.
        UploadCommand = new AsyncRelayCommand(PrepareUploadAsync);
        DownloadCommand = new AsyncRelayCommand(token => PlanAsync("download", token), HasTarget);
        // Start must always explain its outcome. AsyncRelayCommand still
        // disables duplicate clicks while the handler is executing.
        StartCommand = new AsyncRelayCommand(StartMissionAsync);
        PauseCommand = new AsyncRelayCommand(token => PlanAsync("pause", token), CanOperate);
        ContinueCommand = new AsyncRelayCommand(token => PlanAsync("continue", token), CanOperate);
        ResumeCommand = new AsyncRelayCommand(token => PlanAsync("resume", token), CanOperate);
        RetainCommand = new AsyncRelayCommand(token => PlanAsync("retain", token), CanOperate);
        RemoveOnboardCommand = new AsyncRelayCommand(token => PlanAsync("remove", token), CanOperate);
        SetEndActionCommand = new AsyncRelayCommand(SetEndActionAsync, HasMissionSelected);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => PendingOperation?.CanExecute == true);
        CancelOperationCommand = new AsyncRelayCommand(CancelOperationAsync, () => HasPendingOperation);
        _workflow.Changed += OnChanged; _geometry.Changed += OnChanged; _units.Changed += OnChanged; _fences.Changed += OnChanged; _reviewed.Changed += OnChanged; _operatorLocation.Changed += OnChanged; _localization.PropertyChanged += OnLocalizationChanged; Refresh();
    }

    public ObservableCollection<FlightMissionSnapshot> Missions { get; }
    public ObservableCollection<GeometryWorkflowSnapshot> Geometry { get; }
    public ObservableCollection<UnitObservationSnapshot> Targets { get; }
    public ObservableCollection<FenceSnapshot> Fences { get; }
    public bool HasMission => SelectedMission is not null;
    public bool DeleteConfirmationPending
    {
        get => _deleteConfirmationPending;
        private set
        {
            if (!SetProperty(ref _deleteConfirmationPending, value)) return;
            OnPropertyChanged(nameof(IsDeleteButtonVisible));
            OnPropertyChanged(nameof(HasDeleteConfirmation));
            RaiseCommands();
        }
    }
    public bool IsDeleteButtonVisible => HasMission && !DeleteConfirmationPending;
    public bool HasDeleteConfirmation => DeleteConfirmationPending;
    public bool DeleteAssociatedGeometry
    {
        get => _deleteAssociatedGeometry;
        set
        {
            if (!SetProperty(ref _deleteAssociatedGeometry, value)) return;
            if (!value) DeleteConflictMessage = string.Empty;
            RaiseCommands();
        }
    }
    public string DeleteConflictMessage
    {
        get => _deleteConflictMessage;
        private set
        {
            if (SetProperty(ref _deleteConflictMessage, value))
            {
                OnPropertyChanged(nameof(HasDeleteConflict));
                RaiseCommands();
            }
        }
    }
    public bool HasDeleteConflict => !string.IsNullOrWhiteSpace(DeleteConflictMessage);
    public ICommand CreateCommand { get; }
    public ICommand NewMissionCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand DeleteMissionOnlyCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand AddTakeoffCommand { get; }
    public ICommand AddRtlCommand { get; }
    public ICommand AddLandCommand { get; }
    public ICommand AddSurveyCommand { get; }
    public ICommand AddCorridorCommand { get; }
    public ICommand AddLoiterCommand { get; }
    public ICommand AddCameraIntentCommand { get; }
    public ICommand AddMissionStartCameraActionCommand { get; }
    public ICommand AddSelectedStepCameraActionCommand { get; }
    public ICommand RemoveMissionStartCameraActionCommand { get; }
    public ICommand RemoveSelectedStepCameraActionCommand { get; }
    public ICommand SetAltitudeCommand { get; }
    public ICommand SetSpeedCommand { get; }
    public ICommand SetStepOverridesCommand { get; }
    public ICommand SetTakeoffAltitudeCommand { get; }
    public ICommand SetFenceCommand { get; }
    public ICommand RemoveStepCommand { get; }
    public ICommand MoveStepUpCommand { get; }
    public ICommand MoveStepDownCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand RetainCommand { get; }
    public ICommand RemoveOnboardCommand { get; }
    public ICommand SetEndActionCommand { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand CancelOperationCommand { get; }
    public IReadOnlyList<FlightMissionEndAction> EndActions { get; } = [FlightMissionEndAction.Hold, FlightMissionEndAction.ReturnToLaunch];
    public IReadOnlyList<string> EndActionOptions =>
    [
        _localization.Get("FlightMissionEndHold"),
        _localization.Get("FlightMissionEndRtl")
    ];
    public FlightMissionSnapshot? SelectedMission
    {
        get => _selectedMission;
        set
        {
            if (!SetProperty(ref _selectedMission, value)) return;
            IsCreatingMission = false;
            IsEditingMissionName = false;
            DeleteConfirmationPending = false;
            DeleteAssociatedGeometry = false;
            DeleteConflictMessage = string.Empty;
            CancelStepOptionsUpdate();
            CancelStepGeometryUpdate();
            _workflow.SetMapPreviewMission(value?.Id);
            NewName = value?.Name ?? LocalizationService.Current.Get("FlightMissionDefaultName");
            Altitude = value?.RelativeAltitudeMetres ?? 20;
            CruiseSpeed = value?.CruiseSpeedMetresPerSecond ?? 5;
            EndAction = value?.EndAction ?? FlightMissionEndAction.Hold;
            RequestPreview();
            OnPropertyChanged(nameof(MissionStartCameraActions));
            OnPropertyChanged(nameof(HasMissionStartCameraActions));
            OnPropertyChanged(nameof(HasNoMissionStartCameraActions));
            OnPropertyChanged(nameof(SelectedStepCameraActions));
            OnPropertyChanged(nameof(HasSelectedStepCameraActions));
            OnPropertyChanged(nameof(HasNoSelectedStepCameraActions));
            OnPropertyChanged(nameof(CameraPlanSummary));
            OnPropertyChanged(nameof(CanStartMission));
            OnPropertyChanged(nameof(StartMissionUnavailableReason));
            RaiseCommands();
        }
    }
    public IReadOnlyList<GeometryWorkflowSnapshot> SelectedStepGeometryOptions => SelectedStep?.Kind switch
    {
        FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.TimedLoiter => Geometry.Where(item => item.Kind == "PointOfInterest").ToArray(),
        FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.CorridorScan => Geometry.Where(item => item.Kind == "WaypointSequence").ToArray(),
        FlightMissionStepKind.SurveyZone => Geometry.Where(item => item.Kind == "Zone").ToArray(),
        _ => []
    };
    public GeometryWorkflowSnapshot? SelectedStepGeometry
    {
        get => _selectedStepGeometry;
        set
        {
            if (!SetProperty(ref _selectedStepGeometry, value) || _loadingStepGeometry || value is null || SelectedMission is null || SelectedStep is null)
            {
                return;
            }

            _ = PersistStepGeometryAsync(SelectedMission.Id, SelectedStep.Id, value.Id);
        }
    }
    public FlightMissionStep? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!ReferenceEquals(_selectedStep, value))
            {
                CancelStepOptionsUpdate();
                CancelStepGeometryUpdate();
            }
            if (!SetProperty(ref _selectedStep, value)) return;
            LoadStepOptions(value);
            LoadStepGeometry(value);
            RaiseStepOptionVisibility();
            OnPropertyChanged(nameof(SelectedStepCameraActions));
            OnPropertyChanged(nameof(HasSelectedStepCameraActions));
            OnPropertyChanged(nameof(HasNoSelectedStepCameraActions));
            RaiseCommands();
        }
    }
    public UnitObservationSnapshot? SelectedTarget { get => _selectedTarget; set { if (SetProperty(ref _selectedTarget, value)) { OnPropertyChanged(nameof(PreviewUnit)); RequestPreview(); OnPropertyChanged(nameof(CanStartMission)); OnPropertyChanged(nameof(StartMissionUnavailableReason)); RaiseCommands(); } } }
    public UnitObservationSnapshot? PreviewUnit => SelectedTarget;
    public OperatorLocationSnapshot PreviewOperatorLocation => _operatorLocation.Snapshot;
    public FenceSnapshot? SelectedFence { get => _selectedFence; set => SetProperty(ref _selectedFence, value); }
    public ReviewedOperationSnapshot? PendingOperation { get => _pendingOperation; private set { if (SetProperty(ref _pendingOperation, value)) RaiseCommands(); } }
    public bool HasPendingOperation => PendingOperation is not null &&
        PendingOperation.State is ReviewedOperationState.Ready or ReviewedOperationState.Unavailable or ReviewedOperationState.Executing or ReviewedOperationState.Failed;
    public string PendingOperationOutcome => PendingOperation is null ? string.Empty : PendingOperation.State switch
    {
        ReviewedOperationState.Ready => "Ready to execute",
        ReviewedOperationState.Unavailable => "Blocked",
        ReviewedOperationState.Executing => "In progress",
        ReviewedOperationState.Succeeded => "Completed",
        ReviewedOperationState.Failed => "Failed",
        ReviewedOperationState.Cancelled => "Cancelled",
        ReviewedOperationState.Expired => "Expired",
        _ => PendingOperation.State.ToString()
    };
    public string NewName { get => _newName; set { if (SetProperty(ref _newName, value)) RaiseCommands(); } }
    public bool IsCreatingMission
    {
        get => _isCreatingMission;
        private set
        {
            if (!SetProperty(ref _isCreatingMission, value)) return;
            OnPropertyChanged(nameof(IsNameEditorVisible));
            OnPropertyChanged(nameof(IsMissionNameDisplayVisible));
            OnPropertyChanged(nameof(MissionNewButtonLabel));
            RaiseCommands();
        }
    }
    public bool IsEditingMissionName
    {
        get => _isEditingMissionName;
        private set
        {
            if (!SetProperty(ref _isEditingMissionName, value)) return;
            OnPropertyChanged(nameof(IsNameEditorVisible));
            OnPropertyChanged(nameof(IsMissionNameDisplayVisible));
            OnPropertyChanged(nameof(MissionNameActionLabel));
            RaiseCommands();
        }
    }
    public bool IsNameEditorVisible => IsCreatingMission || IsEditingMissionName;
    public bool IsMissionNameDisplayVisible => HasMission && !IsNameEditorVisible;
    public string MissionNewButtonLabel => IsCreatingMission
        ? _localization.Get("FlightMissionCreate")
        : _localization.Get("FlightMissionNew");
    public string MissionNameActionLabel => IsEditingMissionName
        ? _localization.Get("FlightMissionSaveName")
        : _localization.Get("FlightMissionRename");
    public double Altitude { get => _altitude; set => SetProperty(ref _altitude, value); }
    public FlightMissionEndAction EndAction { get => _endAction; set { if (SetProperty(ref _endAction, value)) RaiseCommands(); } }
    public string EndActionDisplay
    {
        get => EndAction == FlightMissionEndAction.ReturnToLaunch
            ? _localization.Get("FlightMissionEndRtl")
            : _localization.Get("FlightMissionEndHold");
        set
        {
            var selected = string.Equals(value, _localization.Get("FlightMissionEndRtl"), StringComparison.Ordinal)
                ? FlightMissionEndAction.ReturnToLaunch
                : FlightMissionEndAction.Hold;
            EndAction = selected;
        }
    }
    public double CruiseSpeed { get => _cruiseSpeed; set { if (SetProperty(ref _cruiseSpeed, value)) RaiseCommands(); } }
    public double LoiterSeconds { get => _loiterSeconds; set { if (SetProperty(ref _loiterSeconds, value)) { ScheduleStepOptionsUpdate(); RaiseCommands(); } } }
    public double SurveySpacing { get => _surveySpacing; set { if (SetProperty(ref _surveySpacing, value)) { ScheduleStepOptionsUpdate(); RaiseCommands(); } } }
    public double SurveyBearing { get => _surveyBearing; set { if (SetProperty(ref _surveyBearing, value)) ScheduleStepOptionsUpdate(); } }
    public double SurveyTurnaround { get => _surveyTurnaround; set { if (SetProperty(ref _surveyTurnaround, value)) ScheduleStepOptionsUpdate(); } }
    public bool SurveyReverseEntry { get => _surveyReverseEntry; set { if (SetProperty(ref _surveyReverseEntry, value)) ScheduleStepOptionsUpdate(); } }
    public double CorridorWidth { get => _corridorWidth; set { if (SetProperty(ref _corridorWidth, value)) { ScheduleStepOptionsUpdate(); RaiseCommands(); } } }
    public double CorridorSpacing { get => _corridorSpacing; set { if (SetProperty(ref _corridorSpacing, value)) { ScheduleStepOptionsUpdate(); RaiseCommands(); } } }
    public double CorridorTurnaround { get => _corridorTurnaround; set { if (SetProperty(ref _corridorTurnaround, value)) ScheduleStepOptionsUpdate(); } }
    public double CorridorFrontLap { get => _corridorFrontLap; set { if (SetProperty(ref _corridorFrontLap, value)) ScheduleStepOptionsUpdate(); } }
    public double CorridorSideLap { get => _corridorSideLap; set { if (SetProperty(ref _corridorSideLap, value)) ScheduleStepOptionsUpdate(); } }
    public bool CorridorReverseDirection { get => _corridorReverseDirection; set { if (SetProperty(ref _corridorReverseDirection, value)) ScheduleStepOptionsUpdate(); } }
    public FlightMissionCorridorEntrySide CorridorEntrySide { get => _corridorEntrySide; set { if (SetProperty(ref _corridorEntrySide, value)) { OnPropertyChanged(nameof(CorridorEntryRight)); ScheduleStepOptionsUpdate(); } } }
    public bool CorridorEntryRight { get => CorridorEntrySide == FlightMissionCorridorEntrySide.Right; set => CorridorEntrySide = value ? FlightMissionCorridorEntrySide.Right : FlightMissionCorridorEntrySide.Left; }
    public bool CorridorImagesInTurnarounds { get => _corridorImagesInTurnarounds; set { if (SetProperty(ref _corridorImagesInTurnarounds, value)) ScheduleStepOptionsUpdate(); } }
    public bool AutomaticPhotoCaptureEnabled { get => _automaticPhotoCaptureEnabled; set { if (SetProperty(ref _automaticPhotoCaptureEnabled, value)) ScheduleStepOptionsUpdate(); } }
    public double? CameraDistance { get => _cameraDistance; set { if (SetProperty(ref _cameraDistance, value)) ScheduleStepOptionsUpdate(); } }
    public double? CameraInterval { get => _cameraInterval; set { if (SetProperty(ref _cameraInterval, value)) ScheduleStepOptionsUpdate(); } }
    public IReadOnlyList<FlightMissionCameraActionKind> CameraActionKinds { get; } = Enum.GetValues<FlightMissionCameraActionKind>();
    public IReadOnlyList<FlightMissionCameraMode> CameraActionModes { get; } = Enum.GetValues<FlightMissionCameraMode>();
    public IReadOnlyList<FlightMissionGimbalFrame> CameraActionGimbalFrames { get; } = Enum.GetValues<FlightMissionGimbalFrame>();
    public FlightMissionCameraActionKind CameraActionKind
    {
        get => _cameraActionKind;
        set
        {
            if (!SetProperty(ref _cameraActionKind, value)) return;
            OnPropertyChanged(nameof(ShowCameraActionInterval));
            OnPropertyChanged(nameof(ShowCameraActionDistance));
            OnPropertyChanged(nameof(ShowCameraActionMode));
            OnPropertyChanged(nameof(ShowCameraActionRoi));
            OnPropertyChanged(nameof(ShowCameraActionGimbal));
        }
    }
    public double CameraActionInterval { get => _cameraActionInterval; set => SetProperty(ref _cameraActionInterval, value); }
    public double CameraActionDistance { get => _cameraActionDistance; set => SetProperty(ref _cameraActionDistance, value); }
    public FlightMissionCameraMode CameraActionMode { get => _cameraActionMode; set => SetProperty(ref _cameraActionMode, value); }
    public string CameraActionCameraName { get => _cameraActionCameraName; set => SetProperty(ref _cameraActionCameraName, value); }
    public double? CameraActionRoiLatitude { get => _cameraActionRoiLatitude; set => SetProperty(ref _cameraActionRoiLatitude, value); }
    public double? CameraActionRoiLongitude { get => _cameraActionRoiLongitude; set => SetProperty(ref _cameraActionRoiLongitude, value); }
    public double? CameraActionGimbalPitch { get => _cameraActionGimbalPitch; set => SetProperty(ref _cameraActionGimbalPitch, value); }
    public double? CameraActionGimbalYaw { get => _cameraActionGimbalYaw; set => SetProperty(ref _cameraActionGimbalYaw, value); }
    public double? CameraActionGimbalRoll { get => _cameraActionGimbalRoll; set => SetProperty(ref _cameraActionGimbalRoll, value); }
    public FlightMissionGimbalFrame CameraActionGimbalFrame { get => _cameraActionGimbalFrame; set => SetProperty(ref _cameraActionGimbalFrame, value); }
    public bool ShowCameraActionInterval => CameraActionKind == FlightMissionCameraActionKind.PhotoByTime;
    public bool ShowCameraActionDistance => CameraActionKind == FlightMissionCameraActionKind.PhotoByDistance;
    public bool ShowCameraActionMode => CameraActionKind == FlightMissionCameraActionKind.CameraMode;
    public bool ShowCameraActionRoi => CameraActionKind == FlightMissionCameraActionKind.RegionOfInterest;
    public bool ShowCameraActionGimbal => CameraActionKind == FlightMissionCameraActionKind.Gimbal;
    public IReadOnlyList<FlightMissionCameraAction> MissionStartCameraActions => SelectedMission?.CameraIntent?.Actions ?? [];
    public bool HasMissionStartCameraActions => MissionStartCameraActions.Count > 0;
    public bool HasNoMissionStartCameraActions => !HasMissionStartCameraActions;
    public IReadOnlyList<FlightMissionCameraAction> SelectedStepCameraActions
        => SelectedStep is { } step ? CameraIntentForStep(step)?.Actions ?? [] : [];
    public bool HasSelectedStepCameraActions => SelectedStepCameraActions.Count > 0;
    public bool HasNoSelectedStepCameraActions => SelectedStep is not null && !HasSelectedStepCameraActions;
    public string CameraPlanSummary
    {
        get
        {
            if (SelectedMission is null) return string.Empty;
            var count = MissionStartCameraActions.Count + SelectedMission.Steps
                .Sum(step => CameraIntentForStep(step)?.Actions?.Count ?? 0);
            return count == 0
                ? _localization.Get("FlightMissionNoCameraActions")
                : string.Format(CultureInfo.CurrentCulture, _localization.Get("FlightMissionCameraActionsConfigured"), count);
        }
    }
    public bool TerrainFollowing { get => _terrainFollowing; set => SetProperty(ref _terrainFollowing, value); }
    public bool ShowSurveyOptions => SelectedStep?.Kind == FlightMissionStepKind.SurveyZone;
    public bool ShowCorridorOptions => SelectedStep?.Kind == FlightMissionStepKind.CorridorScan;
    public bool ShowLoiterOptions => SelectedStep?.Kind == FlightMissionStepKind.TimedLoiter;
    public bool ShowCameraOptions => SelectedStep?.Kind is FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan;
    public bool ShowStepGeometrySelector => SelectedStep?.Kind is FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan or FlightMissionStepKind.TimedLoiter;
    public bool ShowTakeoffAltitude => SelectedStep?.Kind == FlightMissionStepKind.Takeoff;
    public bool ShowStepOverrides => SelectedStep?.Kind is FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan or FlightMissionStepKind.TimedLoiter;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string MissionStartStatus { get => _missionStartStatus; private set { if (SetProperty(ref _missionStartStatus, value)) OnPropertyChanged(nameof(HasMissionStartStatus)); } }
    public bool HasMissionStartStatus => !string.IsNullOrWhiteSpace(MissionStartStatus);
    public string MissionOperationStatus { get => _missionOperationStatus; private set { if (SetProperty(ref _missionOperationStatus, value)) OnPropertyChanged(nameof(HasMissionOperationStatus)); } }
    public bool HasMissionOperationStatus => !string.IsNullOrWhiteSpace(MissionOperationStatus);
    public FlightMissionExecutionSnapshot? Execution => SelectedMission is null ? null : _workflow.Executions.FirstOrDefault(item => item.MissionId == SelectedMission.Id);
    public bool HasExecution => Execution is not null;
    public bool IsAwaitingPostLandingDecision => Execution?.PostLandingState == FlightMissionPostLandingState.AwaitingDecision;
    public string ExecutionStatusText
    {
        get
        {
            var execution = Execution;
            if (execution is null) return string.Empty;

            var stateKey = execution.State switch
            {
                FlightMissionExecutionState.Uploaded => "FlightMissionExecutionReady",
                FlightMissionExecutionState.Running => "FlightMissionExecutionRunning",
                FlightMissionExecutionState.Paused => "FlightMissionExecutionPaused",
                FlightMissionExecutionState.Completed => "FlightMissionExecutionComplete",
                FlightMissionExecutionState.Interrupted => "FlightMissionExecutionInterrupted",
                FlightMissionExecutionState.Failed => "FlightMissionExecutionError",
                FlightMissionExecutionState.NotUploaded => "FlightMissionExecutionNotUploaded",
                _ => "FlightMissionExecutionUnknown"
            };
            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, _localization.Get("FlightMissionExecutionStatus"), _localization.Get(stateKey))
            };
            if (execution.CurrentItemIndex is { } index && execution.ItemCount > 0)
                lines.Add(string.Format(CultureInfo.CurrentCulture, _localization.Get("FlightMissionExecutionRouteItem"), Math.Min(index + 1, execution.ItemCount), execution.ItemCount));
            if (execution.State == FlightMissionExecutionState.Unknown)
                lines.Add(_localization.Get("FlightMissionExecutionUnconfirmed"));
            else if ((execution.State is FlightMissionExecutionState.Interrupted or FlightMissionExecutionState.Failed) && !string.IsNullOrWhiteSpace(execution.Summary))
                lines.Add(execution.Summary);
            if (execution.PostLandingState == FlightMissionPostLandingState.AwaitingDecision)
                lines.Add(_localization.Get("FlightMissionExecutionPostLandingDecision"));
            return string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
        }
    }
    public FlightMissionCompilationPreview? Preview { get; private set; }
    public string PreviewSummary => Preview?.Summary ?? string.Empty;
    public bool HasCaptureStatistics => Preview?.CaptureStatistics?.HasCaptures == true;
    public string CaptureStatisticsSummary
        => Preview?.CaptureStatistics is { HasCaptures: true } statistics
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localization.Get("FlightMissionCaptureStatistics"),
                statistics.ExpectedPhotoCount,
                statistics.ExpectedVideoDurationSeconds,
                statistics.TriggerCommandCount)
            : string.Empty;
    private bool HasMissionSelected() => SelectedMission is not null; private bool HasTarget() => SelectedTarget is not null; private bool CanOperate() => HasMissionSelected() && HasTarget();
    private FlightMissionExecutionSnapshot? ActiveExecutionForTarget => SelectedTarget is null
        ? null
        : _workflow.Executions.FirstOrDefault(item => item.VehicleId == SelectedTarget.Id &&
            item.State is FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused);
    public bool CanStartMission => CanOperate() && !_missionStartInProgress && ActiveExecutionForTarget is null;
    private bool CanPrepareUpload() => CanOperate() && !_missionStartInProgress && !_missionOperationInProgress && PendingOperation?.State != ReviewedOperationState.Executing;
    private string UploadUnavailableReason => SelectedMission is null
        ? "Select a mission before uploading."
        : SelectedTarget is null
            ? "Select a drone before uploading the mission."
            : _missionStartInProgress
                ? "Mission upload and start are already in progress for this drone."
                : _missionOperationInProgress || PendingOperation?.State == ReviewedOperationState.Executing
                    ? "Mission upload is already in progress for this drone."
                    : "Mission upload cannot be prepared right now.";
    public string StartMissionUnavailableReason => _missionStartInProgress
        ? "Mission upload and start are already in progress for this drone."
        : ActiveExecutionForTarget is { } execution
            ? $"{(execution.MissionId == SelectedMission?.Id ? "This mission" : "Another mission")} is {execution.State.ToString().ToLowerInvariant()} for this drone. Pause or complete it before starting a new mission."
            : string.Empty;
    private async Task BeginOrCreateMissionAsync(CancellationToken token)
    {
        if (!IsCreatingMission)
        {
            NewName = LocalizationService.Current.Get("FlightMissionDefaultName");
            IsEditingMissionName = false;
            IsCreatingMission = true;
            return;
        }

        await CreateAsync(token);
    }
    private async Task CreateAsync(CancellationToken token)
    {
        SelectMissionSnapshot(await _workflow.CreateAsync(new(NewName, Altitude), token));
        IsCreatingMission = false;
    }
    private async Task RenameAsync(CancellationToken token)
    {
        if (!IsEditingMissionName)
        {
            NewName = Require().Name;
            IsEditingMissionName = true;
            return;
        }

        await _workflow.RenameAsync(Require().Id, NewName, token);
        IsEditingMissionName = false;
    }
    private async Task DuplicateAsync(CancellationToken token) { SelectMissionSnapshot(await _workflow.DuplicateAsync(Require().Id, null, token)); }
    private Task BeginDeleteAsync(CancellationToken token)
    {
        DeleteAssociatedGeometry = false;
        DeleteConflictMessage = string.Empty;
        DeleteConfirmationPending = true;
        return Task.CompletedTask;
    }

    private async Task ConfirmDeleteAsync(CancellationToken token)
    {
        if (SelectedMission is not { } mission || !DeleteConfirmationPending)
        {
            return;
        }

        if (DeleteAssociatedGeometry)
        {
            var associated = AssociatedGeometry(mission);
            var conflicts = associated
                .Select(geometry => (Geometry: geometry, Missions: _workflow.Missions
                    .Where(other => other.Id != mission.Id)
                    .Where(other => other.Steps.Any(step => string.Equals(step.SourceGeometryId, geometry.Id, StringComparison.Ordinal)))
                    .Select(other => other.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
                .Where(item => item.Missions.Length > 0)
                .ToArray();
            if (conflicts.Length > 0)
            {
                DeleteConflictMessage = string.Join(
                    " ",
                    conflicts.Select(item =>
                        $"'{item.Geometry.Name}' is also used by {string.Join(", ", item.Missions.Select(name => $"'{name}'"))}."));
                return;
            }

            try
            {
                foreach (var geometry in associated)
                {
                    await _geometry.RemoveAsync(geometry.Id, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Status = $"Mission deletion could not remove associated geometry: {exception.Message}";
                return;
            }
        }

        await DeleteMissionOnlyAsync(token);
    }

    private async Task DeleteMissionOnlyAsync(CancellationToken token)
    {
        if (SelectedMission is not { } mission)
        {
            return;
        }

        await _workflow.DeleteAsync(mission.Id, token);
        DeleteConfirmationPending = false;
        DeleteAssociatedGeometry = false;
        DeleteConflictMessage = string.Empty;
        SelectedMission = null;
    }

    private IReadOnlyList<(string Id, string Name)> AssociatedGeometry(FlightMissionSnapshot mission)
        => mission.Steps
            .Select(step => step.SourceGeometryId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => _geometry.LocalDocuments.FirstOrDefault(item => item.Id == id) is { } geometry
                ? (Id: geometry.Id, Name: geometry.Name)
                : (Id: string.Empty, Name: string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
    private async Task ValidateAsync(CancellationToken token)
    {
        try
        {
            var findings = await _workflow.ValidateAsync(Require().Id, SelectedTarget?.Id, token);
            Status = findings.Count == 0 ? "Mission is ready." : string.Join(" ", findings.Select(item => item.Message));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation is normal when the selected mission or target
            // changes while validation is in flight.
        }
        catch (Exception exception)
        {
            // Validation is operator feedback, not a reason for an Avalonia
            // dispatcher exception to terminate the application.
            Status = $"Mission validation could not complete: {exception.Message}";
        }
    }
    private async Task SetAltitudeAsync(CancellationToken token) { SelectMissionSnapshot(await _workflow.SetAltitudeAsync(Require().Id, Altitude, token)); }
    private async Task SetSpeedAsync(CancellationToken token) { SelectMissionSnapshot(await _workflow.SetCruiseSpeedAsync(Require().Id, CruiseSpeed, token)); }
    private async Task SetStepOverridesAsync(CancellationToken token)
    {
        if (SelectedStep is null) return;
        SelectMissionSnapshot(await _workflow.SetStepOverridesAsync(Require().Id, SelectedStep.Id, Altitude, CruiseSpeed, TerrainFollowing, token));
    }
    private async Task SetTakeoffAltitudeAsync(CancellationToken token)
    {
        if (SelectedStep?.Kind != FlightMissionStepKind.Takeoff) return;
        SelectMissionSnapshot(await _workflow.SetStepOverridesAsync(
            Require().Id, SelectedStep.Id, Altitude, null, false, token));
    }
    private async Task SetFenceAsync(CancellationToken token)
    {
        var mission = Require();
        await _workflow.SetTargetAssignmentAsync(mission.Id,
            new FlightMissionTargetAssignment("Px4", "Multicopter", SelectedFence?.Document.FenceId), token);
    }
    private async Task AddSurveyAsync(CancellationToken token)
        => await AddAsync(() => _workflow.AddSurveyAsync(Require().Id, null,
            new FlightMissionSurveyOptions(SurveySpacing, SurveyBearing, SurveyTurnaround, SurveyReverseEntry, BuildAutomaticPhotoCaptureIntent()), token));
    private async Task AddCorridorAsync(CancellationToken token)
        => await AddAsync(() => _workflow.AddCorridorAsync(Require().Id, null,
            new FlightMissionCorridorOptions(CorridorWidth, CorridorSpacing, CorridorTurnaround, CorridorReverseDirection,
                CorridorEntrySide, CorridorFrontLap, CorridorSideLap, CorridorImagesInTurnarounds,
                BuildAutomaticPhotoCaptureIntent()), token));
    private async Task AddLoiterAsync(CancellationToken token)
        => await AddAsync(() => _workflow.AddTimedLoiterAsync(Require().Id, null, LoiterSeconds, token));
    private async Task AddMissionStartCameraActionAsync(CancellationToken token)
    {
        try
        {
            var updated = await _workflow.SetMissionCameraActionsAsync(
                Require().Id, MissionStartCameraActions.Append(BuildCameraAction()).ToArray(), token);
            SelectMissionSnapshot(updated);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            Status = exception.Message;
        }
    }
    private async Task AddSelectedStepCameraActionAsync(CancellationToken token)
    {
        if (SelectedStep is not { } step) return;
        try
        {
            var updated = await _workflow.SetStepCameraActionsAsync(
                Require().Id, step.Id, SelectedStepCameraActions.Append(BuildCameraAction()).ToArray(), token);
            SelectMissionSnapshot(updated);
            SelectedStep = SelectedMission?.Steps.FirstOrDefault(item => item.Id == step.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            Status = exception.Message;
        }
    }
    private async Task RemoveMissionStartCameraActionAsync(object? parameter)
    {
        if (parameter is not FlightMissionCameraAction action || SelectedMission is not { } mission) return;
        try
        {
            var actions = MissionStartCameraActions.ToList();
            actions.Remove(action);
            SelectMissionSnapshot(await _workflow.SetMissionCameraActionsAsync(mission.Id, actions, CancellationToken.None));
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            Status = exception.Message;
        }
    }
    private async Task RemoveSelectedStepCameraActionAsync(object? parameter)
    {
        if (parameter is not FlightMissionCameraAction action || SelectedMission is not { } mission || SelectedStep is not { } step) return;
        try
        {
            var actions = SelectedStepCameraActions.ToList();
            actions.Remove(action);
            SelectMissionSnapshot(await _workflow.SetStepCameraActionsAsync(mission.Id, step.Id, actions, CancellationToken.None));
            SelectedStep = SelectedMission?.Steps.FirstOrDefault(item => item.Id == step.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            Status = exception.Message;
        }
    }
    private FlightMissionCameraAction BuildCameraAction()
    {
        var cameraName = string.IsNullOrWhiteSpace(CameraActionCameraName) ? null : CameraActionCameraName.Trim();
        return CameraActionKind switch
        {
            FlightMissionCameraActionKind.PhotoOnce => FlightMissionCameraAction.PhotoOnce(cameraName),
            FlightMissionCameraActionKind.PhotoByTime => FlightMissionCameraAction.PhotoByTime(CameraActionInterval, cameraName),
            FlightMissionCameraActionKind.PhotoByDistance => FlightMissionCameraAction.PhotoByDistance(CameraActionDistance, cameraName),
            FlightMissionCameraActionKind.StopPhotos => FlightMissionCameraAction.StopPhotos(cameraName),
            FlightMissionCameraActionKind.StartVideo => FlightMissionCameraAction.StartVideo(cameraName),
            FlightMissionCameraActionKind.StopVideo => FlightMissionCameraAction.StopVideo(cameraName),
            FlightMissionCameraActionKind.CameraMode => FlightMissionCameraAction.SetCameraMode(CameraActionMode, cameraName),
            FlightMissionCameraActionKind.RegionOfInterest => FlightMissionCameraAction.SetRegionOfInterest(
                new(CameraActionRoiLatitude ?? double.NaN, CameraActionRoiLongitude ?? double.NaN), cameraName),
            FlightMissionCameraActionKind.Gimbal => FlightMissionCameraAction.SetGimbal(
                CameraActionGimbalPitch, CameraActionGimbalYaw, CameraActionGimbalRoll, CameraActionGimbalFrame, cameraName),
            _ => throw new InvalidOperationException("Select a valid camera action first.")
        };
    }
    private async Task RemoveStepAsync(CancellationToken token) { var steps = Require().Steps; var step = steps.Count > 0 ? steps[^1] : null; if (step is not null) { SelectMissionSnapshot(await _workflow.RemoveStepAsync(Require().Id, step.Id, token)); } }
    private async Task MoveStepAsync(int direction, CancellationToken token)
    {
        if (SelectedMission is null || SelectedStep is null) return;
        var stepId = SelectedStep.Id;
        var index = StepIndex(SelectedMission, SelectedStep);
        var updated = await _workflow.MoveStepAsync(SelectedMission.Id, stepId, index + direction, token);
        SelectMissionSnapshot(updated);
        SelectedStep = SelectedMission.Steps.FirstOrDefault(step => step.Id == stepId);
    }
    private async Task AddAsync(Func<Task<FlightMissionSnapshot>> add)
    {
        try
        {
            var existingStepIds = Require().Steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
            var updated = await add();
            SelectMissionSnapshot(updated);
            var selected = SelectedMission ?? updated;
            SelectedStep = selected.Steps.LastOrDefault(step => !existingStepIds.Contains(step.Id)) ?? SelectedStep;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            // Authoring errors are expected user feedback, never an unhandled Avalonia
            // dispatcher exception that can terminate the desktop application.
            Status = exception.Message;
        }
    }
    private async Task SetEndActionAsync(CancellationToken token)
    {
        if (SelectedMission is null) return;
        SelectMissionSnapshot(await _workflow.SetEndActionAsync(SelectedMission.Id, EndAction, token));
    }
    private async Task PlanAsync(string operation, CancellationToken token)
    {
        await CreatePlanAsync(operation, token);
    }

    private async Task PrepareUploadAsync(CancellationToken token)
    {
        if (!CanPrepareUpload())
        {
            MissionOperationStatus = UploadUnavailableReason;
            Status = MissionOperationStatus;
            return;
        }

        _missionOperationInProgress = true;
        MissionOperationStatus = "Preparing mission upload...";
        Status = MissionOperationStatus;
        RaiseCommands();
        try
        {
            // Let Avalonia paint the busy state before preview/terrain work begins.
            await Task.Yield();
            var plan = await CreatePlanAsync("upload", token);
            if (plan is null)
            {
                MissionOperationStatus = "Mission upload could not be prepared.";
                Status = MissionOperationStatus;
                return;
            }
            if (!plan.CanExecute)
            {
                MissionOperationStatus = HumanReadableBlock(plan);
                Status = MissionOperationStatus;
                return;
            }

            MissionOperationStatus = "Uploading mission to the drone...";
            Status = MissionOperationStatus;
            var result = await _reviewed.ExecuteAsync(plan.Id, token);
            MissionOperationStatus = result.Succeeded
                ? string.Empty
                : HumanReadableResult("Mission upload", result, plan);
            Status = result.Succeeded ? string.Empty : MissionOperationStatus;
            Refresh();
        }
        finally
        {
            _missionOperationInProgress = false;
            RaiseCommands();
        }
    }

    private async Task<ReviewedOperationSnapshot?> CreatePlanAsync(string operation, CancellationToken token)
    {
        try
        {
            var target = SelectedTarget ?? throw new InvalidOperationException("Select a mission target first.");
            // The workflow resolves the actual MAVLink member for a reconciled unit.
            // Passing the authority ID here also gives it an unambiguous fallback.
            var connection = target.CommandAuthorityVehicleId ?? (target.ConnectionIds.Count > 0 ? target.ConnectionIds[0] : null) ?? throw new InvalidOperationException("The selected unit has no mission connection.");
            var plan = operation switch
            {
                "upload" => await _workflow.PlanUploadAsync(Require().Id, connection, target.Id, token),
                "download" => await _workflow.PlanDownloadAsync(connection, target.Id, NewName, token),
                "start" => await _workflow.PlanStartAsync(Require().Id, connection, target.Id, token),
                "pause" => await _workflow.PlanPauseAsync(Require().Id, connection, target.Id, token),
                "continue" => await _workflow.PlanContinueAsync(Require().Id, connection, target.Id, token),
                "retain" => await _workflow.PlanRetainAsync(Require().Id, connection, target.Id, token),
                "remove" => await _workflow.PlanRemoveAsync(Require().Id, connection, target.Id, token),
                _ => await _workflow.PlanResumeAsync(Require().Id, connection, target.Id, token)
            };
            PendingOperation = plan;
            Status = plan.CanExecute ? plan.Summary : HumanReadableBlock(plan);
            return plan;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Status = $"Mission operation could not be prepared: {exception.Message}";
        }
        return null;
    }
    private async Task StartMissionAsync(CancellationToken token)
    {
        if (SelectedMission is null)
        {
            MissionStartStatus = "Select a mission before starting it.";
            Status = MissionStartStatus;
            return;
        }
        if (SelectedTarget is null)
        {
            MissionStartStatus = "Select a drone before starting the mission.";
            Status = MissionStartStatus;
            return;
        }
        if (_missionStartInProgress)
        {
            MissionStartStatus = "Mission upload and start are already in progress for this drone.";
            Status = MissionStartStatus;
            return;
        }
        if (ActiveExecutionForTarget is not null)
        {
            MissionStartStatus = StartMissionUnavailableReason;
            Status = MissionStartStatus;
            return;
        }

        _missionStartInProgress = true;
        MissionStartStatus = "Starting mission: preparing the upload to the drone...";
        OnPropertyChanged(nameof(StartMissionUnavailableReason));
        OnPropertyChanged(nameof(CanStartMission));
        RaiseCommands();
        try
        {
            // Give Avalonia a turn to render the lockout and status before mission
            // preview, upload, or mode-selection work can take time.
            await Task.Yield();
            // Upload is a reviewed operation, but Start mission is intentionally a
            // complete operator action in the GUI. If this session has no uploaded
            // artifact yet, prepare and execute the upload first, then start it.
            // This avoids the confusing state where a Start plan is accepted for
            // review and later fails only because an earlier Upload plan was never
            // executed (or was replaced by the Start plan).
            if (Execution is not { State: FlightMissionExecutionState.Uploaded or FlightMissionExecutionState.Running or FlightMissionExecutionState.Paused })
            {
                MissionStartStatus = "Starting mission: uploading it to the drone...";
                Status = MissionStartStatus;
                var uploadPlan = await CreatePlanAsync("upload", token);
                if (uploadPlan is null)
                {
                    MissionStartStatus = "Mission could not start because its upload could not be prepared.";
                    Status = MissionStartStatus;
                    return;
                }
                if (!uploadPlan.CanExecute)
                {
                    MissionStartStatus = HumanReadableBlock(uploadPlan);
                    Status = MissionStartStatus;
                    return;
                }
                var uploadResult = await _reviewed.ExecuteAsync(uploadPlan.Id, token);
                Refresh();
                if (!uploadResult.Succeeded)
                {
                    MissionStartStatus = HumanReadableResult("Mission upload", uploadResult, uploadPlan);
                    Status = MissionStartStatus;
                    return;
                }
            }

            MissionStartStatus = "Mission uploaded. Requesting Mission mode from the drone...";
            Status = MissionStartStatus;
            var startPlan = await CreatePlanAsync("start", token);
            if (startPlan is null)
            {
                MissionStartStatus = "Mission start could not be prepared.";
                Status = MissionStartStatus;
                return;
            }
            if (!startPlan.CanExecute)
            {
                MissionStartStatus = HumanReadableBlock(startPlan);
                Status = MissionStartStatus;
                return;
            }
            var startResult = await _reviewed.ExecuteAsync(startPlan.Id, token);
            MissionStartStatus = startResult.Succeeded
                ? string.Empty
                : HumanReadableMissionStartFailure(startResult, startPlan);
            Status = startResult.Succeeded ? string.Empty : MissionStartStatus;
            Refresh();
        }
        finally
        {
            _missionStartInProgress = false;
            OnPropertyChanged(nameof(StartMissionUnavailableReason));
            OnPropertyChanged(nameof(CanStartMission));
            RaiseCommands();
        }
    }

    private static string HumanReadableBlock(ReviewedOperationSnapshot operation)
    {
        var finding = operation.Findings.FirstOrDefault(item => item.Severity == WorkflowFindingSeverity.Blocking)
            ?? (operation.Findings.Count > 0 ? operation.Findings[0] : null);
        return finding is null
            ? $"{operation.Title} is not available yet."
            : $"{operation.Title} is not available: {finding.Message}";
    }

    private static string HumanReadableResult(string operation, ReviewedOperationExecutionResult result, ReviewedOperationSnapshot plan)
    {
        var detail = result.Details.FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail));
        if (string.IsNullOrWhiteSpace(detail))
            detail = plan.Findings.FirstOrDefault(item => item.Severity == WorkflowFindingSeverity.Blocking)?.Message;
        return string.IsNullOrWhiteSpace(detail)
            ? $"{operation} failed: {result.Summary}"
            : $"{operation} failed: {detail}";
    }

    private static string HumanReadableMissionStartFailure(ReviewedOperationExecutionResult result, ReviewedOperationSnapshot plan)
    {
        if (result.Summary.Contains("did not confirm the requested mode", StringComparison.OrdinalIgnoreCase))
            return "PX4 accepted the Mission request, but did not confirm Mission mode. The mission has not begun; check the vehicle status and try again.";
        return HumanReadableResult("Mission start", result, plan);
    }
    private async Task ExecuteAsync(CancellationToken token)
    {
        if (PendingOperation is null) return;
        var operation = PendingOperation;
        var isMissionUpload = operation.Kind == ReviewedOperationKind.FlightMissionUpload;
        if (isMissionUpload)
        {
            _missionOperationInProgress = true;
            MissionOperationStatus = "Uploading mission to the drone...";
            Status = MissionOperationStatus;
            RaiseCommands();
        }
        try
        {
            var result = await _reviewed.ExecuteAsync(operation.Id, token);
            if (isMissionUpload)
                MissionOperationStatus = result.Succeeded ? string.Empty : HumanReadableResult("Mission upload", result, operation);
            Status = result.Succeeded ? string.Empty : result.Summary;
            Refresh();
        }
        finally
        {
            if (isMissionUpload)
            {
                _missionOperationInProgress = false;
                RaiseCommands();
            }
        }
    }
    private async Task CancelOperationAsync(CancellationToken token) { if (PendingOperation is null) return; await _reviewed.CancelAsync(PendingOperation.Id, cancellationToken: token); Status = "Mission operation cancelled."; Refresh(); }
    public async Task ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        SelectMissionSnapshot(await _workflow.ImportAsync(path, false, cancellationToken));
        Status = "Mission imported.";
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        await _workflow.ExportAsync(Require().Id, path, cancellationToken);
        Status = "Mission exported.";
    }
    private FlightMissionSnapshot Require() => SelectedMission ?? throw new InvalidOperationException("Select a mission first.");
    private void SelectMissionSnapshot(FlightMissionSnapshot snapshot)
    {
        // Always bind SelectedMission to the instance currently present in the
        // ObservableCollection.  Binding to the workflow's returned snapshot
        // directly leaves Avalonia's ListBox with a SelectedItem that is not in
        // ItemsSource, which looks like the mission was deselected after a step
        // was added.
        Refresh();
        SelectedMission = Missions.FirstOrDefault(item => item.Id == snapshot.Id) ?? snapshot;
    }
    private static int StepIndex(FlightMissionSnapshot mission, FlightMissionStep step)
        => mission.Steps.Select((item, index) => (item, index)).FirstOrDefault(item => item.item.Id == step.Id).index;
    private void OnChanged(object? sender, EventArgs e) { if (_dispatcher.CheckAccess()) Refresh(); else _ = _dispatcher.InvokeAsync(Refresh); }
    private void Refresh()
    {
        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshQueued = false;
                RefreshCore();
            }
            while (_refreshQueued);
        }
        finally { _refreshing = false; }
    }

    private void RefreshCore()
    {
        var missionId = SelectedMission?.Id; var targetId = SelectedTarget?.Id; var stepId = SelectedStep?.Id; var fenceId = SelectedFence?.Document.FenceId;
        Synchronize(Missions, _workflow.Missions, item => item.Id, MissionEquivalent);
        Synchronize(Geometry, _geometry.LocalDocuments.Where(item => item.Kind is "PointOfInterest" or "WaypointSequence" or "Zone"), item => item.Id);
        // Ghosts and PX4 multicopters share the native mission workflow.  The
        // previous PX4-only filter hid Ghosts from the target selector even
        // though the Runtime already provides a Ghost mission executor, leaving
        // every upload/start button disabled with an empty dropdown.
        Synchronize(Targets, _units.Units.Where(item =>
            item.ConnectionIds.Count > 0 &&
            item.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase) &&
            (item.IsGhost || item.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase))), item => item.Id);
        Synchronize(Fences, _fences.Fences, item => item.Document.FenceId);
        _selectedMission = Missions.FirstOrDefault(item => item.Id == missionId);
        // A single eligible target is unambiguous, so select it on first load.
        // This keeps the common Ghost-only/PX4-only workflow immediately usable
        // while still requiring an explicit choice when multiple vehicles are
        // available.
        var effectiveTargetId = targetId ?? (Targets.Count == 1 ? Targets[0].Id : null);
        _selectedTarget = Targets.FirstOrDefault(item => item.Id == effectiveTargetId); _selectedFence = Fences.FirstOrDefault(item => item.Document.FenceId == (fenceId ?? _selectedMission?.TargetAssignment?.ActiveFenceId));
        var refreshedStep = _selectedMission?.Steps.FirstOrDefault(item => item.Id == stepId);
        if (_stepOptionsMissionId is not null &&
            (!string.Equals(_stepOptionsMissionId, _selectedMission?.Id, StringComparison.Ordinal) ||
             !string.Equals(_stepOptionsStepId, refreshedStep?.Id, StringComparison.Ordinal)))
        {
            CancelStepOptionsUpdate();
        }
        _selectedStep = refreshedStep;
        LoadStepOptions(_selectedStep);
        LoadStepGeometry(_selectedStep);
        RaiseStepOptionVisibility();
        if (PendingOperation is not null && _reviewed.TryGet(PendingOperation.Id, out var current)) _pendingOperation = current;
        EndAction = _selectedMission?.EndAction ?? FlightMissionEndAction.Hold;
        OnPropertyChanged(nameof(SelectedMission)); OnPropertyChanged(nameof(HasMission)); OnPropertyChanged(nameof(IsMissionNameDisplayVisible)); OnPropertyChanged(nameof(IsNameEditorVisible)); OnPropertyChanged(nameof(SelectedTarget)); OnPropertyChanged(nameof(PreviewUnit)); OnPropertyChanged(nameof(PreviewOperatorLocation)); OnPropertyChanged(nameof(SelectedFence)); OnPropertyChanged(nameof(SelectedStep)); OnPropertyChanged(nameof(SelectedStepGeometryOptions)); OnPropertyChanged(nameof(SelectedStepGeometry)); OnPropertyChanged(nameof(MissionStartCameraActions)); OnPropertyChanged(nameof(HasMissionStartCameraActions)); OnPropertyChanged(nameof(HasNoMissionStartCameraActions)); OnPropertyChanged(nameof(SelectedStepCameraActions)); OnPropertyChanged(nameof(HasSelectedStepCameraActions)); OnPropertyChanged(nameof(HasNoSelectedStepCameraActions)); OnPropertyChanged(nameof(CameraPlanSummary)); OnPropertyChanged(nameof(PendingOperation)); OnPropertyChanged(nameof(HasPendingOperation)); OnPropertyChanged(nameof(PendingOperationOutcome)); OnPropertyChanged(nameof(Execution)); OnPropertyChanged(nameof(HasExecution)); OnPropertyChanged(nameof(IsAwaitingPostLandingDecision)); OnPropertyChanged(nameof(ExecutionStatusText)); OnPropertyChanged(nameof(CanStartMission)); OnPropertyChanged(nameof(StartMissionUnavailableReason)); RaiseCommands();
        RequestPreview();
    }

    private void RequestPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        var missionId = SelectedMission?.Id;
        if (missionId is null)
        {
            Preview = null;
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(PreviewSummary));
            OnPropertyChanged(nameof(HasCaptureStatistics));
            OnPropertyChanged(nameof(CaptureStatisticsSummary));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _ = RefreshPreviewAsync(missionId, SelectedTarget?.Id, cancellation);
    }

    private async Task RefreshPreviewAsync(string missionId, string? targetId, CancellationTokenSource cancellation)
    {
        try
        {
            var preview = await _workflow.PreviewAsync(missionId, targetId, cancellation.Token);
            if (cancellation.IsCancellationRequested || !string.Equals(SelectedMission?.Id, missionId, StringComparison.Ordinal)) return;
            void Apply()
            {
                if (cancellation.IsCancellationRequested || !string.Equals(SelectedMission?.Id, missionId, StringComparison.Ordinal)) return;
                Preview = preview;
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(PreviewSummary));
                OnPropertyChanged(nameof(HasCaptureStatistics));
                OnPropertyChanged(nameof(CaptureStatisticsSummary));
            }
            if (_dispatcher.CheckAccess()) Apply();
            else await _dispatcher.InvokeAsync(Apply);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (cancellation.IsCancellationRequested || !string.Equals(SelectedMission?.Id, missionId, StringComparison.Ordinal)) return;
            Preview = new FlightMissionCompilationPreview(missionId, [], 0, 0, 0, string.Empty, [], $"Preview unavailable: {exception.Message}");
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(PreviewSummary));
            OnPropertyChanged(nameof(HasCaptureStatistics));
            OnPropertyChanged(nameof(CaptureStatisticsSummary));
        }
    }

    private static bool MissionEquivalent(FlightMissionSnapshot left, FlightMissionSnapshot right)
        => string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Hash, right.Hash, StringComparison.Ordinal)
            && left.UpdatedAt == right.UpdatedAt;

    private void LoadStepGeometry(FlightMissionStep? step)
    {
        _loadingStepGeometry = true;
        try
        {
            _selectedStepGeometry = step?.SourceGeometryId is { } geometryId
                ? Geometry.FirstOrDefault(item => item.Id == geometryId)
                : null;
        }
        finally
        {
            _loadingStepGeometry = false;
        }

        OnPropertyChanged(nameof(SelectedStepGeometry));
        OnPropertyChanged(nameof(SelectedStepGeometryOptions));
    }

    private async Task PersistStepGeometryAsync(string missionId, string stepId, string geometryId)
    {
        CancelStepGeometryUpdate();
        var cancellation = new CancellationTokenSource();
        _stepGeometryCancellation = cancellation;

        try
        {
            var updated = await _workflow.SetStepGeometryAsync(missionId, stepId, geometryId, cancellation.Token);
            if (cancellation.IsCancellationRequested || SelectedMission?.Id != missionId || SelectedStep?.Id != stepId) return;
            SelectMissionSnapshot(updated);
            SelectedStep = SelectedMission?.Steps.FirstOrDefault(item => item.Id == stepId);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            Status = exception.Message;
            LoadStepGeometry(SelectedStep);
        }
    }

    private void CancelStepGeometryUpdate()
    {
        _stepGeometryCancellation?.Cancel();
        _stepGeometryCancellation?.Dispose();
        _stepGeometryCancellation = null;
    }

    private static void Synchronize<T, TKey>(ObservableCollection<T> destination, IEnumerable<T> source, Func<T, TKey> key, Func<T, T, bool>? equivalent = null)
        where TKey : notnull
    {
        // Changed events are frequent while editing a mission. Updating the collection
        // in place prevents Avalonia from briefly rendering old and new list items.
        var desired = source
            .GroupBy(key)
            .Select(group => group.Last())
            .ToArray();
        var comparer = EqualityComparer<TKey>.Default;
        var desiredKeys = desired.Select(key).ToHashSet(comparer);
        var retainedKeys = new HashSet<TKey>(comparer);

        for (var index = destination.Count - 1; index >= 0; index--)
        {
            var currentKey = key(destination[index]);
            if (!desiredKeys.Contains(currentKey) || !retainedKeys.Add(currentKey))
                destination.RemoveAt(index);
        }

        for (var desiredIndex = 0; desiredIndex < desired.Length; desiredIndex++)
        {
            var currentIndex = -1;
            for (var index = 0; index < destination.Count; index++)
            {
                if (comparer.Equals(key(destination[index]), key(desired[desiredIndex])))
                {
                    currentIndex = index;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                destination.Insert(desiredIndex, desired[desiredIndex]);
                continue;
            }

            if (currentIndex != desiredIndex)
                destination.Move(currentIndex, desiredIndex);
            if (!(equivalent?.Invoke(destination[desiredIndex], desired[desiredIndex]) ?? EqualityComparer<T>.Default.Equals(destination[desiredIndex], desired[desiredIndex])))
                destination[desiredIndex] = desired[desiredIndex];
        }
    }
    private void RaiseCommands()
    {
        foreach (var command in new[] { CreateCommand, NewMissionCommand, RenameCommand, DuplicateCommand, DeleteCommand, ConfirmDeleteCommand, DeleteMissionOnlyCommand, ValidateCommand, AddTakeoffCommand, AddRtlCommand, AddLandCommand, AddSurveyCommand, AddCorridorCommand, AddLoiterCommand, AddCameraIntentCommand, AddMissionStartCameraActionCommand, AddSelectedStepCameraActionCommand, RemoveMissionStartCameraActionCommand, RemoveSelectedStepCameraActionCommand, SetAltitudeCommand, SetSpeedCommand, SetStepOverridesCommand, SetTakeoffAltitudeCommand, SetFenceCommand, RemoveStepCommand, MoveStepUpCommand, MoveStepDownCommand, UploadCommand, DownloadCommand, StartCommand, PauseCommand, ContinueCommand, ResumeCommand, RetainCommand, RemoveOnboardCommand, SetEndActionCommand, ExecuteCommand, CancelOperationCommand })
        {
            if (command is AsyncRelayCommand async) async.RaiseCanExecuteChanged();
            else if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
        }
    }
    private void LoadStepOptions(FlightMissionStep? step)
    {
        _loadingStepOptions = true;
        try
        {
            if (step is null) return;
            Altitude = step.RelativeAltitudeMetres ?? SelectedMission?.RelativeAltitudeMetres ?? 20;
            CruiseSpeed = step.CruiseSpeedMetresPerSecond ?? SelectedMission?.CruiseSpeedMetresPerSecond ?? 5;
            TerrainFollowing = step.TerrainFollowing;
            var survey = step.Survey;
            if (survey is not null)
            {
                SurveySpacing = survey.LineSpacingMetres; SurveyBearing = survey.BearingDegrees; SurveyTurnaround = survey.TurnaroundDistanceMetres; SurveyReverseEntry = survey.ReverseEntry;
                LoadCamera(survey.CameraIntent, step);
            }
            var corridor = step.Corridor;
            if (corridor is not null)
            {
                CorridorWidth = corridor.CorridorWidthMetres; CorridorSpacing = corridor.LineSpacingMetres; CorridorTurnaround = corridor.TurnaroundDistanceMetres; CorridorReverseDirection = corridor.ReverseDirection; CorridorEntrySide = corridor.EntrySide; CorridorFrontLap = corridor.FrontLapPercent; CorridorSideLap = corridor.SideLapPercent; CorridorImagesInTurnarounds = corridor.TakeImagesInTurnarounds;
                LoadCamera(corridor.CameraIntent, step);
            }
            if (step.Kind == FlightMissionStepKind.TimedLoiter) LoiterSeconds = step.LoiterDurationSeconds ?? 30;
            if (step.Kind == FlightMissionStepKind.CameraCaptureIntent) LoadCamera(step.CameraIntent, step);
        }
        finally { _loadingStepOptions = false; }
    }

    private void LoadCamera(FlightMissionCameraIntent? intent, FlightMissionStep step)
    {
        if (string.Equals(_stepOptionsMissionId, SelectedMission?.Id, StringComparison.Ordinal) &&
            string.Equals(_stepOptionsStepId, step.Id, StringComparison.Ordinal))
        {
            AutomaticPhotoCaptureEnabled = _pendingAutomaticPhotoCaptureEnabled;
            CameraDistance = _pendingCameraDistance;
            CameraInterval = _pendingCameraInterval;
            return;
        }

        AutomaticPhotoCaptureEnabled = intent?.AutomaticPhotoCaptureEnabled == true;
        CameraDistance = intent?.TriggerDistanceMetres;
        CameraInterval = intent?.TriggerIntervalSeconds;
    }

    private FlightMissionCameraIntent BuildAutomaticPhotoCaptureIntent(IReadOnlyList<FlightMissionCameraAction>? actions = null)
        => BuildAutomaticPhotoCaptureIntent(actions, AutomaticPhotoCaptureEnabled, CameraDistance, CameraInterval);

    private static FlightMissionCameraIntent BuildAutomaticPhotoCaptureIntent(
        IReadOnlyList<FlightMissionCameraAction>? actions,
        bool automaticPhotoCaptureEnabled,
        double? cameraDistance,
        double? cameraInterval)
        => new(
            Mode: "Photo",
            TriggerDistanceMetres: automaticPhotoCaptureEnabled ? cameraDistance : null,
            TriggerIntervalSeconds: automaticPhotoCaptureEnabled ? cameraInterval : null,
            Actions: actions,
            AutomaticPhotoCaptureEnabled: automaticPhotoCaptureEnabled);

    private static FlightMissionCameraIntent? CameraIntentForStep(FlightMissionStep step)
        => step.Kind == FlightMissionStepKind.SurveyZone
            ? step.Survey?.CameraIntent ?? step.CameraIntent
            : step.Kind == FlightMissionStepKind.CorridorScan
                ? step.Corridor?.CameraIntent ?? step.CameraIntent
                : step.CameraIntent;

    private void ScheduleStepOptionsUpdate()
    {
        if (_loadingStepOptions || SelectedMission is null || SelectedStep is null) return;
        if (SelectedStep.Kind is not (FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan)) return;
        _stepOptionsCancellation?.Cancel(); _stepOptionsCancellation?.Dispose();
        var missionId = SelectedMission.Id;
        var stepId = SelectedStep.Id;
        _stepOptionsMissionId = missionId;
        _stepOptionsStepId = stepId;
        _pendingAutomaticPhotoCaptureEnabled = AutomaticPhotoCaptureEnabled;
        _pendingCameraDistance = CameraDistance;
        _pendingCameraInterval = CameraInterval;
        var cancellation = new CancellationTokenSource(); _stepOptionsCancellation = cancellation;
        _ = PersistStepOptionsAsync(cancellation, missionId, stepId,
            _pendingAutomaticPhotoCaptureEnabled, _pendingCameraDistance, _pendingCameraInterval);
    }

    private async Task PersistStepOptionsAsync(
        CancellationTokenSource cancellation,
        string missionId,
        string stepId,
        bool automaticPhotoCaptureEnabled,
        double? cameraDistance,
        double? cameraInterval)
    {
        try
        {
            await Task.Delay(150, cancellation.Token);
            var mission = SelectedMission; var step = SelectedStep;
            if (mission is null || step is null || cancellation.IsCancellationRequested ||
                !string.Equals(mission.Id, missionId, StringComparison.Ordinal) ||
                !string.Equals(step.Id, stepId, StringComparison.Ordinal)) return;
            FlightMissionSurveyOptions? survey = step.Survey;
            FlightMissionCorridorOptions? corridor = step.Corridor;
            double? loiter = step.LoiterDurationSeconds;
            FlightMissionCameraIntent? camera = step.CameraIntent;
            if (step.Kind == FlightMissionStepKind.SurveyZone)
                survey = new(SurveySpacing, SurveyBearing, SurveyTurnaround, SurveyReverseEntry,
                    BuildAutomaticPhotoCaptureIntent(CameraIntentForStep(step)?.Actions, automaticPhotoCaptureEnabled, cameraDistance, cameraInterval));
            else if (step.Kind == FlightMissionStepKind.CorridorScan)
                corridor = new(CorridorWidth, CorridorSpacing, CorridorTurnaround, CorridorReverseDirection, CorridorEntrySide, CorridorFrontLap, CorridorSideLap, CorridorImagesInTurnarounds,
                    BuildAutomaticPhotoCaptureIntent(CameraIntentForStep(step)?.Actions, automaticPhotoCaptureEnabled, cameraDistance, cameraInterval));
            else if (step.Kind == FlightMissionStepKind.TimedLoiter)
                loiter = LoiterSeconds;
            await _workflow.SetStepOptionsAsync(mission.Id, step.Id, survey, corridor, loiter, camera, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { Status = $"Step settings could not be saved: {exception.Message}"; }
        finally
        {
            if (ReferenceEquals(_stepOptionsCancellation, cancellation))
            {
                _stepOptionsCancellation = null;
                _stepOptionsMissionId = null;
                _stepOptionsStepId = null;
                cancellation.Dispose();
            }
        }
    }

    private void CancelStepOptionsUpdate()
    {
        _stepOptionsCancellation?.Cancel();
        _stepOptionsCancellation?.Dispose();
        _stepOptionsCancellation = null;
        _stepOptionsMissionId = null;
        _stepOptionsStepId = null;
    }

    private void RaiseStepOptionVisibility()
    {
        OnPropertyChanged(nameof(ShowSurveyOptions)); OnPropertyChanged(nameof(ShowCorridorOptions)); OnPropertyChanged(nameof(ShowLoiterOptions)); OnPropertyChanged(nameof(ShowCameraOptions)); OnPropertyChanged(nameof(ShowStepGeometrySelector)); OnPropertyChanged(nameof(ShowTakeoffAltitude)); OnPropertyChanged(nameof(ShowStepOverrides)); OnPropertyChanged(nameof(HasSelectedStepCameraActions)); OnPropertyChanged(nameof(HasNoSelectedStepCameraActions)); OnPropertyChanged(nameof(CameraPlanSummary));
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(EndActionOptions));
        OnPropertyChanged(nameof(EndActionDisplay));
        OnPropertyChanged(nameof(MissionNewButtonLabel));
        OnPropertyChanged(nameof(MissionNameActionLabel));
        OnPropertyChanged(nameof(CameraActionKinds));
        OnPropertyChanged(nameof(CameraActionModes));
        OnPropertyChanged(nameof(CameraActionGimbalFrames));
        OnPropertyChanged(nameof(CaptureStatisticsSummary));
        OnPropertyChanged(nameof(SelectedMission));
    }

    public void Dispose() { _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _stepOptionsCancellation?.Cancel(); _stepOptionsCancellation?.Dispose(); _stepGeometryCancellation?.Cancel(); _stepGeometryCancellation?.Dispose(); _workflow.Changed -= OnChanged; _geometry.Changed -= OnChanged; _units.Changed -= OnChanged; _fences.Changed -= OnChanged; _reviewed.Changed -= OnChanged; _operatorLocation.Changed -= OnChanged; _localization.PropertyChanged -= OnLocalizationChanged; }
}
