using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Rendering;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Location;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class OperationalMapViewModel : ObservableObject, IMapNavigationController
{
    private readonly ISelectionService _selection;
    private readonly ITeamSelectionWorkflow? _teamSelection;
    private readonly IFormationLockWorkflow? _formationLock;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, GeometryOverlayRecord> _geometries;
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly IOperationalMapSceneBuilder _builder;
    private readonly IOperationalMapEngine _engine;
    private readonly IGeometryEditSession _geometryEdit;
    private readonly IGeometryWorkspaceService _geometryWorkspace;
    private readonly IGeometrySelectionWorkflow? _geometrySelection;
    private readonly IGeometryWorkflow? _geometryWorkflow;
    private readonly IFlightMissionWorkflow? _flightMissions;
    private readonly IFenceWorkflow? _fences;
    private readonly GeometryDocumentCodec? _geometryCodec;
    private readonly IVehicleTrackHistory _trackHistory;
    private readonly IMapPackageCatalog _catalog;
    private readonly ISavedMapViewRepository _savedViewRepository;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMapViewportState _viewportState;
    private readonly IMapPresentationState? _presentationState;
    private readonly IOperatorLocationService? _operatorLocation;
    private readonly IThreeDWorldSceneWorkflow? _worldScene;
    private readonly OperatorControlsViewModel? _operatorControls;
    private readonly IUnitDefinitionService? _reconciliation;
    private readonly AsyncRelayCommand _saveViewCommand;
    private readonly AsyncRelayCommand _applySavedViewCommand;
    private readonly AsyncRelayCommand _deleteSavedViewCommand;
    private readonly RelayCommand _geometryMapEditCommand;
    private readonly RelayCommand _undoGeometryEditCommand;
    private readonly RelayCommand _redoGeometryEditCommand;
    private readonly RelayCommand _removeGeometryVertexCommand;
    private readonly AsyncRelayCommand _completeGeometryEditCommand;
    private readonly RelayCommand _cancelGeometryEditCommand;
    private readonly AsyncRelayCommand _deleteActiveGeometryCommand;
    private readonly RelayCommand _beginGeometryRenameCommand;
    private readonly RelayCommand _applyGeometryRenameCommand;
    private readonly AsyncRelayCommand _newPointGeometryCommand;
    private readonly AsyncRelayCommand _newRouteGeometryCommand;
    private readonly AsyncRelayCommand _newZoneGeometryCommand;
    private readonly RelayCommand _startGeometrySelectionCommand;
    private readonly RelayCommand _selectGeometryCommand;
    private readonly AsyncRelayCommand _deleteSelectedGeometryCommand;
    private readonly RelayCommand _jumpToSelectedCommand;
    private readonly RelayCommand _followSelectedCommand;
    private readonly RelayCommand _stopFollowingCommand;
    private readonly RelayCommand _jumpToOperatorCommand;
    private readonly RelayCommand _userPannedCommand;
    private OperationalMapScene _scene = OperationalMapScene.Empty;
    private OperationalMapPresentation _presentation = OperationalMapPresentation.Empty;
    private MapVehicleMotionSnapshot _vehicleMotion = MapVehicleMotionSnapshot.Empty;
    private MapViewportMode _viewportMode = MapViewportMode.FitAll;
    private MapOrientationMode _orientationMode = MapOrientationMode.NorthUp;
    private bool _geometryVisible = true;
    private bool _policyVisible = true;
    private bool _trailsVisible = true;
    private bool _goToIndicatorsVisible = true;
    private bool _vehicleLabelsVisible = true;
    private string _summary = "Waiting for compatible vehicle or geometry coordinates.";
    private string _savedViewName = string.Empty;
    private string _savedViewStatus = "Move the global map to the desired position, then save a view.";
    private MapStyleOption? _selectedStyle;
    private ThreeDSceneSnapshot? _worldSceneSnapshot;
    private SavedMapView? _selectedSavedView;
    private MapViewportSnapshot? _lastViewport;
    private MapViewportSnapshot? _requestedViewport;
    private MapNavigationRequest? _navigationRequest;
    private MapFollowState? _follow;
    private bool _startupViewportPending = true;
    private long _navigationRevision = 1;
    private bool _refreshingStyles;
    private string _geometryDefaultAltitudeText = "20";
    private string _geometryRenameText = string.Empty;
    private bool _geometryRenameVisible;
    private bool _geometrySelectionEnabled;
    private GeometryDocumentKind? _activeGeometryTool;
    private CancellationTokenSource? _rebuildDebounce;
    private CancellationTokenSource? _motionDebounce;
    private DateTimeOffset _lastTrailMotionAt = DateTimeOffset.MinValue;
    private string _vehicleStructureKey = string.Empty;

    public OperationalMapViewModel(
        ISelectionService selection,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, GeometryOverlayRecord> geometries,
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks,
        IOperationalMapSceneBuilder builder,
        IOperationalMapEngine engine,
        IGeometryEditSession geometryEdit,
        IGeometryWorkspaceService geometryWorkspace,
        IVehicleTrackHistory trackHistory,
        IMapPackageCatalog catalog,
        ISavedMapViewRepository savedViewRepository,
        IUiDispatcher dispatcher,
        IMapViewportState? viewportState = null,
        IMapPresentationState? presentationState = null,
        OperatorControlsViewModel? operatorControls = null,
        IOperatorLocationService? operatorLocation = null,
        IUnitDefinitionService? reconciliation = null,
        IGeometrySelectionWorkflow? geometrySelection = null,
        IGeometryWorkflow? geometryWorkflow = null,
        GeometryDocumentCodec? geometryCodec = null,
        IFlightMissionWorkflow? flightMissions = null,
        IFenceWorkflow? px4Fences = null,
        ITeamSelectionWorkflow? teamSelection = null,
        IFormationLockWorkflow? formationLock = null,
        IThreeDWorldSceneWorkflow? worldScene = null)
    {
        _selection = selection;
        _teamSelection = teamSelection;
        _formationLock = formationLock;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _geometries = geometries;
        _missions = missions;
        _tasks = tasks;
        _builder = builder;
        _engine = engine;
        _geometryEdit = geometryEdit;
        _geometryWorkspace = geometryWorkspace;
        _geometrySelection = geometrySelection;
        _geometryWorkflow = geometryWorkflow;
        _geometryCodec = geometryCodec;
        _flightMissions = flightMissions;
        _fences = px4Fences;
        _trackHistory = trackHistory;
        _catalog = catalog;
        _savedViewRepository = savedViewRepository;
        _dispatcher = dispatcher;
        _viewportState = viewportState ?? new MapViewportState();
        _presentationState = presentationState;
        _operatorLocation = operatorLocation;
        _worldScene = worldScene;
        _worldSceneSnapshot = worldScene?.Current;
        // The startup viewport is a real camera state, not an absence of
        // state. Keeping it from the beginning prevents a freshly recreated
        // map control from replacing the operator's view with Mapsui's world
        // placeholder when a workspace or the last local geometry disappears.
        _lastViewport = _viewportState.Current ?? _engine.StartupViewport;
        if (_viewportState.Current is null)
        {
            _viewportState.Update(_lastViewport);
        }
        _startupViewportPending = false;
        _operatorControls = operatorControls;
        _reconciliation = reconciliation;
        Styles = [];
        SavedViews = [];

        _engine.Changed += OnEngineChanged;
        _geometryEdit.Changed += OnGeometryEditChanged;
        _geometryWorkspace.Changed += OnDataChanged;
        if (_flightMissions is not null) _flightMissions.Changed += OnDataChanged;
        if (_fences is not null) _fences.Changed += OnDataChanged;
        if (_geometrySelection is not null) _geometrySelection.Changed += OnGeometrySelectionChanged;
        _savedViewRepository.Changed += OnSavedViewsChanged;
        if (_operatorLocation is not null)
        {
            _operatorLocation.Changed += OnOperatorLocationChanged;
        }
        if (_worldScene is not null) _worldScene.Changed += OnWorldSceneChanged;
        _selection.Changed += OnSelectionChanged;
        if (_teamSelection is not null) _teamSelection.Changed += OnTeamSelectionChanged;
        if (_formationLock is not null) _formationLock.Changed += OnFormationLockChanged;
        if (_reconciliation is not null) _reconciliation.Changed += OnDataChanged;
        if (_operatorControls is not null)
        {
            _operatorControls.ActiveGoToTargetChanged += OnActiveGoToTargetChanged;
            _operatorControls.FormationPreviewChanged += OnFormationPreviewChanged;
            _operatorControls.QueuedCommandsChanged += OnQueuedCommandsChanged;
        }
        Subscribe(_vehicles.Items, OnVehicleChanged);
        Subscribe(_telemetry.Items, OnTelemetryChanged);
        Subscribe(_geometries.Items);
        Subscribe(_missions.Items);
        Subscribe(_tasks.Items);

        FitAllCommand = new RelayCommand(_ =>
        {
            if (IsThreeD) _ = FitWorldSceneAsync();
            else SetViewport(MapViewportMode.FitAll);
        });
        _jumpToSelectedCommand = new RelayCommand(_ => JumpToSelected(), _ => CanNavigateSelection);
        _followSelectedCommand = new RelayCommand(_ => FollowSelected(), _ => CanNavigateSelection);
        _stopFollowingCommand = new RelayCommand(_ => StopFollowing(), _ => IsFollowing);
        _jumpToOperatorCommand = new RelayCommand(_ => JumpToOperator(), _ => CanJumpToOperator);
        _userPannedCommand = new RelayCommand(_ => UserPanned());
        FollowSelectedCommand = _followSelectedCommand;
        ToggleGeometryCommand = new RelayCommand(_ =>
        {
            GeometryVisible = !GeometryVisible;
            Rebuild();
        });
        TogglePolicyCommand = new RelayCommand(_ =>
        {
            PolicyVisible = !PolicyVisible;
            Rebuild();
        });
        ToggleTrailsCommand = new RelayCommand(_ =>
        {
            TrailsVisible = !TrailsVisible;
            Rebuild();
        });
        ToggleGoToIndicatorsCommand = new RelayCommand(_ =>
        {
            GoToIndicatorsVisible = !GoToIndicatorsVisible;
            Rebuild();
        });
        ToggleVehicleLabelsCommand = new RelayCommand(_ =>
        {
            VehicleLabelsVisible = !VehicleLabelsVisible;
            Rebuild();
        });
        ToggleOrientationCommand = new RelayCommand(_ =>
        {
            _requestedViewport = null;
            OrientationMode = OrientationMode == MapOrientationMode.NorthUp
                ? MapOrientationMode.CourseUp
                : MapOrientationMode.NorthUp;
            Rebuild();
        });
        ResetNorthCommand = new RelayCommand(_ =>
        {
            _requestedViewport = null;
            OrientationMode = MapOrientationMode.NorthUp;
            Rebuild();
        });
        ToggleWorldCameraModeCommand = new RelayCommand(_ => ToggleWorldCameraMode(), _ => IsThreeD);
        ResetWorldCameraCommand = new AsyncRelayCommand(ResetWorldCameraAsync, () => IsThreeD);
        FitWorldSceneCommand = new AsyncRelayCommand(FitWorldSceneAsync, () => IsThreeD);
        ClearTrailsCommand = new RelayCommand(_ =>
        {
            var selectedVehicleId = SelectedVehicleId();
            _trackHistory.Clear(selectedVehicleId);
            Rebuild();
        });
        SelectVehicleCommand = new RelayCommand(SelectVehicle);
        UpdateViewportCommand = new RelayCommand(UpdateViewport);
        _saveViewCommand = new AsyncRelayCommand(SaveViewAsync, CanSaveView);
        _applySavedViewCommand = new AsyncRelayCommand(ApplySavedViewAsync, () => SelectedSavedView is not null);
        _deleteSavedViewCommand = new AsyncRelayCommand(DeleteSavedViewAsync, () => SelectedSavedView is not null);
        _geometryMapEditCommand = new RelayCommand(ApplyGeometryMapEdit, _ => GeometryEdit.IsEditing);
        _undoGeometryEditCommand = new RelayCommand(_ => _geometryEdit.Undo(), _ => GeometryEdit.CanUndo);
        _redoGeometryEditCommand = new RelayCommand(_ => _geometryEdit.Redo(), _ => GeometryEdit.CanRedo);
        _removeGeometryVertexCommand = new RelayCommand(
            _ => _geometryEdit.RemoveVertex(),
            _ => GeometryEdit.IsEditing && GeometryEdit.SelectedVertexIndex is not null);
        _completeGeometryEditCommand = new AsyncRelayCommand(
            CompleteGeometryEditAsync,
            () => GeometryEdit.CanComplete);
        _cancelGeometryEditCommand = new RelayCommand(
            _ => _geometryEdit.Cancel(),
            _ => GeometryEdit.HasDraft);
        _deleteActiveGeometryCommand = new AsyncRelayCommand(
            DeleteActiveGeometryAsync,
            () => GeometryEdit.HasDraft);
        _beginGeometryRenameCommand = new RelayCommand(_ => BeginGeometryRename(), _ => GeometryEdit.HasDraft);
        _applyGeometryRenameCommand = new RelayCommand(_ => ApplyGeometryRename(), _ => CanApplyGeometryRename());
        _newPointGeometryCommand = new AsyncRelayCommand(
            token => BeginNewGeometryAsync(GeometryDocumentKind.PointOfInterest, token));
        _newRouteGeometryCommand = new AsyncRelayCommand(
            token => BeginNewGeometryAsync(GeometryDocumentKind.WaypointSequence, token));
        _newZoneGeometryCommand = new AsyncRelayCommand(
            token => BeginNewGeometryAsync(GeometryDocumentKind.Zone, token));
        _startGeometrySelectionCommand = new RelayCommand(_ => StartGeometrySelection());
        _selectGeometryCommand = new RelayCommand(SelectGeometry);
        _deleteSelectedGeometryCommand = new AsyncRelayCommand(
            ConfirmDeleteSelectedGeometryAsync,
            () => _geometrySelection?.Current.HasSelection == true);
        SaveViewCommand = _saveViewCommand;
        ApplySavedViewCommand = _applySavedViewCommand;
        DeleteSavedViewCommand = _deleteSavedViewCommand;
        GeometryMapEditCommand = _geometryMapEditCommand;
        UndoGeometryEditCommand = _undoGeometryEditCommand;
        RedoGeometryEditCommand = _redoGeometryEditCommand;
        RemoveGeometryVertexCommand = _removeGeometryVertexCommand;
        CompleteGeometryEditCommand = _completeGeometryEditCommand;
        CancelGeometryEditCommand = _cancelGeometryEditCommand;
        DeleteActiveGeometryCommand = _deleteActiveGeometryCommand;
        BeginGeometryRenameCommand = _beginGeometryRenameCommand;
        ApplyGeometryRenameCommand = _applyGeometryRenameCommand;
        NewPointGeometryCommand = _newPointGeometryCommand;
        NewRouteGeometryCommand = _newRouteGeometryCommand;
        NewZoneGeometryCommand = _newZoneGeometryCommand;
        StartGeometrySelectionCommand = _startGeometrySelectionCommand;
        SelectGeometryCommand = _selectGeometryCommand;
        DeleteSelectedGeometryCommand = _deleteSelectedGeometryCommand;

        RefreshStyles();
        RefreshSavedViews();
        Rebuild();
    }

    public ObservableCollection<MapStyleOption> Styles { get; }

    public ObservableCollection<SavedMapView> SavedViews { get; }

    public ThreeDSceneSnapshot? WorldScene
    {
        get => _worldSceneSnapshot;
        private set => SetProperty(ref _worldSceneSnapshot, value);
    }

    public bool IsThreeD => SelectedStyle?.IsThreeD == true;

    public string WorldCameraModeLabel => WorldScene?.Camera.OrbitMode == true ? "Orbit" : "Free";

    public Task SetWorldCameraAsync(ThreeDCameraSnapshot camera)
        => _worldScene is null ? Task.CompletedTask : _worldScene.SetCameraAsync(camera);

    public Task FitWorldSceneAsync()
        => FitWorldSceneAsync(CancellationToken.None);

    public ICommand ToggleWorldCameraModeCommand { get; }
    public ICommand ResetWorldCameraCommand { get; }
    public ICommand FitWorldSceneCommand { get; }

    public OperationalMapScene Scene
    {
        get => _scene;
        private set => SetProperty(ref _scene, value);
    }

    public OperationalMapPresentation Presentation
    {
        get => _presentation;
        private set => SetProperty(ref _presentation, value);
    }

    public MapVehicleMotionSnapshot VehicleMotion
    {
        get => _vehicleMotion;
        private set => SetProperty(ref _vehicleMotion, value);
    }

    public MapStyleOption? SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            // ComboBox briefly reports null while its item collection is
            // being refreshed. Keep the current style instead of allowing
            // that transient state to blank the selector.
            if (value is null && (_refreshingStyles || Styles.Count > 0))
            {
                return;
            }

            if (!SetProperty(ref _selectedStyle, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedStyleId));
            OnPropertyChanged(nameof(IsThreeD));
            OnPropertyChanged(nameof(WorldCameraModeLabel));
            if (value is null || _refreshingStyles)
            {
                return;
            }

            _ = SelectStyleAsync(value);
        }
    }

    public string? SelectedStyleId
    {
        get => SelectedStyle?.StyleId;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var style = Styles.FirstOrDefault(item =>
                string.Equals(item.StyleId, value, StringComparison.OrdinalIgnoreCase));
            if (style is null || string.Equals(SelectedStyle?.StyleId, style.StyleId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedStyle = style;
        }
    }

    public SavedMapView? SelectedSavedView
    {
        get => _selectedSavedView;
        set
        {
            if (!SetProperty(ref _selectedSavedView, value))
            {
                return;
            }

            if (value is not null)
            {
                SavedViewName = value.Name;
            }

            RaiseSavedViewCommandStates();
        }
    }

    public string SavedViewName
    {
        get => _savedViewName;
        set
        {
            if (SetProperty(ref _savedViewName, value))
            {
                _saveViewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SavedViewStatus
    {
        get => _savedViewStatus;
        private set => SetProperty(ref _savedViewStatus, value);
    }

    public bool GeometryVisible
    {
        get => _geometryVisible;
        private set
        {
            if (SetProperty(ref _geometryVisible, value))
            {
                OnPropertyChanged(nameof(GeometryButtonLabel));
            }
        }
    }

    public bool PolicyVisible
    {
        get => _policyVisible;
        private set
        {
            if (SetProperty(ref _policyVisible, value))
            {
                OnPropertyChanged(nameof(PolicyButtonLabel));
            }
        }
    }

    public bool TrailsVisible
    {
        get => _trailsVisible;
        private set
        {
            if (SetProperty(ref _trailsVisible, value))
            {
                OnPropertyChanged(nameof(TrailsButtonLabel));
            }
        }
    }

    public bool GoToIndicatorsVisible
    {
        get => _goToIndicatorsVisible;
        private set
        {
            if (SetProperty(ref _goToIndicatorsVisible, value))
                OnPropertyChanged(nameof(GoToIndicatorsButtonLabel));
        }
    }

    public MapViewportSnapshot? CurrentViewport
        => _lastViewport ?? _viewportState.Current;

    public bool VehicleLabelsVisible
    {
        get => _vehicleLabelsVisible;
        private set
        {
            if (SetProperty(ref _vehicleLabelsVisible, value))
            {
                OnPropertyChanged(nameof(VehicleLabelsButtonLabel));
            }
        }
    }

    public MapOrientationMode OrientationMode
    {
        get => _orientationMode;
        private set
        {
            if (SetProperty(ref _orientationMode, value))
            {
                OnPropertyChanged(nameof(OrientationButtonLabel));
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string FrameLabel => Presentation.Scene.FrameLabel;

    public bool IsEmpty => Presentation.Renderer == OperationalMapRendererKind.Empty;

    public bool GlobalMapControlsAvailable => !IsThreeD && Presentation.Renderer == OperationalMapRendererKind.NativeGlobal;

    public bool OverlayControlsAvailable => !IsThreeD && Scene.Frame != MapFrameKind.Unknown;

    public bool MultipleStylesAvailable => Styles.Count > 1;

    public bool SavedViewsAvailable => SavedViews.Count > 0;

    public string StyleLabel => Presentation.StyleLabel;

    public string ViewportLabel => IsFollowing
        ? $"FOLLOW {FollowLabel.ToUpperInvariant()}"
        : "FIT ALL";

    public bool IsFollowing => _follow is not null;

    public string FollowLabel => _follow?.Label ?? "selected";

    public bool CanNavigateSelection => TryGetSelectedPositions(out _);

    public bool CanJumpToOperator => _operatorLocation?.Snapshot.IsAvailable == true &&
                                     _operatorLocation.Snapshot.LatitudeDegrees is not null &&
                                     _operatorLocation.Snapshot.LongitudeDegrees is not null;

    public string OperatorNavigationLabel => CanJumpToOperator
        ? "Jump to operator"
        : "Operator Location Unavailable";

    public string GeometryButtonLabel => GeometryVisible ? "Hide geometries" : "Show geometries";

    public string PolicyButtonLabel => PolicyVisible ? "Hide policy" : "Show policy";

    public string TrailsButtonLabel => TrailsVisible ? "Hide trails" : "Show trails";

    public string GoToIndicatorsButtonLabel => GoToIndicatorsVisible ? "Hide Go To" : "Show Go To";

    public string VehicleLabelsButtonLabel => VehicleLabelsVisible ? "Hide labels" : "Show labels";

    public string OrientationButtonLabel => OrientationMode == MapOrientationMode.NorthUp
        ? "North up"
        : "Course up";

    public GeometryEditSnapshot GeometryEdit => _geometryEdit.Snapshot;

    public bool HasGeometryDraft => GeometryEdit.HasDraft;

    public bool IsGeometryEditing => GeometryEdit.IsEditing;

    public string GeometryEditModeLabel => GeometryEdit.InteractionMode switch
    {
        MapInteractionMode.CreatePoint => "CREATE POI",
        MapInteractionMode.CreatePath => "CREATE ROUTE",
        MapInteractionMode.CreateZone => "CREATE ZONE",
        MapInteractionMode.EditGeometry => "EDIT GEOMETRY",
        _ => GeometryEdit.IsCompleted ? "DRAFT READY" : "MAP"
    };

    public string GeometryEditStatus => GeometryEdit.Status;

    public string GeometryDefaultAltitudeText
    {
        get => _geometryDefaultAltitudeText;
        set => SetProperty(ref _geometryDefaultAltitudeText, value);
    }

    public string GeometryRenameText
    {
        get => _geometryRenameText;
        set
        {
            if (SetProperty(ref _geometryRenameText, value))
            {
                _applyGeometryRenameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool GeometryRenameVisible
    {
        get => _geometryRenameVisible;
        private set => SetProperty(ref _geometryRenameVisible, value);
    }

    /// <summary>
    /// Geometry selection is an explicit map tool. Keeping it separate from
    /// the edit session means an empty-map click can reliably clear geometry
    /// selection without accidentally selecting a unit behind the geometry.
    /// </summary>
    public bool GeometrySelectionEnabled
    {
        get => _geometrySelectionEnabled;
        private set => SetProperty(ref _geometrySelectionEnabled, value);
    }

    public string GeometryAltitudeUnitLabel => "m AGL";

    public ICommand FitAllCommand { get; }

    public ICommand FollowSelectedCommand { get; }

    public ICommand JumpToSelectedCommand => _jumpToSelectedCommand;

    public ICommand StopFollowingCommand => _stopFollowingCommand;

    public ICommand JumpToOperatorCommand => _jumpToOperatorCommand;

    public ICommand UserPannedCommand => _userPannedCommand;

    public event EventHandler? Changed;

    public ICommand ToggleGeometryCommand { get; }

    public ICommand TogglePolicyCommand { get; }

    public ICommand ToggleTrailsCommand { get; }

    public ICommand ToggleGoToIndicatorsCommand { get; }


    public ICommand ToggleVehicleLabelsCommand { get; }

    public ICommand ToggleOrientationCommand { get; }

    public ICommand ResetNorthCommand { get; }

    public ICommand ClearTrailsCommand { get; }

    public ICommand SelectVehicleCommand { get; }

    public ICommand UpdateViewportCommand { get; }

    public ICommand SaveViewCommand { get; }

    public ICommand ApplySavedViewCommand { get; }

    public ICommand DeleteSavedViewCommand { get; }

    public ICommand GeometryMapEditCommand { get; }

    public ICommand UndoGeometryEditCommand { get; }

    public ICommand RedoGeometryEditCommand { get; }

    public ICommand RemoveGeometryVertexCommand { get; }

    public ICommand CompleteGeometryEditCommand { get; }

    public ICommand CancelGeometryEditCommand { get; }

    public ICommand DeleteActiveGeometryCommand { get; }

    public ICommand BeginGeometryRenameCommand { get; }

    public ICommand ApplyGeometryRenameCommand { get; }

    public ICommand NewPointGeometryCommand { get; }

    public ICommand NewRouteGeometryCommand { get; }

    public ICommand NewZoneGeometryCommand { get; }

    public ICommand StartGeometrySelectionCommand { get; }

    public ICommand SelectGeometryCommand { get; }

    public ICommand DeleteSelectedGeometryCommand { get; }

    public IReadOnlyList<string> SelectedGeometryIds => _geometrySelection?.Current.GeometryIds ?? [];

    private void Subscribe(System.Collections.IEnumerable collection, NotifyCollectionChangedEventHandler? handler = null)
        => ((INotifyCollectionChanged)collection).CollectionChanged += handler ?? OnDataChanged;

    private void OnTelemetryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleMotionUpdate();
    }

    private void OnVehicleChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnVehicleChanged(sender, e));
            return;
        }

        // Connection managers periodically replace the vehicle projection to
        // refresh availability. Identity and map membership are unchanged in
        // that case, so do not rebuild the scene or map layers.
        var structureKey = CreateVehicleStructureKey(_vehicles.Items);
        if (string.Equals(_vehicleStructureKey, structureKey, StringComparison.Ordinal))
        {
            return;
        }

        _vehicleStructureKey = structureKey;
        OnDataChanged(sender, e);
    }

    private static string CreateVehicleStructureKey(IEnumerable<VehicleRecord> vehicles)
        => string.Join("|", vehicles
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => string.Join("\u001f", [
                item.Id,
                item.Name,
                string.Join(",", item.ConnectionIds.OrderBy(id => id, StringComparer.Ordinal)),
                item.LogosInstanceId ?? string.Empty,
                item.TeamId ?? string.Empty,
                item.VehicleClass,
                item.Domain,
                item.ProfileKey,
                item.IsGhost.ToString()
            ])));

    private void ProcessLatestTelemetryMotion()
    {
        if (_scene.Frame == MapFrameKind.Unknown)
        {
            ScheduleRebuild();
            return;
        }

        _trackHistory.Record(_telemetry.Items, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var refreshTrails = TrailsVisible && (now - _lastTrailMotionAt) >= TimeSpan.FromMilliseconds(100);
        if (refreshTrails)
            _lastTrailMotionAt = now;
        var motion = BuildMotionSnapshot(includeTrails: refreshTrails);
        if (RequiresStructuralMotionRebuild(motion))
        {
            ScheduleRebuild();
            return;
        }

        PublishMotion(motion);
    }

    private void ScheduleMotionUpdate()
    {
        _motionDebounce?.Cancel();
        var cancellation = _motionDebounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(8, cancellation.Token);
                await _dispatcher.InvokeAsync(ProcessLatestTelemetryMotion, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer telemetry publication superseded this motion update.
            }
        }, cancellation.Token);
    }

    private MapVehicleMotionSnapshot BuildMotionSnapshot(bool includeTrails)
    {
        var motion = _builder.BuildMotion(
            _vehicles.Items,
            _telemetry.Items,
            _scene.Frame,
            DateTimeOffset.UtcNow);
        if (!includeTrails || _scene.Frame != MapFrameKind.GlobalWgs84)
            return motion;

        return motion with
        {
            Trails = _trackHistory.BuildTrailsForSelectedVehicles(
                _reconciliation?.ProjectVehicles(_vehicles.Items) ?? _vehicles.Items,
                ActiveUnitSelectionIds().ToHashSet(StringComparer.Ordinal)),
            TrailsUpdated = true
        };
    }

    private bool RequiresStructuralMotionRebuild(MapVehicleMotionSnapshot motion)
    {
        var sceneIds = _scene.Vehicles.Select(vehicle => vehicle.VehicleId).ToHashSet(StringComparer.Ordinal);
        var motionIds = motion.Vehicles.Select(vehicle => vehicle.VehicleId).ToHashSet(StringComparer.Ordinal);
        return motion.Frame != _scene.Frame || !sceneIds.SetEquals(motionIds);
    }

    private void PublishMotion(MapVehicleMotionSnapshot motion)
    {
        VehicleMotion = motion;
        _presentationState?.UpdateMotion(motion);
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnDataChanged(sender, e));
            return;
        }

        RaiseNavigationStateChanged();
        ScheduleRebuild();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnSelectionChanged(sender, e));
            return;
        }

        // Unit selection is exclusive with local geometry selection. This is
        // raised for list, map, and CLI-driven selection alike rather than
        // relying on any one GUI control to clear geometry state.
        if (_selection.SelectedUnitIds.Count > 0 || _selection.Current.Kind == SelectionKind.Vehicle)
        {
            GeometrySelectionEnabled = false;
            _activeGeometryTool = null;
            if (GeometryEdit.HasDraft)
            {
                _geometryEdit.Cancel();
            }
            _geometrySelection?.Clear();
            _deleteSelectedGeometryCommand.RaiseCanExecuteChanged();
        }

        OnDataChanged(sender, e);
    }

    private void OnGeometrySelectionChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnGeometrySelectionChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(SelectedGeometryIds));
        _deleteSelectedGeometryCommand.RaiseCanExecuteChanged();
        OnDataChanged(sender, e);
    }

    private void OnDataChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnDataChanged(sender, (EventArgs)e);

    private void OnOperatorLocationChanged(object? sender, EventArgs e)
    {
        // WinRT location callbacks are not guaranteed to arrive on Avalonia's
        // UI thread. Update both command state and the rendered snapshot on
        // the dispatcher so the toolbar button cannot remain bound to an old
        // unavailable state after Windows has supplied a valid fix.
        if (_dispatcher.CheckAccess())
        {
            RaiseNavigationStateChanged();
            Rebuild();
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            RaiseNavigationStateChanged();
            Rebuild();
        });
    }

    private void ScheduleRebuild()
    {
        _rebuildDebounce?.Cancel();
        var cancellation = _rebuildDebounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(16, cancellation.Token);
                await _dispatcher.InvokeAsync(Rebuild, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer telemetry batch superseded this render request.
            }
        }, cancellation.Token);
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        RefreshStyles();
        Rebuild();
    }

    private void OnGeometryEditChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            RefreshGeometryEditPresentation();
            return;
        }

        _ = _dispatcher.InvokeAsync(RefreshGeometryEditPresentation);
    }

    private void RefreshGeometryEditPresentation()
    {
        Rebuild();
        OnPropertyChanged(nameof(GeometryEdit));
        OnPropertyChanged(nameof(HasGeometryDraft));
        OnPropertyChanged(nameof(IsGeometryEditing));
        OnPropertyChanged(nameof(GeometryEditModeLabel));
        OnPropertyChanged(nameof(GeometryEditStatus));
        RaiseGeometryEditCommandStates();
    }

    private void ApplyGeometryMapEdit(object? parameter)
    {
        if (parameter is not GeometryMapEditRequest request)
        {
            return;
        }

        switch (request.Action)
        {
            case GeometryMapEditAction.AddVertex when request.Point is { } addPoint:
                AddGeometryVertex(addPoint);
                break;
            case GeometryMapEditAction.SelectVertex:
                _geometryEdit.SelectVertex(request.VertexIndex);
                break;
            case GeometryMapEditAction.MoveVertex when request.Point is { } movePoint && request.VertexIndex is { } moveIndex:
                var currentVertices = GeometryEdit.Vertices;
                if (moveIndex >= 0 && moveIndex < currentVertices.Count)
                {
                    _geometryEdit.MoveVertex(
                        moveIndex,
                        movePoint with { Z = currentVertices[moveIndex].Z });
                }
                break;
            case GeometryMapEditAction.InsertVertex when request.Point is { } insertPoint && request.VertexIndex is { } insertIndex:
                _geometryEdit.InsertVertex(insertIndex, insertPoint with { Z = DefaultGeometryAltitudeMetres() });
                break;
            case GeometryMapEditAction.RemoveVertex when request.VertexIndex is not null || GeometryEdit.SelectedVertexIndex is not null:
                _geometryEdit.RemoveVertex(request.VertexIndex);
                break;
        }
    }

    private async Task BeginNewGeometryAsync(
        GeometryDocumentKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            GeometrySelectionEnabled = false;

            // A selected route or zone is the current authoring target for
            // its matching tool. PoIs deliberately always start a fresh
            // point-stamp draft so every click creates a new saved PoI.
            if ((kind is GeometryDocumentKind.WaypointSequence or GeometryDocumentKind.Zone) &&
                TryGetSelectedGeometry(kind, out var selected))
            {
                if (GeometryEdit.HasDraft)
                {
                    _geometryEdit.Cancel();
                }

                _activeGeometryTool = kind;
                _selection.Clear();
                _geometryEdit.BeginEdit(selected);
                Summary = $"Continuing {kind} '{selected.DisplayName}'.";
                Rebuild();
                return;
            }

            if (GeometryEdit.HasDraft)
            {
                _geometryEdit.Cancel();
            }

            var document = await _geometryWorkspace.CreateLocalDraftAsync(kind, cancellationToken: cancellationToken);
            _selection.Clear();
            _geometrySelection?.Clear();
            _activeGeometryTool = kind;
            GeometryRenameText = document.DisplayName;
            GeometryRenameVisible = false;
            _geometryEdit.BeginCreate(document);
            Summary = $"Creating {kind} '{document.DisplayName}'. Click the map to place geometry.";
            Rebuild();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = $"Could not start geometry creation: {ex.Message}";
        }
    }

    private void StartGeometrySelection()
    {
        GeometrySelectionEnabled = !GeometrySelectionEnabled;

        if (GeometrySelectionEnabled)
        {
            _activeGeometryTool = null;
            if (GeometryEdit.HasDraft)
            {
                _geometryEdit.Cancel();
            }

            _selection.Clear();
            Summary = "Select geometry on the map. Click empty map space to clear selection.";
        }
        else
        {
            Summary = "Geometry selection is off.";
        }

        Rebuild();
    }

    private async Task CompleteGeometryEditAsync(CancellationToken cancellationToken)
    {
        if (!GeometryEdit.CanComplete)
        {
            return;
        }

        GeometryDocument completed;
        try
        {
            completed = _geometryEdit.Complete() with
            {
                AltitudeReference = GeometryAltitudeReference.AboveGroundLevel
            };

            if (_geometryWorkflow is not null && _geometryCodec is not null)
            {
                await _geometryWorkflow.SaveAsync(
                    new GeometrySaveWorkflowRequest(_geometryCodec.Serialize(completed)),
                    cancellationToken);
            }
            else
            {
                await _geometryWorkspace.SaveLocalAsync(completed, cancellationToken);
            }

            _geometryEdit.Clear();
            _activeGeometryTool = null;
            GeometryRenameVisible = false;
            GeometrySelectionEnabled = true;
            _geometrySelection?.Set([completed.GeometryId], completed.GeometryId);
            Summary = $"Saved geometry '{completed.DisplayName}'.";
            Rebuild();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Complete leaves the finished draft visible, so a temporary
            // storage failure never throws away the operator's geometry.
            Summary = $"Could not save geometry: {ex.Message}";
            Rebuild();
        }
    }

    private void BeginGeometryRename()
    {
        if (GeometryEdit.Draft is not { } draft)
        {
            return;
        }

        GeometryRenameText = draft.DisplayName;
        GeometryRenameVisible = true;
    }

    private bool CanApplyGeometryRename()
        => GeometryEdit.HasDraft && !string.IsNullOrWhiteSpace(GeometryRenameText);

    private void ApplyGeometryRename()
    {
        if (!CanApplyGeometryRename())
        {
            return;
        }

        try
        {
            _geometryEdit.Rename(GeometryRenameText);
            GeometryRenameVisible = false;
        }
        catch (Exception ex)
        {
            Summary = $"Could not rename geometry: {ex.Message}";
        }
    }

    private void AddGeometryVertex(GeometryDocumentPoint point)
    {
        _geometryEdit.AddVertex(point with { Z = DefaultGeometryAltitudeMetres() });
    }

    private async Task DeleteActiveGeometryAsync(CancellationToken cancellationToken)
    {
        var draft = GeometryEdit.Draft;
        if (draft is null)
        {
            return;
        }

        try
        {
            if (_geometryWorkspace.LocalDocuments.Any(item => item.GeometryId == draft.GeometryId))
            {
                if (_geometryWorkflow is not null)
                {
                    await _geometryWorkflow.RemoveAsync(draft.GeometryId, cancellationToken);
                }
                else
                {
                    await _geometryWorkspace.RemoveLocalAsync(draft.GeometryId, cancellationToken);
                }
            }

            _geometryEdit.Clear();
            _activeGeometryTool = null;
            GeometryRenameVisible = false;
            Summary = $"Deleted geometry '{draft.DisplayName}'.";
            Rebuild();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = $"Could not delete geometry: {ex.Message}";
        }
    }

    private void RaiseGeometryEditCommandStates()
    {
        _geometryMapEditCommand.RaiseCanExecuteChanged();
        _undoGeometryEditCommand.RaiseCanExecuteChanged();
        _redoGeometryEditCommand.RaiseCanExecuteChanged();
        _removeGeometryVertexCommand.RaiseCanExecuteChanged();
        _completeGeometryEditCommand.RaiseCanExecuteChanged();
        _cancelGeometryEditCommand.RaiseCanExecuteChanged();
        _deleteActiveGeometryCommand.RaiseCanExecuteChanged();
        _beginGeometryRenameCommand.RaiseCanExecuteChanged();
        _applyGeometryRenameCommand.RaiseCanExecuteChanged();
    }

    private async Task ConfirmDeleteSelectedGeometryAsync(CancellationToken cancellationToken)
    {
        var selected = _geometrySelection?.Current.GeometryIds.ToArray() ?? [];
        if (selected.Length == 0)
        {
            return;
        }

        try
        {
            foreach (var geometryId in selected)
            {
                if (_geometryWorkflow is not null)
                {
                    await _geometryWorkflow.RemoveAsync(geometryId, cancellationToken);
                }
                else
                {
                    await _geometryWorkspace.RemoveLocalAsync(geometryId, cancellationToken);
                }
            }

            _geometrySelection?.Clear();
            _selection.Clear();
            Summary = $"Deleted {selected.Length} local geometry object(s).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Summary = $"Could not delete geometry: {ex.Message}";
        }
        finally { Rebuild(); }
    }

    private void SelectGeometry(object? parameter)
    {
        if (parameter is null)
        {
            _geometrySelection?.Clear();
            Summary = "Geometry selection cleared.";
            Rebuild();
            return;
        }

        if (parameter is not MapGeometrySelectionRequest request ||
            !_geometryWorkspace.LocalDocuments.Any(item => item.GeometryId == request.GeometryId))
        {
            return;
        }

        var ordered = _geometryWorkspace.LocalDocuments
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
            .Select(item => item.GeometryId)
            .ToArray();
        var current = _geometrySelection?.Current.GeometryIds.ToList() ?? [];
        var anchor = _geometrySelection?.Current.AnchorGeometryId;
        IReadOnlyList<string> next;
        if (request.Range && !string.IsNullOrWhiteSpace(anchor))
        {
            var anchorIndex = Array.IndexOf(ordered, anchor);
            var targetIndex = Array.IndexOf(ordered, request.GeometryId);
            if (anchorIndex >= 0 && targetIndex >= 0)
            {
                var range = ordered[Math.Min(anchorIndex, targetIndex)..(Math.Max(anchorIndex, targetIndex) + 1)];
                next = request.Extend
                    ? current.Concat(range).Distinct(StringComparer.Ordinal).ToArray()
                    : range;
            }
            else
            {
                next = [request.GeometryId];
            }
        }
        else if (request.Extend)
        {
            if (!current.Remove(request.GeometryId)) current.Add(request.GeometryId);
            next = current;
        }
        else
        {
            next = [request.GeometryId];
        }

        _selection.Clear();
        _geometrySelection?.Set(next, anchor ?? request.GeometryId);
        Summary = next.Count == 0
            ? "Geometry selection cleared."
            : $"{next.Count} local geometry object(s) selected.";
        Rebuild();
        _deleteSelectedGeometryCommand.RaiseCanExecuteChanged();
    }

    private bool TryGetSelectedGeometry(
        GeometryDocumentKind kind,
        out GeometryDocument document)
    {
        var selectedId = _geometrySelection?.Current.GeometryIds.Count == 1
            ? _geometrySelection.Current.GeometryIds[0]
            : null;
        document = _geometryWorkspace.LocalDocuments.FirstOrDefault(
            item => item.GeometryId == selectedId && item.Kind == kind)!;
        return document is not null;
    }

    private double DefaultGeometryAltitudeMetres()
    {
        return double.TryParse(
                   GeometryDefaultAltitudeText,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var altitude) &&
               double.IsFinite(altitude) && altitude >= 0 && altitude <= 10000
            ? altitude
            : 20;
    }

    private void OnSavedViewsChanged(object? sender, EventArgs e)
        => RefreshSavedViews(SelectedSavedView?.Id);

    private void SetViewport(MapViewportMode mode)
    {
        _requestedViewport = null;
        _navigationRequest = null;
        _follow = null;
        if (_viewportMode != mode)
        {
            _viewportMode = mode;
            OnPropertyChanged(nameof(ViewportLabel));
        }

        _navigationRevision++;
        RaiseNavigationStateChanged();
        Rebuild();
    }

    public void JumpToSelected()
    {
        if (!TryGetSelectedPositions(out var positions))
        {
            return;
        }

        if (IsThreeD && WorldScene is not null)
        {
            var worldPoints = positions
                .Select(position => WorldScene.Primitives.FirstOrDefault(item =>
                    string.Equals(item.Id, $"world:unit:{position.VehicleId}", StringComparison.Ordinal))?.Transform.Position)
                .Where(point => point is not null)
                .Select(point => point!)
                .ToArray();
            if (worldPoints.Length > 0)
            {
                FocusWorldPoint(new ThreeDVector3(
                    worldPoints.Average(point => point.X),
                    worldPoints.Average(point => point.Y),
                    worldPoints.Average(point => point.Z)));
            }
            return;
        }

        StopFollowingInternal();
        _navigationRequest = positions.Count == 1
            ? new MapNavigationRequest(
                MapNavigationRequestKind.CenterOn,
                CreateCenterViewport(positions[0].Longitude, positions[0].Latitude))
            : new MapNavigationRequest(
                MapNavigationRequestKind.FitVehicles,
                MapNavigationMath.TryMean(
                    positions.Select(item => new MapNavigationCoordinate(item.Longitude, item.Latitude)),
                    out var mean)
                    ? CreateCenterViewport(mean.LongitudeDegrees, mean.LatitudeDegrees)
                    : null,
                VehicleIds: positions.Select(item => item.VehicleId).ToArray());
        _navigationRevision++;
        RaiseNavigationStateChanged();
        Rebuild();
    }

    public void FollowSelected()
    {
        if (!TryGetSelectedPositions(out var positions))
        {
            return;
        }

        _requestedViewport = null;
        _navigationRequest = null;
        _viewportMode = MapViewportMode.FitAll;
        _navigationRevision++;
        _follow = new MapFollowState(
            positions.Select(item => item.VehicleId).ToArray(),
            _navigationRevision);
        RaiseNavigationStateChanged();
        Rebuild();
    }

    public void StopFollowing()
    {
        if (!IsFollowing)
        {
            return;
        }

        StopFollowingInternal();
        RaiseNavigationStateChanged();
        Rebuild();
    }

    public void JumpToOperator()
    {
        // Use the snapshot that is currently rendered first. The Windows
        // provider can transition to unavailable between a button becoming
        // enabled and the click; the rendered scene is still a valid target.
        var snapshot = Scene.OperatorLocation.IsAvailable
            ? Scene.OperatorLocation
            : _operatorLocation?.Snapshot;
        if (snapshot is null || !snapshot.IsAvailable ||
            snapshot.LongitudeDegrees is not { } longitude ||
            snapshot.LatitudeDegrees is not { } latitude)
        {
            return;
        }

        if (IsThreeD && WorldScene is not null)
        {
            var operatorPoint = WorldScene.Primitives.FirstOrDefault(item => item.Id == "world:operator")?.Transform.Position
                ?? ThreeDSceneMath.ToLocal(latitude, longitude, 0, WorldScene.Origin);
            FocusWorldPoint(operatorPoint);
            return;
        }

        StopFollowingInternal();
        _navigationRequest = new MapNavigationRequest(
            MapNavigationRequestKind.CenterOn,
            CreateCenterViewport(longitude, latitude));
        _navigationRevision++;
        RaiseNavigationStateChanged();
        Rebuild();
    }

    public void UserPanned()
        => StopFollowing();

    private void StopFollowingInternal()
    {
        _follow = null;
        OnPropertyChanged(nameof(IsFollowing));
        OnPropertyChanged(nameof(FollowLabel));
        OnPropertyChanged(nameof(ViewportLabel));
        _stopFollowingCommand.RaiseCanExecuteChanged();
    }

    private MapViewportSnapshot CreateCenterViewport(double longitude, double latitude)
        => new(
            longitude,
            latitude,
            MapNavigationMath.SingleTargetResolution,
            CurrentViewport?.RotationDegrees ?? 0);

    private bool TryGetSelectedPositions(out IReadOnlyList<NavigationPosition> positions)
    {
        var selectedIds = _selection.SelectedUnitIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedIds.Length == 0)
        {
            positions = [];
            return false;
        }

        var result = new List<NavigationPosition>(selectedIds.Length);
        foreach (var vehicleId in selectedIds)
        {
            var telemetrySourceId = _reconciliation?.ResolveTelemetrySource(vehicleId) ?? vehicleId;
            var telemetry = _telemetry.Items
                .Where(item => string.Equals(item.VehicleId, telemetrySourceId, StringComparison.Ordinal))
                .OrderByDescending(item => item.ObservedAt)
                .FirstOrDefault();
            if (telemetry?.LatitudeDegrees is not { } latitude ||
                telemetry.LongitudeDegrees is not { } longitude ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
                telemetry.IsStale ||
                telemetry.State is AvailabilityState.Offline or AvailabilityState.Faulted)
            {
                positions = [];
                return false;
            }

            result.Add(new NavigationPosition(vehicleId, longitude, latitude));
        }

        positions = result;
        return true;
    }

    private bool HasValidPositions(IReadOnlyList<string> vehicleIds)
    {
        foreach (var vehicleId in vehicleIds)
        {
            var telemetrySourceId = _reconciliation?.ResolveTelemetrySource(vehicleId) ?? vehicleId;
            var telemetry = _telemetry.Items
                .Where(item => string.Equals(item.VehicleId, telemetrySourceId, StringComparison.Ordinal))
                .OrderByDescending(item => item.ObservedAt)
                .FirstOrDefault();
            if (telemetry?.LatitudeDegrees is not double latitude ||
                telemetry.LongitudeDegrees is not double longitude ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
                telemetry.IsStale ||
                telemetry.State is AvailabilityState.Offline or AvailabilityState.Faulted)
            {
                return false;
            }
        }

        return vehicleIds.Count > 0;
    }

    private void RaiseNavigationStateChanged()
    {
        OnPropertyChanged(nameof(IsFollowing));
        OnPropertyChanged(nameof(FollowLabel));
        OnPropertyChanged(nameof(ViewportLabel));
        OnPropertyChanged(nameof(CanNavigateSelection));
        OnPropertyChanged(nameof(CanJumpToOperator));
        OnPropertyChanged(nameof(OperatorNavigationLabel));
        _jumpToSelectedCommand.RaiseCanExecuteChanged();
        _followSelectedCommand.RaiseCanExecuteChanged();
        _stopFollowingCommand.RaiseCanExecuteChanged();
        _jumpToOperatorCommand.RaiseCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record NavigationPosition(string VehicleId, double Longitude, double Latitude);

    private void SelectVehicle(object? parameter)
    {
        _geometrySelection?.Clear();
        _deleteSelectedGeometryCommand.RaiseCanExecuteChanged();
        if (parameter is null)
        {
            _selection.Clear();
            return;
        }

        if (parameter is MapVehicleBoxSelectionRequest boxRequest)
        {
            var boxedIds = boxRequest.VehicleIds
                .Where(id => _vehicles.TryGet(id, out _))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (boxedIds.Length == 0)
                return;

            var boxedSelectedIds = boxRequest.Extend || boxRequest.Range
                ? _selection.SelectedUnitIds.Concat(boxedIds).Distinct(StringComparer.Ordinal).ToArray()
                : boxedIds;
            var selections = boxedSelectedIds
                .Select(id => _vehicles.TryGet(id, out var vehicle) ? vehicle : null)
                .Where(vehicle => vehicle is not null)
                .Cast<VehicleRecord>()
                .Select(SelectionFactory.From)
                .ToArray();
            _selection.SetUnitSelection(selections, _selection.UnitSelectionAnchorId ?? boxedIds[0]);
            return;
        }

        var request = parameter is MapVehicleSelectionRequest mapRequest
            ? mapRequest
            : parameter is string id
                ? new MapVehicleSelectionRequest(id, false, false)
                : null;
        if (request is null ||
            !_vehicles.TryGet(request.VehicleId, out var vehicle) ||
            vehicle is null)
        {
            return;
        }

        if (!request.Extend && !request.Range)
        {
            _selection.SetUnitSelection([SelectionFactory.From(vehicle)], vehicle.Id);
            return;
        }

        var selectedIds = _selection.SelectedUnitIds.ToList();
        var anchorId = _selection.UnitSelectionAnchorId;
        if (request.Range && selectedIds.Count > 0)
        {
            var anchorIndex = _vehicles.Items
                .Select((item, index) => (item, index))
                .FirstOrDefault(pair => pair.item.Id == anchorId).index;
            var targetIndex = _vehicles.Items.IndexOf(vehicle);
            var start = Math.Min(anchorIndex, targetIndex);
            var end = Math.Max(anchorIndex, targetIndex);
            selectedIds = _vehicles.Items
                .Skip(start)
                .Take(end - start + 1)
                .Select(item => item.Id)
                .ToList();
        }
        else if (request.Extend && !selectedIds.Remove(vehicle.Id))
        {
            selectedIds.Add(vehicle.Id);
        }

        _selection.SetUnitSelection(
            selectedIds
                .Select(id => _vehicles.TryGet(id, out var item) ? item : null)
                .Where(item => item is not null)
                .Cast<VehicleRecord>()
                .Select(SelectionFactory.From)
                .ToArray(),
            anchorId ?? vehicle.Id);
    }

    private void UpdateViewport(object? parameter)
    {
        if (parameter is not MapViewportSnapshot snapshot ||
            !double.IsFinite(snapshot.LongitudeDegrees) ||
            !double.IsFinite(snapshot.LatitudeDegrees) ||
            !double.IsFinite(snapshot.Resolution) ||
            !double.IsFinite(snapshot.RotationDegrees) ||
            snapshot.Resolution <= 0)
        {
            return;
        }

        // A newly assigned Mapsui control can publish its placeholder
        // viewport before an explicit startup, saved-view, or style-change
        // request has been rendered. Do not let that placeholder replace the
        // requested viewport or trigger a rebuild that drops the request.
        var requestedViewport = _requestedViewport ?? Presentation.Scene.RequestedViewport;
        if (_startupViewportPending &&
            _lastViewport is null &&
            _requestedViewport is null &&
            !ViewportMatches(snapshot, _engine.StartupViewport))
        {
            return;
        }

        if (requestedViewport is { } requested && !ViewportMatches(snapshot, requested))
        {
            return;
        }

        _lastViewport = snapshot;
        _viewportState.Update(snapshot);
        _presentationState?.Update(Presentation, snapshot, GoToIndicatorsVisible);
        if (_startupViewportPending && ViewportMatches(snapshot, _engine.StartupViewport))
        {
            _startupViewportPending = false;
        }
        _requestedViewport = null;
        OnPropertyChanged(nameof(CurrentViewport));
        _saveViewCommand.RaiseCanExecuteChanged();
    }

    private static bool ViewportMatches(MapViewportSnapshot actual, MapViewportSnapshot requested)
        => Math.Abs(actual.LongitudeDegrees - requested.LongitudeDegrees) < 0.000001 &&
           Math.Abs(actual.LatitudeDegrees - requested.LatitudeDegrees) < 0.000001 &&
           Math.Abs(actual.Resolution - requested.Resolution) <= Math.Max(1, requested.Resolution * 0.01) &&
           Math.Abs(actual.RotationDegrees - requested.RotationDegrees) < 0.1;

    private async Task SelectStyleAsync(MapStyleOption style)
    {
        try
        {
            if (style.IsThreeD)
            {
                var viewport = _lastViewport ?? _viewportState.Current ?? _engine.StartupViewport;
                if (_worldScene is null)
                {
                    SavedViewStatus = "3D view is unavailable.";
                    RefreshStyles();
                    return;
                }

                await _worldScene.ActivateAsync(new ThreeDWorldEntryRequest(
                    viewport.LongitudeDegrees,
                    viewport.LatitudeDegrees,
                    viewport.Resolution,
                    viewport.RotationDegrees));
                WorldScene = _worldScene.Current;
                OnPropertyChanged(nameof(IsThreeD));
                OnPropertyChanged(nameof(WorldCameraModeLabel));
                RaiseSavedViewCommandStates();
                SavedViewStatus = "Selected 3D.";
                return;
            }

            // Changing the basemap must not change the operator's current
            // center, zoom, or rotation. The map control consumes this
            // request on its next presentation update.
            _requestedViewport = _lastViewport;
            await _engine.SelectStyleAsync(style.StyleId);
            OnPropertyChanged(nameof(IsThreeD));
            OnPropertyChanged(nameof(WorldCameraModeLabel));
            RaiseSavedViewCommandStates();
            SavedViewStatus = $"Selected {style.DisplayName}.";
        }
        catch (Exception ex)
        {
            SavedViewStatus = $"Style selection failed: {ex.Message}";
            RefreshStyles();
        }
    }

    private bool CanSaveView()
        => GlobalMapControlsAvailable && !string.IsNullOrWhiteSpace(SavedViewName);

    private async Task SaveViewAsync(CancellationToken cancellationToken)
    {
        var viewport = _lastViewport ?? Presentation.Scene.RequestedViewport;
        if (viewport is null)
        {
            SavedViewStatus = "The map viewport is not ready yet. Move or zoom the map, then try again.";
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var existing = SelectedSavedView is not null &&
                           string.Equals(SelectedSavedView.Name, SavedViewName.Trim(), StringComparison.OrdinalIgnoreCase)
                ? SelectedSavedView
                : null;
            var currentSelection = _selection.Current;
            var view = new SavedMapView(
                existing?.Id ?? $"view-{Guid.NewGuid():N}",
                SavedViewName.Trim(),
                _catalog.ActivePackage?.Key,
                SelectedStyle?.IsThreeD == true ? _engine.SelectedStyleId : SelectedStyle?.StyleId ?? _engine.SelectedStyleId,
                viewport,
                _follow is null ? _viewportMode : MapViewportMode.FitAll,
                OrientationMode,
                GeometryVisible,
                PolicyVisible,
                TrailsVisible,
                VehicleLabelsVisible,
                currentSelection.Kind,
                currentSelection.Id,
                existing?.CreatedAt ?? now,
                now)
            {
                SelectedUnitIds = _selection.SelectedUnitIds.ToArray(),
                UnitSelectionAnchorId = _selection.UnitSelectionAnchorId
            };
            await _savedViewRepository.UpsertAsync(view, cancellationToken);
            RefreshSavedViews(view.Id);
            SavedViewStatus = $"Saved view '{view.Name}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SavedViewStatus = $"Could not save view: {ex.Message}";
        }
    }

    private async Task ApplySavedViewAsync(CancellationToken cancellationToken)
    {
        var view = SelectedSavedView;
        if (view is null)
        {
            return;
        }

        try
        {
            // Saved views are also useful as camera bookmarks while the
            // synthetic 3D view is active. Keep the 3D scene selected and
            // move its local origin/camera to the saved view center.
            if (IsThreeD && _worldScene is not null)
            {
                await _worldScene.ActivateAsync(new ThreeDWorldEntryRequest(
                    view.Viewport.LongitudeDegrees,
                    view.Viewport.LatitudeDegrees,
                    view.Viewport.Resolution,
                    view.Viewport.RotationDegrees), cancellationToken);
                WorldScene = _worldScene.Current;
                SavedViewStatus = $"Moved camera to '{view.Name}'.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(view.PackageKey) &&
                !string.Equals(_catalog.ActivePackage?.Key, view.PackageKey, StringComparison.Ordinal))
            {
                if (!_catalog.TryGet(view.PackageKey, out var package) || package is not { Valid: true })
                {
                    throw new InvalidOperationException(
                        $"The saved view requires map package '{view.PackageKey}', which is not installed and valid.");
                }

                await _catalog.ActivateAsync(view.PackageKey, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(view.StyleId))
            {
                if (!_engine.AvailableStyles.Any(item =>
                        string.Equals(item.StyleId, view.StyleId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"The saved view requires map style '{view.StyleId}', which is not available in the active package.");
                }

                await _engine.SelectStyleAsync(view.StyleId, cancellationToken);
            }

            StopFollowingInternal();
            _navigationRequest = null;
            _viewportMode = view.ViewportMode == MapViewportMode.FollowSelected
                ? MapViewportMode.FitAll
                : view.ViewportMode;
            OnPropertyChanged(nameof(ViewportLabel));
            OrientationMode = view.OrientationMode;
            GeometryVisible = view.GeometryVisible;
            PolicyVisible = view.PolicyVisible;
            TrailsVisible = view.TrailsVisible;
            VehicleLabelsVisible = view.VehicleLabelsVisible;
            _requestedViewport = view.Viewport;
            _lastViewport = view.Viewport;
            RestoreSelection(view);
            _navigationRevision++;
            RaiseNavigationStateChanged();
            RefreshStyles();
            Rebuild();
            SavedViewStatus = $"Applied view '{view.Name}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SavedViewStatus = $"Could not apply saved view: {ex.Message}";
        }
    }

    private async Task DeleteSavedViewAsync(CancellationToken cancellationToken)
    {
        var view = SelectedSavedView;
        if (view is null)
        {
            return;
        }

        try
        {
            await _savedViewRepository.RemoveAsync(view.Id, cancellationToken);
            RefreshSavedViews();
            SavedViewStatus = $"Deleted view '{view.Name}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SavedViewStatus = $"Could not delete view: {ex.Message}";
        }
    }

    private void RestoreSelection(SavedMapView view)
    {
        if (view.SelectedUnitIds.Count > 0)
        {
            var units = view.SelectedUnitIds
                .Select(id => _vehicles.TryGet(id, out var vehicle) ? vehicle : null)
                .Where(vehicle => vehicle is not null)
                .Cast<VehicleRecord>()
                .Select(SelectionFactory.From)
                .ToArray();
            _selection.SetUnitSelection(units, view.UnitSelectionAnchorId);
            return;
        }
        if (string.IsNullOrWhiteSpace(view.SelectionId))
        {
            return;
        }

        switch (view.SelectionKind)
        {
            case SelectionKind.Vehicle when _vehicles.TryGet(view.SelectionId, out var vehicle) && vehicle is not null:
                _selection.Select(SelectionFactory.From(vehicle));
                break;
            case SelectionKind.Mission when _missions.TryGet(view.SelectionId, out var mission) && mission is not null:
                _selection.Select(SelectionFactory.From(mission));
                break;
            case SelectionKind.Task when _tasks.TryGet(view.SelectionId, out var task) && task is not null:
                _selection.Select(SelectionFactory.From(task));
                break;
            case SelectionKind.Geometry:
                _selection.Select(SelectionFactory.From(new GeometrySelectionContext(
                    view.SelectionId,
                    view.SelectionId,
                    GeometryDocumentKind.Unknown,
                    GeometryCoordinateFrame.GlobalWgs84,
                    null,
                    "Saved view reference",
                    "Unknown",
                    "Saved map view")));
                break;
        }
    }

    private void RefreshStyles()
    {
        var selectedId = _engine.SelectedStyleId;
        var keepThreeD = _selectedStyle?.IsThreeD == true;
        _refreshingStyles = true;
        try
        {
            Styles.Clear();
            foreach (var style in _engine.AvailableStyles)
            {
                Styles.Add(style);
            }

            Styles.Add(new MapStyleOption("3d", "3D", MapStyleKind.Operational, [], string.Empty, false)
            {
                IsThreeD = true
            });

            SelectedStyle = keepThreeD
                ? Styles.FirstOrDefault(item => item.IsThreeD)
                : Styles.FirstOrDefault(item =>
                                    string.Equals(item.StyleId, selectedId, StringComparison.OrdinalIgnoreCase))
                                ?? Styles.FirstOrDefault();
        }
        finally
        {
            _refreshingStyles = false;
        }

        OnPropertyChanged(nameof(MultipleStylesAvailable));
        OnPropertyChanged(nameof(StyleLabel));
    }

    private void RefreshSavedViews(string? selectedId = null)
    {
        selectedId ??= SelectedSavedView?.Id;
        SavedViews.Clear();
        foreach (var view in _savedViewRepository.Views)
        {
            SavedViews.Add(view);
        }

        SelectedSavedView = selectedId is null
            ? SavedViews.FirstOrDefault()
            : SavedViews.FirstOrDefault(item => item.Id == selectedId) ?? SavedViews.FirstOrDefault();
        OnPropertyChanged(nameof(SavedViewsAvailable));
        RaiseSavedViewCommandStates();
    }

    private void RaiseSavedViewCommandStates()
    {
        _saveViewCommand.RaiseCanExecuteChanged();
        _applySavedViewCommand.RaiseCanExecuteChanged();
        _deleteSavedViewCommand.RaiseCanExecuteChanged();
    }

    private void Rebuild()
    {
        if (_follow is not null && !HasValidPositions(_follow.VehicleIds))
        {
            StopFollowingInternal();
            _navigationRevision++;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        var selectedVehicleId = SelectedVehicleId();
        _vehicleStructureKey = CreateVehicleStructureKey(_vehicles.Items);
        var displayVehicles = _reconciliation?.ProjectVehicles(_vehicles.Items) ?? _vehicles.Items;
        _trackHistory.Record(_telemetry.Items, DateTimeOffset.UtcNow);
        var highlightedGeometryIds = HighlightedGeometryIds()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var geometryId in SelectedGeometryIds) highlightedGeometryIds.Add(geometryId);
        var localGeometry = _geometryWorkspace.LocalDocuments
            .Where(item => item.Frame == GeometryCoordinateFrame.GlobalWgs84)
            .Select(item => new GeometryOverlayRecord(
                $"robotcommand-local:{item.GeometryId}",
                item.GeometryId,
                "robotcommand-local",
                null,
                item.DisplayName,
                item.Kind.ToString(),
                MapFrameKind.GlobalWgs84,
                item.IsClosed,
                item.Points.Select(point => new OperationalPoint(point.X, point.Y, point.Z)).ToArray(),
                item.Rings.Select(ring => (IReadOnlyList<OperationalPoint>)ring.Points.Select(point => new OperationalPoint(point.X, point.Y, point.Z)).ToArray()).ToArray(),
                "none",
                "none",
                item.UpdatedAt))
            .ToArray();
        // The preview Geometry library is local-first. Legacy remote Logos
        // registry records are intentionally not projected here: those
        // resources are not yet managed or deployed by this UI and must not
        // appear as unexplained map objects.
        var missionPreview = _flightMissions?.MapPreview;
        var missionPreviewGeometry = missionPreview?.Steps
            .Where(step => step.Kind is FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.WaypointSequence or FlightMissionStepKind.SurveyZone or FlightMissionStepKind.CorridorScan or FlightMissionStepKind.TimedLoiter)
            .Select(step => (Step: step, Coordinates: step.Kind switch
            {
                FlightMissionStepKind.SurveyZone => Px4FlightMissionCompiler.SurveyRoute(step),
                FlightMissionStepKind.CorridorScan => Px4FlightMissionCompiler.CorridorRoute(step),
                _ => step.FrozenCoordinates
            }))
            .Where(item => item.Coordinates.Count > 0)
            .Select(step => new GeometryOverlayRecord(
                $"robotcommand-mission-preview:{missionPreview.Id}:{step.Step.Id}",
                $"robotcommand-mission-preview:{missionPreview.Id}:{step.Step.Id}",
                "robotcommand-mission-preview",
                null,
                missionPreview.Name,
                step.Step.Kind is FlightMissionStepKind.PointOfInterest or FlightMissionStepKind.TimedLoiter ? "FlightMissionPreviewPoint" : "FlightMissionPreviewRoute",
                MapFrameKind.GlobalWgs84,
                false,
                step.Coordinates.Select(point => new OperationalPoint(point.LongitudeDegrees, point.LatitudeDegrees, missionPreview.RelativeAltitudeMetres)).ToArray(),
                [],
                "none",
                "none",
                missionPreview.UpdatedAt))
            .ToArray() ?? [];
        var fenceGeometry = _fences?.Fences
            .Select(fence => new GeometryOverlayRecord(
                $"robotcommand-fence:{fence.Document.FenceId}",
                $"robotcommand-fence:{fence.Document.FenceId}",
                "robotcommand-fence",
                null,
                fence.Document.DisplayName,
                fence.Document.Kind == FenceKind.Inclusion ? "FenceInclusion" : "FenceExclusion",
                MapFrameKind.GlobalWgs84,
                true,
                fence.Document.Coordinates
                    .Select(point => new OperationalPoint(point.LongitudeDegrees, point.LatitudeDegrees))
                    .ToArray(),
                [],
                "none",
                "none",
                fence.Document.UpdatedAt))
            .ToArray() ?? [];
        var geometryForScene = localGeometry.Concat(fenceGeometry).Concat(missionPreviewGeometry).ToArray();
        var unitSelectionIds = _selection.SelectedUnitIds;
        var teamSelectionIds = _teamSelection?.Current.IsSelected == true
            ? _teamSelection.Current.MemberUnitIds
            : [];
        var activeTargetIds = ActiveUnitSelectionIds();
        var baseScene = _builder.Build(
            _vehicles.Items,
            _telemetry.Items,
            geometryForScene,
            selectedVehicleId,
            _viewportMode,
            GeometryVisible,
            PolicyVisible,
            highlightedGeometryIds,
            unitSelectionIds.ToHashSet(StringComparer.Ordinal));
        baseScene = baseScene with
        {
            Vehicles = baseScene.Vehicles
                .Select(vehicle => vehicle with { TeamSelected = teamSelectionIds.Contains(vehicle.VehicleId, StringComparer.Ordinal) })
                .ToArray()
        };
        var activeGoToTargets = (_operatorControls?.ActiveGoToTargets ?? [])
            .Select(target => target with
            {
                Selected = activeTargetIds.Contains(target.VehicleId, StringComparer.Ordinal)
            })
            .ToArray();
        var queuedGoToTargets = (_operatorControls?.QueuedGoToTargets ?? [])
            .Select(target => target with
            {
                Selected = activeTargetIds.Contains(target.VehicleId, StringComparer.Ordinal)
            })
            .ToArray();
        var goToTargets = queuedGoToTargets.Concat(
            activeGoToTargets.Where(active =>
                !queuedGoToTargets.Any(queued => queued.VehicleId == active.VehicleId))).ToArray();
        Scene = baseScene with
        {
            NavigationRevision = _navigationRevision,
            Trails = baseScene.Frame == MapFrameKind.GlobalWgs84
                ? _trackHistory.BuildTrailsForSelectedVehicles(
                    displayVehicles,
                    activeTargetIds.ToHashSet(StringComparer.Ordinal))
                : [],
            TrailsVisible = TrailsVisible,
            VehicleLabelsVisible = VehicleLabelsVisible,
            OrientationMode = OrientationMode,
            RequestedViewport = _requestedViewport,
            NavigationRequest = _navigationRequest,
            Follow = _follow,
            GoToTarget = GoToIndicatorsVisible ? goToTargets.FirstOrDefault() : null,
            GoToTargets = GoToIndicatorsVisible ? goToTargets : [],
            FormationPreviewTargets = GoToIndicatorsVisible
                ? (_operatorControls?.FormationPreviewTargets ?? [])
                : [],
            FormationPreviewPaths = GoToIndicatorsVisible
                ? (_operatorControls?.FormationPreviewPaths ?? [])
                : [],
            LockedTeamPosition = BuildLockedTeamPosition(),
            OperatorLocation = _operatorLocation?.Snapshot ?? OperatorLocationSnapshot.Unavailable()
        };
        var prepared = _engine.Prepare(Scene);
        if (_startupViewportPending &&
            _lastViewport is null &&
            _requestedViewport is null &&
            !Scene.HasData &&
            prepared.Scene.RequestedViewport is null)
        {
            prepared = prepared with
            {
                Scene = prepared.Scene with
                {
                    RequestedViewport = _engine.StartupViewport
                }
            };
        }

        var motion = BuildMotionSnapshot(includeTrails: TrailsVisible);
        VehicleMotion = motion;
        Presentation = prepared with
        {
            GeometryEdit = _geometryEdit.Snapshot,
            Motion = motion
        };
        _presentationState?.Update(Presentation, CurrentViewport ?? Presentation.Scene.RequestedViewport, GoToIndicatorsVisible);
        OnPropertyChanged(nameof(FrameLabel));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(GlobalMapControlsAvailable));
        OnPropertyChanged(nameof(OverlayControlsAvailable));
        OnPropertyChanged(nameof(StyleLabel));
        OnPropertyChanged(nameof(IsThreeD));
        OnPropertyChanged(nameof(WorldCameraModeLabel));
        _saveViewCommand.RaiseCanExecuteChanged();

        Summary = Scene.HasData
            ? Presentation.Renderer == OperationalMapRendererKind.NativeGlobal
                ? $"{Scene.Vehicles.Count} vehicle(s) · {Scene.Geometries.Count} overlay(s) · {Scene.Trails.Count} trail(s) · {Presentation.Status}"
                : $"{Scene.Vehicles.Count} vehicle(s) · {Scene.Geometries.Count} geometry object(s) · {Presentation.Status}"
            : Presentation.Renderer == OperationalMapRendererKind.NativeGlobal
                ? $"No live units connected · {Presentation.Status}"
                : "Waiting for compatible vehicle or geometry coordinates.";
    }

    private void OnActiveGoToTargetChanged(object? sender, EventArgs e)
        => Rebuild();

    private void OnFormationPreviewChanged(object? sender, EventArgs e)
        => Rebuild();

    private void OnQueuedCommandsChanged(object? sender, EventArgs e)
        => Rebuild();

    private void OnTeamSelectionChanged(object? sender, EventArgs e)
        => Rebuild();

    private void OnWorldSceneChanged(object? sender, EventArgs e)
    {
        if (_worldScene is null) return;
        WorldScene = _worldScene.Current;
        OnPropertyChanged(nameof(WorldCameraModeLabel));
    }

    private void ToggleWorldCameraMode()
    {
        if (!IsThreeD || _worldScene is null || WorldScene is null) return;
        var current = WorldScene.Camera;
        ThreeDCameraSnapshot next;
        if (current.OrbitMode)
        {
            next = current with { OrbitMode = false, OrbitTarget = null, OrbitDistance = null };
        }
        else
        {
            var target = current.OrbitTarget ?? ThreeDVector3.Zero;
            var distance = Math.Clamp(current.OrbitDistance ?? ThreeDSceneMath.Distance(current.Position, target), 2, 10000);
            next = current with
            {
                OrbitMode = true,
                OrbitTarget = target,
                OrbitDistance = distance
            };
        }
        _ = _worldScene.SetCameraAsync(next);
    }

    private Task ResetWorldCameraAsync(CancellationToken cancellationToken)
        => _worldScene is null ? Task.CompletedTask : _worldScene.ResetCameraAsync(cancellationToken);

    private Task FitWorldSceneAsync(CancellationToken cancellationToken)
        => _worldScene is null ? Task.CompletedTask : _worldScene.FitSceneAsync(cancellationToken);

    private void FocusWorldPoint(ThreeDVector3 target)
    {
        if (_worldScene is null || WorldScene is null)
        {
            return;
        }

        var current = WorldScene.Camera;
        ThreeDCameraSnapshot next;
        if (current.OrbitMode || current.OrbitTarget is not null)
        {
            var distance = Math.Clamp(
                current.OrbitDistance ?? ThreeDSceneMath.Distance(current.Position, current.OrbitTarget ?? ThreeDVector3.Zero),
                2,
                10000);
            next = current with
            {
                OrbitTarget = target,
                OrbitDistance = distance,
                Position = ThreeDProjection.OrbitPosition(target, current.YawDegrees, current.PitchDegrees, distance)
            };
        }
        else
        {
            var offset = new ThreeDVector3(
                current.Position.X - (current.OrbitTarget?.X ?? 0),
                current.Position.Y - (current.OrbitTarget?.Y ?? 0),
                current.Position.Z - (current.OrbitTarget?.Z ?? 0));
            next = current with
            {
                Position = new ThreeDVector3(
                    target.X + offset.X,
                    target.Y + offset.Y,
                    target.Z + offset.Z),
                OrbitTarget = null,
                OrbitDistance = null
            };
        }

        _ = _worldScene.SetCameraAsync(next);
    }

    private void OnFormationLockChanged(object? sender, EventArgs e)
    {
        // Formation state is advanced by the Runtime controller, not by the
        // Avalonia UI thread. Rebuilding the map directly from that callback
        // can update bound properties and Mapsui state from a worker thread,
        // which both freezes the UI and can stop the formation controller.
        if (_dispatcher.CheckAccess())
        {
            Rebuild();
            return;
        }

        ScheduleRebuild();
    }

    private MapTeamPositionVisual? BuildLockedTeamPosition()
    {
        var formation = _formationLock?.Current;
        return formation is { IsLocked: true, TeamId: { }, TeamName: { }, TeamLatitudeDegrees: { } latitude, TeamLongitudeDegrees: { } longitude }
            ? new MapTeamPositionVisual(
                formation.TeamId,
                formation.TeamName,
                latitude,
                longitude,
                formation.TargetLatitudeDegrees,
                formation.TargetLongitudeDegrees,
                formation.State == FormationLockState.Moving,
                formation.Members.Select(member => new MapTeamMemberTargetVisual(
                    member.UnitId,
                    latitude + (member.TargetNorthOffsetMetres ?? member.NorthOffsetMetres) / 6378137d * 180d / Math.PI,
                    longitude + (member.TargetEastOffsetMetres ?? member.EastOffsetMetres) /
                    (6378137d * Math.Cos(latitude * Math.PI / 180d)) * 180d / Math.PI)).ToArray(),
                Math.Abs(formation.TargetRotationDegrees - formation.CurrentRotationDegrees) > 0.005d ||
                Math.Abs(formation.TargetScalePercent - formation.CurrentScalePercent) > 0.0001d)
            : null;
    }

    private IReadOnlyList<string> ActiveUnitSelectionIds()
        => _teamSelection?.Current.IsSelected == true
            ? _teamSelection.Current.MemberUnitIds
            : _selection.SelectedUnitIds;

    private string? SelectedVehicleId()
    {
        var selectedId = ActiveUnitSelectionIds().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(selectedId))
            return selectedId;

        return _selection.Current.Kind == SelectionKind.Vehicle
            ? _selection.Current.Id
            : null;
    }

    private IReadOnlySet<string> HighlightedGeometryIds()
    {
        var highlighted = new HashSet<string>(StringComparer.Ordinal);
        MissionRecord? mission = null;
        var current = _selection.Current;
        if (current.Kind == SelectionKind.Geometry && !string.IsNullOrWhiteSpace(current.Id))
        {
            highlighted.Add(current.Id);
            return highlighted;
        }

        if (current.Kind == SelectionKind.Mission &&
            current.Id is not null &&
            _missions.TryGet(current.Id, out var selectedMission))
        {
            mission = selectedMission;
        }
        else if (current.Kind == SelectionKind.Task &&
                 current.Id is not null &&
                 _tasks.TryGet(current.Id, out var selectedTask))
        {
            foreach (var geometryId in selectedTask?.GeometryIds ?? [])
            {
                highlighted.Add(geometryId);
            }

            if (selectedTask?.MissionId is not null &&
                _missions.TryGet(selectedTask.MissionId, out var taskMission))
            {
                mission = taskMission;
            }
        }

        foreach (var geometryId in mission?.GeometryIds ?? [])
        {
            highlighted.Add(geometryId);
        }

        return highlighted;
    }
}
