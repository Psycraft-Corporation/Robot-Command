using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Evidence;
using RobotCommand.Services.Media;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class CameraPanelViewModel : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly IEntityStore<string, CameraSourceRecord> _cameraSources;
    private readonly IEntityStore<string, MavlinkCameraDefinitionRecord>? _cameraDefinitions;
    private readonly IEntityStore<string, CameraStreamRecord> _cameraStreams;
    private readonly IEntityStore<string, PerceptionTrackRecord> _tracks;
    private readonly ILogosConnectionManager _connections;
    private readonly IVideoPlaybackAdapter _playback;
    private readonly IGStreamerRuntime _gStreamerRuntime;
    private readonly IGStreamerVideoPipeline _nativePipeline;
    private readonly IUnifiedVideoTimelineService _localVideo;
    private readonly IEvidenceLibrary _evidence;
    private readonly ISourceImageCaptureGateway _sourceImages;
    private readonly IVideoProtocolPolicy _protocolPolicy;
    private readonly AppConfiguration _configuration;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IMediaWorkflow? _mediaWorkflow;
    private readonly ILocalizationService? _localization;
    private readonly IUnitAssociationWorkflow? _unitAssociations;
    private readonly IUnitRoutingWorkflow? _routing;
    private readonly SwitchableVideoFrameSource _frameSource = new();
    private readonly IGhostCameraStreamProvider? _ghostStreams;
    private readonly AsyncRelayCommand _openStreamCommand;
    private readonly AsyncRelayCommand _closeStreamCommand;
    private readonly AsyncRelayCommand _startTestPatternCommand;
    private readonly AsyncRelayCommand _startTestFileCommand;
    private readonly AsyncRelayCommand _stopNativeVideoCommand;
    private readonly AsyncRelayCommand _refreshGStreamerCommand;
    private readonly AsyncRelayCommand _retryStreamCommand;
    private readonly AsyncRelayCommand _playTimelineCommand;
    private readonly AsyncRelayCommand _pauseTimelineCommand;
    private readonly AsyncRelayCommand _resumeTimelineCommand;
    private readonly AsyncRelayCommand _goLiveCommand;
    private readonly AsyncRelayCommand _retainTimelineCommand;
    private readonly AsyncRelayCommand _refreshTimelineCommand;
    private readonly AsyncRelayCommand _downloadTimelineCommand;
    private readonly AsyncRelayCommand _captureDisplayedFrameCommand;
    private readonly AsyncRelayCommand _captureSourceImageCommand;
    private readonly AsyncRelayCommand _exportTimelineClipCommand;
    private CameraSourceRecord? _selectedCamera;
    private CameraStreamRecord? _activeStream;
    private string? _activeStreamUnitId;
    private VideoProtocolPreference _selectedProtocol = VideoProtocolPreference.Automatic;
    private VideoOverlayScene _overlayScene = VideoOverlayScene.Empty;
    private string _streamStatus = "No stream";
    private string _playbackSummary = VideoPlaybackStatus.Detached.Summary;
    private string _playbackDetail = VideoPlaybackStatus.Detached.Detail;
    private string _streamEndpoint = "-";
    private string _gStreamerStatus = GStreamerRuntimeDiagnostics.Unknown.Summary;
    private string _gStreamerDetail = GStreamerRuntimeDiagnostics.Unknown.Detail;
    private string _nativeVideoStatus = NativeVideoPipelineStatus.Stopped.Summary;
    private string _nativeVideoDetail = NativeVideoPipelineStatus.Stopped.Detail;
    private string _nativeVideoMetrics = "No decoded frame";
    private bool _hasNativeFrame;
    private bool _hasSelectedUnit;
    private string _fallbackStatus = "Automatic fallback has not been used.";
    private string _protocolPlanText = "No protocol plan";
    private string _activeProtocolText = "No active protocol";
    private IReadOnlyList<VideoProtocolPreference> _protocolPlan = [];
    private readonly HashSet<NativeVideoProtocol> _attemptedNativeProtocols = [];
    private int _protocolPlanIndex = -1;
    private int _fallbackScheduled;
    private bool _automaticFallback;
    private bool _closingStream;
    private double _timelinePosition = 1;
    private string _localRecordingStatus = LocalVideoRecorderStatus.Stopped.Summary;
    private string _localRecordingDetail = LocalVideoRecorderStatus.Stopped.Detail;
    private string _timelineRangeText = "No console or vehicle recordings";
    private string _remoteRecordingStatus = RemoteVideoRecordingStatus.NotConfigured.Summary;
    private string _remoteRecordingDetail = RemoteVideoRecordingStatus.NotConfigured.Detail;
    private bool _captureIncludeOverlays = true;
    private string _evidenceStatus = "No evidence captured in this session.";
    private string _cameraDefinitionSummary = string.Empty;
    private bool _hasCameraDefinition;

    public CameraPanelViewModel(
        ISelectionService selection,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks,
        IEntityStore<string, CameraSourceRecord> cameraSources,
        IEntityStore<string, CameraStreamRecord> cameraStreams,
        IEntityStore<string, PerceptionTrackRecord> tracks,
        ILogosConnectionManager connections,
        IVideoPlaybackAdapter playback,
        IGStreamerRuntime gStreamerRuntime,
        IGStreamerVideoPipeline nativePipeline,
        IUnifiedVideoTimelineService localVideo,
        IEvidenceLibrary evidence,
        ISourceImageCaptureGateway sourceImages,
        IVideoProtocolPolicy protocolPolicy,
        AppConfiguration configuration,
        IUiDispatcher uiDispatcher,
        IMediaWorkflow? mediaWorkflow = null,
        IMediaSettingsService? mediaSettings = null,
        ILocalizationService? localization = null,
        IEntityStore<string, MavlinkCameraDefinitionRecord>? cameraDefinitions = null,
        IUnitAssociationWorkflow? unitAssociations = null,
        IUnitRoutingWorkflow? routing = null,
        IGhostCameraStreamProvider? ghostStreams = null)
    {
        _selection = selection;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _missions = missions;
        _tasks = tasks;
        _cameraSources = cameraSources;
        _cameraDefinitions = cameraDefinitions;
        _cameraStreams = cameraStreams;
        _tracks = tracks;
        _connections = connections;
        _playback = playback;
        _gStreamerRuntime = gStreamerRuntime;
        _nativePipeline = nativePipeline;
        _localVideo = localVideo;
        _evidence = evidence;
        _sourceImages = sourceImages;
        _protocolPolicy = protocolPolicy;
        _configuration = configuration;
        _uiDispatcher = uiDispatcher;
        _mediaWorkflow = mediaWorkflow;
        _localization = localization;
        _unitAssociations = unitAssociations;
        _routing = routing;
        _ghostStreams = ghostStreams;
        if (_localization is not null)
        {
            _localization.PropertyChanged += OnLocalizationChanged;
        }
        _selectedProtocol = mediaSettings?.Current.DefaultProtocol ?? VideoProtocolPreference.Automatic;
        _frameSource.SetSource(_localVideo.PresentationFrames);
        if (_mediaWorkflow is not null) _mediaWorkflow.Changed += OnMediaWorkflowChanged;

        Cameras = [];
        Protocols =
        [
            VideoProtocolPreference.Automatic,
            VideoProtocolPreference.Rtsp,
            VideoProtocolPreference.Hls,
            VideoProtocolPreference.WebRtc
        ];
        _openStreamCommand = new AsyncRelayCommand(OpenStreamAsync, CanOpenStream);
        _closeStreamCommand = new AsyncRelayCommand(CloseStreamAsync, CanCloseStream);
        _startTestPatternCommand = new AsyncRelayCommand(StartTestPatternAsync, CanStartNativeVideo);
        _startTestFileCommand = new AsyncRelayCommand(StartTestFileAsync, CanStartTestFile);
        _stopNativeVideoCommand = new AsyncRelayCommand(StopNativeVideoAsync, CanStopNativeVideo);
        _refreshGStreamerCommand = new AsyncRelayCommand(
            token => RefreshGStreamerAsync(force: true, token));
        _retryStreamCommand = new AsyncRelayCommand(RetryStreamAsync, CanRetryStream);
        _playTimelineCommand = new AsyncRelayCommand(PlayTimelineAsync, CanPlayTimeline);
        _pauseTimelineCommand = new AsyncRelayCommand(PauseTimelineAsync, CanPauseTimeline);
        _resumeTimelineCommand = new AsyncRelayCommand(ResumeTimelineAsync, CanResumeTimeline);
        _goLiveCommand = new AsyncRelayCommand(GoLiveAsync, CanGoLive);
        _retainTimelineCommand = new AsyncRelayCommand(RetainTimelineAsync, CanRetainTimeline);
        _refreshTimelineCommand = new AsyncRelayCommand(RefreshTimelineAsync);
        _downloadTimelineCommand = new AsyncRelayCommand(DownloadTimelineAsync, CanDownloadTimeline);
        _captureDisplayedFrameCommand = new AsyncRelayCommand(CaptureDisplayedFrameAsync, CanCaptureDisplayedFrame);
        _captureSourceImageCommand = new AsyncRelayCommand(CaptureSourceImageAsync, CanCaptureSourceImage);
        _exportTimelineClipCommand = new AsyncRelayCommand(ExportTimelineClipAsync, CanExportTimelineClip);
        OpenStreamCommand = _openStreamCommand;
        CloseStreamCommand = _closeStreamCommand;
        StartTestPatternCommand = _startTestPatternCommand;
        StartTestFileCommand = _startTestFileCommand;
        StopNativeVideoCommand = _stopNativeVideoCommand;
        RefreshGStreamerCommand = _refreshGStreamerCommand;
        RetryStreamCommand = _retryStreamCommand;
        PlayTimelineCommand = _playTimelineCommand;
        PauseTimelineCommand = _pauseTimelineCommand;
        ResumeTimelineCommand = _resumeTimelineCommand;
        GoLiveCommand = _goLiveCommand;
        RetainTimelineCommand = _retainTimelineCommand;
        RefreshTimelineCommand = _refreshTimelineCommand;
        DownloadTimelineCommand = _downloadTimelineCommand;
        CaptureDisplayedFrameCommand = _captureDisplayedFrameCommand;
        CaptureSourceImageCommand = _captureSourceImageCommand;
        ExportTimelineClipCommand = _exportTimelineClipCommand;

        _selection.Changed += OnSelectionChanged;
        if (_unitAssociations is not null) _unitAssociations.Changed += OnUnitAssociationsChanged;
        if (_routing is not null) _routing.Changed += OnRoutingChanged;
        _playback.Changed += OnPlaybackChanged;
        _nativePipeline.Changed += OnNativePipelineChanged;
        _localVideo.Changed += OnLocalVideoChanged;
        _frameSource.FrameAvailable += OnNativeFrameAvailable;
        Subscribe(_vehicles.Items);
        Subscribe(_telemetry.Items);
        Subscribe(_missions.Items);
        Subscribe(_tasks.Items);
        Subscribe(_cameraSources.Items);
        if (_cameraDefinitions is not null) Subscribe(_cameraDefinitions.Items);
        Subscribe(_cameraStreams.Items);
        Subscribe(_tracks.Items);
        Refresh();
        ApplyNativeStatus();
        ApplyLocalVideoStatus();
        _ = InitializeLocalVideoAsync();
        _ = RefreshGStreamerAsync(force: false, CancellationToken.None);
    }

    public ObservableCollection<CameraSourceRecord> Cameras { get; }

    public ObservableCollection<MavlinkCameraSettingRecord> CameraSettings { get; } = [];

    public bool HasCameraDefinition
    {
        get => _hasCameraDefinition;
        private set => SetProperty(ref _hasCameraDefinition, value);
    }

    public string CameraDefinitionSummary
    {
        get => _cameraDefinitionSummary;
        private set => SetProperty(ref _cameraDefinitionSummary, value);
    }

    public IReadOnlyList<VideoProtocolPreference> Protocols { get; }

    public IReadOnlyList<string> ProtocolOptions =>
    [
        Text("VideoProtocolAutomatic", "Automatic"),
        Text("VideoProtocolRtsp", "RTSP"),
        Text("VideoProtocolHls", "HLS"),
        Text("VideoProtocolWebRtc", "WebRTC/WHEP")
    ];

    public string SelectedProtocolDisplay
    {
        get => DisplayProtocol(SelectedProtocol);
        set
        {
            var selected = Protocols.FirstOrDefault(protocol =>
                string.Equals(DisplayProtocol(protocol), value, StringComparison.Ordinal));
            if (!EqualityComparer<VideoProtocolPreference>.Default.Equals(selected, default) ||
                string.Equals(value, DisplayProtocol(default), StringComparison.Ordinal))
            {
                SelectedProtocol = selected;
            }
        }
    }

    public CameraSourceRecord? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            var previous = _selectedCamera;
            if (!SetProperty(ref _selectedCamera, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PlaybackEmptyMessage));
            // Camera source snapshots can briefly disappear while providers
            // reconcile their stores. Keep an open stream attached during that
            // transient gap; selection/association changes close it explicitly.
            if (_activeStream is not null && value is not null && previous?.Id != value.Id &&
                _activeStream.CameraSourceId != value.CameraSourceId)
                _ = CloseForCameraChangeAsync();
            RefreshPresentation();
        }
    }

    public VideoProtocolPreference SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            if (SetProperty(ref _selectedProtocol, value))
            {
                OnPropertyChanged(nameof(OpenStreamLabel));
                OnPropertyChanged(nameof(SelectedProtocolDisplay));
                RaiseCommandStates();
            }
        }
    }

    public string OpenStreamLabel => SelectedProtocol == VideoProtocolPreference.Automatic
        ? Text("VideoOpenAutomatic", "Open automatic")
        : $"{Text("VideoOpen", "Open stream")} {DisplayProtocol(SelectedProtocol)}";

    public VideoOverlayScene OverlayScene
    {
        get => _overlayScene;
        private set => SetProperty(ref _overlayScene, value);
    }

    public IVideoFrameSource NativeFrameSource => _frameSource;

    public string StreamStatus
    {
        get => _streamStatus;
        private set => SetProperty(ref _streamStatus, value);
    }

    public string PlaybackSummary
    {
        get => _playbackSummary;
        private set
        {
            if (SetProperty(ref _playbackSummary, value)) OnPropertyChanged(nameof(PlaybackEmptyMessage));
        }
    }

    public string PlaybackDetail
    {
        get => _playbackDetail;
        private set => SetProperty(ref _playbackDetail, value);
    }

    public string StreamEndpoint
    {
        get => _streamEndpoint;
        private set => SetProperty(ref _streamEndpoint, value);
    }

    public string GStreamerStatus
    {
        get => _gStreamerStatus;
        private set => SetProperty(ref _gStreamerStatus, value);
    }

    public string GStreamerDetail
    {
        get => _gStreamerDetail;
        private set => SetProperty(ref _gStreamerDetail, value);
    }

    public string NativeVideoStatus
    {
        get => _nativeVideoStatus;
        private set => SetProperty(ref _nativeVideoStatus, value);
    }

    public string NativeVideoDetail
    {
        get => _nativeVideoDetail;
        private set => SetProperty(ref _nativeVideoDetail, value);
    }

    public string NativeVideoMetrics
    {
        get => _nativeVideoMetrics;
        private set => SetProperty(ref _nativeVideoMetrics, value);
    }

    public bool HasNativeFrame
    {
        get => _hasNativeFrame;
        private set
        {
            if (SetProperty(ref _hasNativeFrame, value))
            {
                OnPropertyChanged(nameof(ShowPlaybackPlaceholder));
            }
        }
    }

    public bool ShowPlaybackPlaceholder => !HasNativeFrame;

    public string PlaybackEmptyMessage => HasSelectedUnit && SelectedCamera is null
        ? "No video source is configured for this unit."
        : PlaybackSummary;

    public bool HasSelectedUnit
    {
        get => _hasSelectedUnit;
        private set
        {
            if (SetProperty(ref _hasSelectedUnit, value)) OnPropertyChanged(nameof(PlaybackEmptyMessage));
        }
    }

    public bool HasConfiguredTestFile =>
        !string.IsNullOrWhiteSpace(_configuration.GStreamerTestFilePath) &&
        File.Exists(_configuration.GStreamerTestFilePath);

    public string TestFileLabel
    {
        get
        {
            var path = _configuration.GStreamerTestFilePath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? Path.GetFileName(path) ?? "Configured test file"
                : "No local test file configured";
        }
    }

    public string FallbackStatus
    {
        get => _fallbackStatus;
        private set => SetProperty(ref _fallbackStatus, value);
    }

    public string ProtocolPlanText
    {
        get => _protocolPlanText;
        private set => SetProperty(ref _protocolPlanText, value);
    }

    public string ActiveProtocolText
    {
        get => _activeProtocolText;
        private set => SetProperty(ref _activeProtocolText, value);
    }

    public string OpenStreamTooltip => Text("VideoOpen", "Open stream");
    public string RetryStreamTooltip => Text("VideoRetry", "Retry stream");
    public string CloseStreamTooltip => Text("VideoClose", "Close stream");
    public string RefreshTimelineTooltip => Text("VideoRefresh", "Refresh timeline");
    public string PlayTimelineTooltip => Text("VideoPlay", "Play");
    public string PauseTimelineTooltip => Text("VideoPause", "Pause");
    public string ResumeTimelineTooltip => Text("VideoResume", "Resume");
    public string CacheTimelineTooltip => Text("VideoCache", "Cache recording");
    public string RetainTimelineTooltip => Text("VideoRetain", "Retain recording");
    public string ExportTimelineTooltip => Text("VideoExport", "Export clip");
    public string GoLiveTooltip => Text("VideoLive", "Go live");
    public string CaptureFrameTooltip => Text("VideoCaptureFrame", "Capture frame");
    public string CaptureSourceTooltip => Text("VideoCaptureSource", "Capture source image");

    public ICommand OpenStreamCommand { get; }

    public ICommand CloseStreamCommand { get; }

    public ICommand StartTestPatternCommand { get; }

    public ICommand StartTestFileCommand { get; }

    public ICommand StopNativeVideoCommand { get; }

    public ICommand RefreshGStreamerCommand { get; }

    public ICommand RetryStreamCommand { get; }

    public ICommand PlayTimelineCommand { get; }

    public ICommand PauseTimelineCommand { get; }

    public ICommand ResumeTimelineCommand { get; }

    public ICommand GoLiveCommand { get; }

    public ICommand RetainTimelineCommand { get; }

    public ICommand RefreshTimelineCommand { get; }

    public ICommand DownloadTimelineCommand { get; }

    public ICommand CaptureDisplayedFrameCommand { get; }

    public ICommand CaptureSourceImageCommand { get; }

    public ICommand ExportTimelineClipCommand { get; }


    public bool CaptureIncludeOverlays
    {
        get => _captureIncludeOverlays;
        set => SetProperty(ref _captureIncludeOverlays, value);
    }

    public string EvidenceStatus
    {
        get => _evidenceStatus;
        private set => SetProperty(ref _evidenceStatus, value);
    }

    public string SourceImageStatus => _sourceImages.Status.Summary;

    public string SourceImageDetail => _sourceImages.Status.Detail;

    public UnifiedVideoTimelineSnapshot VideoTimeline => _localVideo.Timeline;

    public double TimelinePosition
    {
        get => _timelinePosition;
        set
        {
            if (SetProperty(ref _timelinePosition, Math.Clamp(value, 0, 1)))
            {
                RaiseTimelineCommandStates();
            }
        }
    }

    public string LocalRecordingStatus
    {
        get => _localRecordingStatus;
        private set => SetProperty(ref _localRecordingStatus, value);
    }

    public string LocalRecordingDetail
    {
        get => _localRecordingDetail;
        private set => SetProperty(ref _localRecordingDetail, value);
    }

    public string TimelineRangeText
    {
        get => _timelineRangeText;
        private set => SetProperty(ref _timelineRangeText, value);
    }

    public string RemoteRecordingStatus
    {
        get => _remoteRecordingStatus;
        private set => SetProperty(ref _remoteRecordingStatus, value);
    }

    public string RemoteRecordingDetail
    {
        get => _remoteRecordingDetail;
        private set => SetProperty(ref _remoteRecordingDetail, value);
    }

    private void Subscribe(System.Collections.IEnumerable collection)
        => ((INotifyCollectionChanged)collection).CollectionChanged += OnDataChanged;

    private void Unsubscribe(System.Collections.IEnumerable collection)
        => ((INotifyCollectionChanged)collection).CollectionChanged -= OnDataChanged;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_activeStreamUnitId is not null &&
            !ShouldCloseStreamForSelection(_activeStreamUnitId, _selection.SelectedUnitIds))
        {
            Refresh();
            return;
        }

        _ = ResetForSelectionAsync();
    }

    internal static bool ShouldCloseStreamForSelection(string activeUnitId, IReadOnlyList<string> selectedUnitIds)
        => selectedUnitIds.Count != 1 || !string.Equals(activeUnitId, selectedUnitIds[0], StringComparison.Ordinal);

    private void OnDataChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnMediaWorkflowChanged(object? sender, EventArgs e)
        => _ = _uiDispatcher.InvokeAsync(RefreshPresentation);

    private void OnUnitAssociationsChanged(object? sender, EventArgs e)
        => _ = HandleUnitAssociationsChangedAsync();

    private async Task HandleUnitAssociationsChangedAsync()
    {
        await _uiDispatcher.InvokeAsync(Refresh);
        if (_activeStream is null || Cameras.Any(camera =>
                camera.ConnectionId == _activeStream.ConnectionId && camera.CameraSourceId == _activeStream.CameraSourceId))
        {
            return;
        }

        await _streamGate.WaitAsync();
        try
        {
            await CloseActiveStreamCoreAsync(suppressErrors: true, CancellationToken.None);
            await _uiDispatcher.InvokeAsync(Refresh);
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private void OnRoutingChanged(object? sender, EventArgs e)
        => _ = HandleRoutingChangedAsync();

    private async Task HandleRoutingChangedAsync()
    {
        await _uiDispatcher.InvokeAsync(Refresh);
        var unit = GetSelectedUnit();
        var activeSource = unit is null
            ? null
            : _routing?.ForUnit(unit.Id).FirstOrDefault(item => item.Role == UnitRouteRole.Video)?.Active?.MediaSourceId;
        if (_activeStream is null || !_activeStream.ConnectionId.StartsWith("media:", StringComparison.Ordinal) ||
            string.Equals(_activeStream.CameraSourceId, activeSource, StringComparison.Ordinal) || SelectedCamera is null)
            return;

        await _streamGate.WaitAsync();
        try
        {
            await CloseActiveStreamCoreAsync(suppressErrors: true, CancellationToken.None);
            Refresh();
        }
        finally { _streamGate.Release(); }
        if (CanOpenStream()) await OpenStreamAsync(CancellationToken.None);
    }

    private void OnPlaybackChanged(object? sender, EventArgs e)
        => _ = HandlePlaybackChangedAsync();

    private async Task HandlePlaybackChangedAsync()
    {
        await _uiDispatcher.InvokeAsync(ApplyPlaybackStatus);
        var status = _playback.Status;
        if (_automaticFallback && !_closingStream && status.State is
            VideoPlaybackState.Faulted or VideoPlaybackState.Unsupported or VideoPlaybackState.Offline)
        {
            await ScheduleFallbackAsync(status.StreamId, status.Detail);
        }
    }

    private void OnNativePipelineChanged(object? sender, EventArgs e)
        => _ = _uiDispatcher.InvokeAsync(ApplyNativeStatus);

    private void OnLocalVideoChanged(object? sender, EventArgs e)
        => _ = _uiDispatcher.InvokeAsync(ApplyLocalVideoStatus);

    private void OnNativeFrameAvailable(object? sender, EventArgs e)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            ApplyNativeStatus();
            _captureDisplayedFrameCommand.RaiseCanExecuteChanged();
        });
    }

    private async Task ResetForSelectionAsync()
    {
        await _streamGate.WaitAsync();
        try
        {
            await CloseActiveStreamCoreAsync(suppressErrors: true, CancellationToken.None);
        }
        finally
        {
            _streamGate.Release();
        }

        Refresh();
    }

    private async Task CloseForCameraChangeAsync()
    {
        await _streamGate.WaitAsync();
        try
        {
            _closingStream = true;
            try { await CloseActiveStreamCoreAsync(suppressErrors: true, CancellationToken.None); }
            finally { _closingStream = false; }
        }
        finally { _streamGate.Release(); }
        await _uiDispatcher.InvokeAsync(RefreshPresentation);
    }

    private void Refresh()
    {
        var vehicle = GetSelectedVehicle();
        var unit = GetSelectedUnit();
        HasSelectedUnit = vehicle is not null && _selection.SelectedUnitIds.Count <= 1;
        var available = BuildUnitCameraOptions(_cameraSources.Items, unit, vehicle).ToList();
        var activeVideoSourceId = unit is null
            ? null
            : _routing?.ForUnit(unit.Id).FirstOrDefault(item => item.Role == UnitRouteRole.Video)?.Active?.MediaSourceId;

        var selectedId = SelectedCamera?.Id;
        Cameras.Clear();
        foreach (var camera in available)
        {
            Cameras.Add(camera);
        }

        SelectedCamera = Cameras.FirstOrDefault(item => item.CameraSourceId == activeVideoSourceId)
                         ?? Cameras.FirstOrDefault(item => item.Id == selectedId)
                         ?? Cameras.FirstOrDefault(item => item.Active)
                         ?? Cameras.FirstOrDefault();
        if (HasSelectedUnit && SelectedCamera is null)
        {
            _frameSource.SetSource(null);
            HasNativeFrame = false;
        }

        if (_activeStream is not null)
        {
            var updated = _cameraStreams.Items.FirstOrDefault(item => item.Id == _activeStream.Id);
            if (updated is not null)
            {
                _activeStream = updated;
            }

            if (_activeStream is not null && IsTerminal(_activeStream))
            {
                _ = ScheduleFallbackAsync(
                    _activeStream.StreamId,
                    $"The stream reported terminal state '{_activeStream.State}'.");
            }
        }

        RefreshPresentation();
        if (HasSelectedUnit && SelectedCamera is null)
        {
            PlaybackSummary = "No video source is configured for this unit.";
            PlaybackDetail = string.Empty;
        }
        RaiseCommandStates();
    }

    internal static IReadOnlyList<CameraSourceRecord> BuildUnitCameraOptions(
        IEnumerable<CameraSourceRecord> cameraSources,
        UnitDefinitionSnapshot? unit,
        VehicleRecord? selectedVehicle = null)
    {
        var sourceRecords = cameraSources.ToArray();
        var available = new Dictionary<string, CameraSourceRecord>(StringComparer.Ordinal);
        if (unit is not null)
        {
            foreach (var binding in unit.CameraSources)
            {
                var logicalMediaBinding = (unit.Cameras ?? []).Any(camera =>
                    camera.ControlConnectionId == binding.ConnectionId &&
                    camera.ControlCameraSourceId == binding.CameraSourceId &&
                    (!string.IsNullOrWhiteSpace(camera.MediaSourceId) || (camera.StandbyMediaSourceIds?.Count ?? 0) > 0));
                if (logicalMediaBinding) continue;
                var camera = sourceRecords.FirstOrDefault(item =>
                    item.ConnectionId == binding.ConnectionId && item.CameraSourceId == binding.CameraSourceId);
                if (camera is not null) available.TryAdd(camera.Id, camera);
            }
        }

        foreach (var binding in unit?.Cameras ?? [])
        {
            var controlCamera = binding.ControlConnectionId is null || binding.ControlCameraSourceId is null
                ? null
                : sourceRecords.FirstOrDefault(item =>
                    item.ConnectionId == binding.ControlConnectionId && item.CameraSourceId == binding.ControlCameraSourceId);
            var mediaSourceIds = new[] { binding.MediaSourceId }
                .Concat(binding.StandbyMediaSourceIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var foundMedia = false;
            foreach (var sourceId in mediaSourceIds)
            {
                var mediaCamera = sourceRecords.FirstOrDefault(item =>
                    item.ConnectionId == $"media:{sourceId}" && item.CameraSourceId == sourceId);
                if (mediaCamera is null)
                {
                    continue;
                }

                foundMedia = true;
                available.TryAdd(mediaCamera.Id, mediaCamera with
                {
                    Name = binding.Name,
                    SupportsGimbal = controlCamera?.SupportsGimbal == true,
                    SupportsPhoto = controlCamera?.SupportsPhoto == true
                });
            }

            if (!foundMedia && controlCamera is not null)
            {
                available.TryAdd(controlCamera.Id, controlCamera);
            }
        }

        if (selectedVehicle?.IsGhost == true)
        {
            foreach (var camera in sourceRecords.Where(item => selectedVehicle.ConnectionIds.Contains(item.ConnectionId, StringComparer.Ordinal) &&
                                                               item.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal)))
                available.TryAdd(camera.Id, camera);
        }

        return available.Values
            .OrderByDescending(item => item.Active)
            .ThenByDescending(item => item.Fresh)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RefreshPresentation()
    {
        var camera = SelectedCamera;
        if (camera is null)
        {
            OverlayScene = VideoOverlayScene.Empty;
            CameraSettings.Clear();
            HasCameraDefinition = false;
            CameraDefinitionSummary = string.Empty;
        }
        else
        {
            var definition = _cameraDefinitions?.Items.FirstOrDefault(item => item.Id == camera.Id);
            CameraSettings.Clear();
            if (definition is not null)
            {
                foreach (var setting in definition.Settings) CameraSettings.Add(setting);
                HasCameraDefinition = definition.Settings.Count > 0;
                CameraDefinitionSummary = string.Join(" · ", new[]
                {
                    definition.DisplayName,
                    string.IsNullOrWhiteSpace(definition.FirmwareVersion) ? null : $"FW {definition.FirmwareVersion}",
                    string.IsNullOrWhiteSpace(definition.CapabilitySummary) ? null : definition.CapabilitySummary,
                    definition.Status
                }.Where(item => !string.IsNullOrWhiteSpace(item)));
            }
            else
            {
                HasCameraDefinition = false;
                CameraDefinitionSummary = string.Empty;
            }
            var relevantTracks = _tracks.Items
                .Where(item => item.ConnectionId == camera.ConnectionId)
                .Where(item =>
                    string.IsNullOrWhiteSpace(item.CameraSourceId) ||
                    item.CameraSourceId == camera.CameraSourceId)
                .OrderByDescending(item => item.Confidence)
                .Select(item => new VideoTrackVisual(
                    item.TrackId,
                    item.ClassId,
                    item.Confidence,
                    item.CenterX,
                    item.CenterY,
                    item.Width,
                    item.Height))
                .ToArray();
            OverlayScene = new VideoOverlayScene(
                camera.CameraSourceId,
                camera.Width,
                camera.Height,
                relevantTracks);
        }

        if (_activeStream is null)
        {
            StreamStatus = Text("VideoNoStream", "No stream");
            StreamEndpoint = "-";
        }
        else
        {
            StreamStatus = FormatStreamStatus(_activeStream);
            StreamEndpoint = _activeStream.Protocol == "Ghost3D"
                ? "Simulated world scene"
                : string.IsNullOrWhiteSpace(_activeStream.StreamUrl)
                ? "Negotiation payload only"
                : GStreamerPipelineArguments.RedactEndpoint(_activeStream.StreamUrl);
        }

        ApplyPlaybackStatus();
        if (HasSelectedUnit && SelectedCamera is null)
        {
            PlaybackSummary = "No video source is configured for this unit.";
            PlaybackDetail = string.Empty;
        }
        RaiseCommandStates();
    }

    private static string FormatStreamStatus(CameraStreamRecord stream)
        => string.Equals(stream.State, "Playing", StringComparison.OrdinalIgnoreCase)
            ? $"{stream.Width}×{stream.Height} · {stream.FrameRateHz:0.#} fps"
            : string.Equals(stream.State, "Faulted", StringComparison.OrdinalIgnoreCase)
                ? $"Stream faulted: {stream.Message}"
                : stream.State;

    private async Task RefreshGStreamerAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        await _uiDispatcher.InvokeAsync(() =>
        {
            GStreamerStatus = "Inspecting GStreamer";
            GStreamerDetail = "Locating the native runtime and required plugins.";
        }, cancellationToken);

        var diagnostics = await _gStreamerRuntime.InspectAsync(
            force,
            cancellationToken);
        await _uiDispatcher.InvokeAsync(() =>
        {
            ApplyRuntimeDiagnostics(diagnostics);
            RaiseCommandStates();
        }, cancellationToken);
    }

    private async Task StartTestPatternAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _nativePipeline.StartTestPatternAsync(CreateTestOptions(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NativeVideoStatus = "Test source failed";
            NativeVideoDetail = ex.Message;
        }
        finally
        {
            ApplyNativeStatus();
        }
    }

    private async Task StartTestFileAsync(CancellationToken cancellationToken)
    {
        var path = _configuration.GStreamerTestFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await _nativePipeline.StartFileAsync(path, CreateTestOptions().Output, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NativeVideoStatus = "Local file playback failed";
            NativeVideoDetail = ex.Message;
        }
        finally
        {
            ApplyNativeStatus();
        }
    }

    private async Task StopNativeVideoAsync(CancellationToken cancellationToken)
    {
        await _nativePipeline.StopAsync(cancellationToken);
        ApplyNativeStatus();
    }

    private GStreamerTestSourceOptions CreateTestOptions()
        => new(
            _configuration.GStreamerTestWidth,
            _configuration.GStreamerTestHeight,
            _configuration.GStreamerTestFrameRate,
            _configuration.GStreamerTestPattern);

    private async Task OpenStreamAsync(CancellationToken cancellationToken)
    {
        if (!CanOpenStream())
        {
            return;
        }

        // Give the operator immediate feedback. Opening the media connection and
        // attaching the native decoder can both take long enough that otherwise
        // the video surface appears to ignore the play action.
        PlaybackSummary = Text("VideoOpeningStream", "Opening stream…");
        PlaybackDetail = Text("VideoConnectingToCamera", "Connecting to the selected camera source.");
        FallbackStatus = Text("VideoOpeningStream", "Opening stream…");

        await _streamGate.WaitAsync(cancellationToken);
        try
        {
            var camera = SelectedCamera;
            if (camera is null || _activeStream is not null)
            {
                return;
            }

            if (IsGhostCamera(camera))
            {
                await OpenGhostStreamAsync(camera, cancellationToken);
                return;
            }

            await BuildProtocolPlanAsync(camera, cancellationToken);
            _protocolPlanIndex = 0;
            _attemptedNativeProtocols.Clear();
            await OpenFromPlanCoreAsync(camera, "Opening selected camera stream.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _activeStream = null;
            _activeStreamUnitId = null;
            ActiveProtocolText = "No active protocol";
            PlaybackSummary = "Camera stream failed";
            PlaybackDetail = GStreamerPipelineArguments.RedactText(ex.Message);
            FallbackStatus = "Could not open the selected camera.";
            RefreshPresentation();
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private async Task RetryStreamAsync(CancellationToken cancellationToken)
    {
        await _streamGate.WaitAsync(cancellationToken);
        try
        {
            var camera = SelectedCamera;
            if (camera is null)
            {
                return;
            }

            _attemptedNativeProtocols.Clear();
            if (_protocolPlan.Count == 0)
            {
                await BuildProtocolPlanAsync(camera, cancellationToken);
            }
            _protocolPlanIndex = _protocolPlanIndex < 0 || _protocolPlanIndex >= _protocolPlan.Count
                ? 0
                : _protocolPlanIndex;
            await CloseActiveStreamCoreAsync(suppressErrors: true, cancellationToken, resetPlan: false);
            await OpenFromPlanCoreAsync(camera, "Operator requested an immediate retry.", cancellationToken);
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private async Task BuildProtocolPlanAsync(CameraSourceRecord camera, CancellationToken cancellationToken)
    {
        if (_connections.TryGetDefinition(camera.ConnectionId, out var mediaDefinition) && mediaDefinition?.Mode == ConnectionMode.Media)
        {
            _protocolPlan = [VideoProtocolPreference.Rtsp];
            _automaticFallback = false;
            ProtocolPlanText = "RTSP";
            return;
        }
        var diagnostics = await _gStreamerRuntime.InspectAsync(cancellationToken: cancellationToken);
        _connections.TryGetDefinition(camera.ConnectionId, out var connection);
        _protocolPlan = _protocolPolicy.BuildPlan(connection, SelectedProtocol, diagnostics);
        _automaticFallback = SelectedProtocol == VideoProtocolPreference.Automatic;
        ProtocolPlanText = _protocolPlan.Count == 0
            ? "No supported protocol candidates"
            : string.Join(" → ", _protocolPlan.Select(DisplayPreference));
    }

    private async Task OpenFromPlanCoreAsync(
        CameraSourceRecord camera,
        string reason,
        CancellationToken cancellationToken)
    {
        if (IsGhostCamera(camera))
        {
            await OpenGhostStreamAsync(camera, cancellationToken);
            return;
        }

        Exception? lastError = null;
        while (_protocolPlanIndex >= 0 && _protocolPlanIndex < _protocolPlan.Count)
        {
            var preference = _protocolPlan[_protocolPlanIndex];
            FallbackStatus = _protocolPlanIndex == 0
                ? reason
                : $"Fallback {_protocolPlanIndex + 1}/{_protocolPlan.Count}: requesting {DisplayPreference(preference)}. {reason}";
            try
            {
                var stream = await _connections.OpenCameraStreamAsync(
                    camera.ConnectionId,
                    new CameraStreamOpenRequest(camera.CameraSourceId, preference),
                    cancellationToken);
                var nativeProtocol = NativeVideoProtocolResolver.Resolve(stream);
                if (nativeProtocol != NativeVideoProtocol.Unknown &&
                    !_attemptedNativeProtocols.Add(nativeProtocol) &&
                    _automaticFallback)
                {
                    await SafeCloseRemoteStreamAsync(stream, cancellationToken);
                    _protocolPlanIndex++;
                    continue;
                }

                _activeStream = stream;
                _activeStreamUnitId = GetSelectedVehicle()?.Id;
                ActiveProtocolText = NativeVideoProtocolResolver.DisplayName(nativeProtocol);
                StreamStatus = FormatStreamStatus(stream);
                StreamEndpoint = string.IsNullOrWhiteSpace(stream.StreamUrl)
                    ? "Negotiation payload only"
                    : GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl);
                PlaybackSummary = Text("VideoStartingPlayback", "Starting video playback…");
                PlaybackDetail = Text("VideoWaitingForFrames", "Stream opened; waiting for decoded frames.");
                await _playback.AttachAsync(stream, cancellationToken);
                var state = _playback.Status.State;
                if (state is not (VideoPlaybackState.Faulted or VideoPlaybackState.Unsupported or VideoPlaybackState.Offline))
                {
                    try
                    {
                        _connections.TryGetDefinition(stream.ConnectionId, out var connection);
                        await _localVideo.BeginSessionAsync(stream, connection, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LocalRecordingStatus = "Local recording unavailable";
                        LocalRecordingDetail = GStreamerPipelineArguments.RedactText(ex.Message);
                    }
                    RefreshPresentation();
                    return;
                }

                lastError = new InvalidOperationException(_playback.Status.Detail);
                await CloseActiveStreamCoreAsync(suppressErrors: true, cancellationToken, resetPlan: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                PlaybackSummary = "Stream attempt failed";
                PlaybackDetail = GStreamerPipelineArguments.RedactText(ex.Message);
                await CloseActiveStreamCoreAsync(suppressErrors: true, cancellationToken, resetPlan: false);
            }

            if (!_automaticFallback)
            {
                break;
            }
            _protocolPlanIndex++;
        }

        FallbackStatus = $"Protocol plan exhausted. {GStreamerPipelineArguments.RedactText(lastError?.Message ?? "No candidate could be opened.")}";
        ActiveProtocolText = "No active protocol";
        RefreshPresentation();
    }

    private async Task ScheduleFallbackAsync(string? streamId, string reason)
    {
        if (!_automaticFallback || Interlocked.Exchange(ref _fallbackScheduled, 1) == 1)
        {
            return;
        }

        try
        {
            await _streamGate.WaitAsync();
            try
            {
                if (!_automaticFallback || _activeStream is null ||
                    (!string.IsNullOrWhiteSpace(streamId) && _activeStream.StreamId != streamId))
                {
                    return;
                }
                if (_protocolPlanIndex + 1 >= _protocolPlan.Count)
                {
                    FallbackStatus = $"Automatic fallback exhausted. {GStreamerPipelineArguments.RedactText(reason)}";
                    return;
                }

                var camera = SelectedCamera;
                if (camera is null)
                {
                    return;
                }

                _closingStream = true;
                try
                {
                    await CloseActiveStreamCoreAsync(suppressErrors: true, CancellationToken.None, resetPlan: false);
                }
                finally
                {
                    _closingStream = false;
                }
                _protocolPlanIndex++;
                await OpenFromPlanCoreAsync(camera, GStreamerPipelineArguments.RedactText(reason), CancellationToken.None);
            }
            finally
            {
                _streamGate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _fallbackScheduled, 0);
        }
    }

    private async Task CloseStreamAsync(CancellationToken cancellationToken)
    {
        await _streamGate.WaitAsync(cancellationToken);
        try
        {
            _closingStream = true;
            try
            {
                await CloseActiveStreamCoreAsync(suppressErrors: false, cancellationToken, resetPlan: true);
            }
            finally
            {
                _closingStream = false;
            }
            RefreshPresentation();
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private async Task CloseActiveStreamCoreAsync(
        bool suppressErrors,
        CancellationToken cancellationToken,
        bool resetPlan = true)
    {
        var stream = _activeStream;
        await StopGhostVideoAsync(cancellationToken);
        await StopLocalVideoAsync(cancellationToken);
        if (stream is null)
        {
            await _playback.DetachAsync(cancellationToken);
            if (resetPlan)
            {
                ResetProtocolPlan();
            }
            return;
        }

        try
        {
            if (_connections.TryGetDefinition(stream.ConnectionId, out var definition) && definition?.Mode == ConnectionMode.Media)
                await _connections.CloseCameraStreamAsync(stream.ConnectionId, stream.StreamId, cancellationToken);
            else if (!string.Equals(stream.Protocol, "Ghost3D", StringComparison.OrdinalIgnoreCase))
                await _connections.CloseCameraStreamAsync(stream.ConnectionId, stream.StreamId, cancellationToken);
        }
        catch (Exception ex) when (suppressErrors && ex is not OperationCanceledException)
        {
            PlaybackSummary = "Stream detached";
            PlaybackDetail = $"The previous stream could not be closed cleanly: {GStreamerPipelineArguments.RedactText(ex.Message)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PlaybackSummary = "Stream close failed";
            PlaybackDetail = GStreamerPipelineArguments.RedactText(ex.Message);
            return;
        }

        _activeStream = null;
        _activeStreamUnitId = null;
        ActiveProtocolText = "No active protocol";
        await _playback.DetachAsync(cancellationToken);
        if (resetPlan)
        {
            ResetProtocolPlan();
        }
    }

    private void ResetProtocolPlan()
    {
        _automaticFallback = false;
        _protocolPlan = [];
        _protocolPlanIndex = -1;
        _attemptedNativeProtocols.Clear();
        ProtocolPlanText = "No protocol plan";
        FallbackStatus = "Automatic fallback has not been used.";
    }

    private async Task StopLocalVideoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _localVideo.EndSessionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalRecordingStatus = "Local recorder shutdown failed";
            LocalRecordingDetail = GStreamerPipelineArguments.RedactText(ex.Message);
        }
    }

    private async Task SafeCloseRemoteStreamAsync(CameraStreamRecord stream, CancellationToken cancellationToken)
    {
        try
        {
            await _connections.CloseCameraStreamAsync(stream.ConnectionId, stream.StreamId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PlaybackDetail = $"A rejected protocol session could not be closed cleanly: {GStreamerPipelineArguments.RedactText(ex.Message)}";
        }
    }

    private bool CanOpenStream()
        => HasSelectedUnit && SelectedCamera is not null &&
           SelectedCamera.State is not (AvailabilityState.Offline or AvailabilityState.Faulted) &&
           _activeStream is null;

    private bool CanCloseStream() => _activeStream is not null;

    private static bool IsGhostCamera(CameraSourceRecord camera)
        => camera.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal);

    private async Task OpenGhostStreamAsync(CameraSourceRecord camera, CancellationToken cancellationToken)
    {
        await StopGhostVideoAsync(cancellationToken);
        if (_ghostStreams is null) throw new InvalidOperationException("The simulated Ghost video provider is not available.");
        var session = await _ghostStreams.OpenAsync(camera, cancellationToken);
        _activeStream = session.Stream;
        _activeStreamUnitId = GetSelectedVehicle()?.Id;
        _frameSource.SetSource(session.Frames);
        ActiveProtocolText = "Ghost 3D scene";
        StreamEndpoint = "Simulated world scene";
        PlaybackSummary = "Ghost camera playing";
        PlaybackDetail = "Live view from the Ghost camera pose.";
        NativeVideoStatus = "Simulated video playing";
        NativeVideoDetail = "Rendered from the shared 3D world scene.";
        ApplyNativeStatus();
        RefreshPresentation();
    }

    private async Task StopGhostVideoAsync(CancellationToken cancellationToken)
    {
        if (_activeStream is { Protocol: "Ghost3D" } stream && _ghostStreams is not null)
            await _ghostStreams.CloseAsync(stream, cancellationToken);
        _frameSource.SetSource(_localVideo.PresentationFrames);
    }

    private bool CanRetryStream()
        => HasSelectedUnit && SelectedCamera is not null &&
           (_playback.Status.State is VideoPlaybackState.Faulted or VideoPlaybackState.Unsupported or VideoPlaybackState.Offline ||
            (_activeStream is null && _protocolPlan.Count > 0 && _protocolPlanIndex >= _protocolPlan.Count));

    private bool CanStartNativeVideo()
        => _activeStream is null &&
           _gStreamerRuntime.Diagnostics.Available &&
           _nativePipeline.Status.State is not (
               NativeVideoPipelineState.InspectingRuntime or
               NativeVideoPipelineState.Starting or
               NativeVideoPipelineState.Playing or
               NativeVideoPipelineState.Stopping);

    private bool CanStartTestFile() => CanStartNativeVideo() && HasConfiguredTestFile;

    private bool CanStopNativeVideo()
        => _activeStream is null &&
           _nativePipeline.Status.State is
            NativeVideoPipelineState.InspectingRuntime or
            NativeVideoPipelineState.Starting or
            NativeVideoPipelineState.Playing or
            NativeVideoPipelineState.Faulted;

    private VehicleRecord? GetSelectedVehicle()
    {
        var current = _selection.Current;
        return current.Kind == SelectionKind.Vehicle &&
               !string.IsNullOrWhiteSpace(current.Id) &&
               _vehicles.TryGet(current.Id, out var vehicle)
            ? vehicle
            : null;
    }

    private UnitDefinitionSnapshot? GetSelectedUnit()
    {
        if (_selection.SelectedUnitIds.Count > 1)
        {
            return null;
        }

        var vehicle = GetSelectedVehicle();
        return vehicle is null ? null : _unitAssociations?.FindByVehicle(vehicle.Id);
    }

    private void ApplyPlaybackStatus()
    {
        if (_activeStream is { Protocol: "Ghost3D" } ghostStream)
        {
            PlaybackSummary = ghostStream.State == "Faulted" ? "Ghost camera stream faulted" : "Ghost camera playing";
            PlaybackDetail = ghostStream.State == "Faulted" ? ghostStream.Message : "Live view from the Ghost camera pose.";
            _retryStreamCommand.RaiseCanExecuteChanged();
            RaiseTimelineCommandStates();
            return;
        }
        var status = _playback.Status;
        PlaybackSummary = LocalizeVideoText(status.Summary);
        PlaybackDetail = LocalizeVideoText(status.Detail);
        if (!string.IsNullOrWhiteSpace(status.Protocol))
        {
            ActiveProtocolText = status.Protocol;
        }
        if (status.State == VideoPlaybackState.Live && _protocolPlanIndex > 0)
        {
            FallbackStatus = $"Live after fallback {_protocolPlanIndex + 1}/{_protocolPlan.Count}.";
        }
        _retryStreamCommand.RaiseCanExecuteChanged();
        RaiseTimelineCommandStates();
    }

    private void ApplyRuntimeDiagnostics(GStreamerRuntimeDiagnostics diagnostics)
    {
        GStreamerStatus = diagnostics.Summary;
        GStreamerDetail = diagnostics.Detail;
    }

    private void ApplyNativeStatus()
    {
        ApplyRuntimeDiagnostics(_gStreamerRuntime.Diagnostics);
        if (_activeStream is { Protocol: "Ghost3D" } ghostStream)
        {
            NativeVideoStatus = ghostStream.State == "Faulted" ? "Simulated video faulted" : "Simulated video playing";
            NativeVideoDetail = ghostStream.State == "Faulted" ? ghostStream.Message : "Rendered from the shared 3D world scene.";
            var ghostInfo = _frameSource.LatestInfo;
            HasNativeFrame = ghostInfo is not null;
            NativeVideoMetrics = ghostInfo is null
                ? Text("VideoNoDecodedFrame", "No decoded frame")
                : $"{ghostInfo.Width}×{ghostInfo.Height} · BGRA · frame {ghostInfo.Sequence} · {ghostInfo.Timestamp.ToLocalTime():HH:mm:ss.fff}";
            RaiseCommandStates();
            return;
        }
        var status = _nativePipeline.Status;
        NativeVideoStatus = LocalizeVideoText(status.Summary);
        NativeVideoDetail = LocalizeVideoText(status.Detail);
        var info = _frameSource.LatestInfo;
        HasNativeFrame = info is not null;
        NativeVideoMetrics = info is null
            ? Text("VideoNoDecodedFrame", "No decoded frame")
            : $"{info.Width}×{info.Height} · BGRA · frame {info.Sequence} · {info.Timestamp.ToLocalTime():HH:mm:ss.fff}";
        RaiseCommandStates();
    }

    private async Task InitializeLocalVideoAsync()
    {
        try
        {
            await _localVideo.RefreshAsync();
            await _uiDispatcher.InvokeAsync(ApplyLocalVideoStatus);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                LocalRecordingStatus = "Video timeline unavailable";
                LocalRecordingDetail = GStreamerPipelineArguments.RedactText(ex.Message);
            });
        }
    }

    private Task PlayTimelineAsync(CancellationToken cancellationToken)
        => RunTimelineActionAsync(
            token => _localVideo.PlaySelectedAsync(TimelinePosition, token),
            "Local playback failed",
            cancellationToken);

    private Task PauseTimelineAsync(CancellationToken cancellationToken)
        => RunTimelineActionAsync(
            _localVideo.PauseAsync,
            "Could not pause local playback",
            cancellationToken);

    private Task ResumeTimelineAsync(CancellationToken cancellationToken)
        => RunTimelineActionAsync(
            _localVideo.ResumeAsync,
            "Could not resume local playback",
            cancellationToken);

    private async Task GoLiveAsync(CancellationToken cancellationToken)
    {
        await RunTimelineActionAsync(
            _localVideo.GoLiveAsync,
            "Could not return to live video",
            cancellationToken);
        if (_localVideo.Timeline.Mode == LocalVideoTimelineMode.Live)
        {
            TimelinePosition = 1;
        }
    }

    private Task RetainTimelineAsync(CancellationToken cancellationToken)
        => RunTimelineActionAsync(
            token => _localVideo.RetainSelectedAsync(TimelinePosition, token),
            "Could not retain the selected segment",
            cancellationToken);

    private Task RefreshTimelineAsync(CancellationToken cancellationToken)
        => RunTimelineActionAsync(
            _localVideo.RefreshAsync,
            "Could not refresh video recordings",
            cancellationToken);

    private Task DownloadTimelineAsync(CancellationToken cancellationToken)
        => RunTimelineActionAsync(
            token => _localVideo.DownloadSelectedAsync(TimelinePosition, token),
            "Could not cache the selected vehicle recording",
            cancellationToken);

    private async Task CaptureDisplayedFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var source = NativeFrameSource;
            VideoFrameInfo? copiedInfo = null;
            byte[]? pixels = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var info = source.LatestInfo;
                if (info is null) break;
                pixels = new byte[info.RequiredBytes];
                if (source.TryCopyLatest(pixels, out copiedInfo) && copiedInfo is not null) break;
                copiedInfo = null;
            }
            if (pixels is null || copiedInfo is null)
            {
                EvidenceStatus = "No decoded frame is available to capture.";
                return;
            }

            var request = new DisplayedFrameCaptureRequest(
                pixels,
                copiedInfo,
                OverlayScene,
                CaptureIncludeOverlays,
                BuildEvidenceContext(mediaTimestamp: copiedInfo.Timestamp));
            var record = await _evidence.CaptureDisplayedFrameAsync(request, cancellationToken);
            EvidenceStatus = $"Captured {record.FileName}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EvidenceStatus = $"Displayed-frame capture failed: {GStreamerPipelineArguments.RedactText(ex.Message)}";
        }
    }

    private async Task CaptureSourceImageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var camera = SelectedCamera;
            if (camera is null)
            {
                EvidenceStatus = "Select a camera source first.";
                return;
            }
            var result = await _sourceImages.CaptureAsync(
                new SourceImageCaptureRequest(
                    camera.ConnectionId,
                    camera.CameraSourceId,
                    BuildEvidenceContext()),
                cancellationToken);
            EvidenceStatus = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EvidenceStatus = $"Source-image capture failed: {GStreamerPipelineArguments.RedactText(ex.Message)}";
        }
    }

    private async Task ExportTimelineClipAsync(CancellationToken cancellationToken)
    {
        try
        {
            var selected = VideoTimeline.ItemAt(TimelinePosition);
            if (selected is null)
            {
                EvidenceStatus = "Select a recording on the timeline first.";
                return;
            }
            if (selected.CanDownload)
            {
                var selectedSourceId = selected.SourceId ?? selected.Id;
                await _localVideo.DownloadSelectedAsync(TimelinePosition, cancellationToken);
                selected = _localVideo.Timeline.Items.FirstOrDefault(item =>
                    string.Equals(item.SourceId ?? item.Id, selectedSourceId, StringComparison.Ordinal));
            }
            if (selected is null || string.IsNullOrWhiteSpace(selected.LocalPath) || !File.Exists(selected.LocalPath))
            {
                EvidenceStatus = "The selected recording is not available locally for export.";
                return;
            }

            var context = BuildEvidenceContext(
                selected,
                selected.StartedAt);
            var record = await _evidence.ExportClipAsync(
                new VideoClipExportRequest(
                    selected.LocalPath,
                    selected.StartedAt,
                    selected.EndedAt,
                    context,
                    Path.GetExtension(selected.LocalPath)),
                cancellationToken);
            EvidenceStatus = $"Exported {record.FileName}.";
            ApplyLocalVideoStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            EvidenceStatus = $"Clip export failed: {GStreamerPipelineArguments.RedactText(ex.Message)}";
        }
    }

    private EvidenceCaptureContext BuildEvidenceContext(
        VideoTimelineItem? timelineItem = null,
        DateTimeOffset? mediaTimestamp = null)
    {
        var vehicle = GetSelectedVehicle();
        var telemetry = vehicle is null
            ? null
            : _telemetry.Items
                .Where(item => item.VehicleId == vehicle.Id)
                .OrderBy(item => item.IsStale)
                .ThenByDescending(item => item.ObservedAt)
                .FirstOrDefault();
        var task = vehicle is null
            ? null
            : _tasks.Items
                .Where(item => item.AssignedVehicleId == vehicle.Id)
                .OrderByDescending(item => ActiveStateRank(item.State))
                .ThenByDescending(item => item.ObservedAt)
                .FirstOrDefault();
        var taskMissionId = task?.MissionId;
        var mission = !string.IsNullOrWhiteSpace(taskMissionId)
            ? _missions.Items.FirstOrDefault(item => item.Id == taskMissionId)
            : vehicle is null
                ? null
                : _missions.Items
                    .Where(item => item.AssignedVehicleId == vehicle.Id)
                    .OrderByDescending(item => ActiveStateRank(item.State))
                    .ThenByDescending(item => item.ObservedAt)
                    .FirstOrDefault();
        var stream = _activeStream;
        var camera = SelectedCamera;
        return new EvidenceCaptureContext(
            stream?.ConnectionId ?? camera?.ConnectionId ?? telemetry?.ConnectionId,
            stream?.LogosInstanceId ?? camera?.LogosInstanceId ?? telemetry?.LogosInstanceId ?? vehicle?.LogosInstanceId,
            vehicle?.Id,
            vehicle?.Name,
            timelineItem?.CameraSourceId ?? camera?.CameraSourceId,
            timelineItem?.StreamId ?? stream?.StreamId,
            timelineItem?.Protocol ?? stream?.Protocol,
            mission?.Id,
            task?.Id,
            telemetry?.LatitudeDegrees,
            telemetry?.LongitudeDegrees,
            telemetry?.AltitudeMslMetres,
            telemetry?.AltitudeAglMetres,
            telemetry?.HeadingDegrees,
            OverlayScene.Tracks.FirstOrDefault(item => item.Selected)?.TrackId,
            mediaTimestamp,
            timelineItem?.Id);
    }

    private static int ActiveStateRank(string state)
        => state.Contains("running", StringComparison.OrdinalIgnoreCase) ||
           state.Contains("execut", StringComparison.OrdinalIgnoreCase) ||
           state.Contains("progress", StringComparison.OrdinalIgnoreCase)
            ? 2
            : state.Contains("planned", StringComparison.OrdinalIgnoreCase) ||
              state.Contains("assigned", StringComparison.OrdinalIgnoreCase) ||
              state.Contains("ready", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

    private async Task RunTimelineActionAsync(
        Func<CancellationToken, Task> action,
        string failureSummary,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
            ApplyLocalVideoStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalRecordingStatus = failureSummary;
            LocalRecordingDetail = GStreamerPipelineArguments.RedactText(ex.Message);
            RaiseTimelineCommandStates();
        }
    }

    private bool CanPlayTimeline()
        => VideoTimeline.HasItems && VideoTimeline.ItemAt(TimelinePosition)?.CanPlay == true;

    private bool CanPauseTimeline() => VideoTimeline.Mode == LocalVideoTimelineMode.Playback;

    private bool CanResumeTimeline() => VideoTimeline.Mode == LocalVideoTimelineMode.Paused;

    private bool CanGoLive() => VideoTimeline.Mode != LocalVideoTimelineMode.Live;

    private bool CanRetainTimeline() => VideoTimeline.ItemAt(TimelinePosition)?.CanPlay == true;

    private bool CanDownloadTimeline() => VideoTimeline.ItemAt(TimelinePosition)?.CanDownload == true;

    private bool CanCaptureDisplayedFrame() => NativeFrameSource.LatestInfo is not null;

    private bool CanCaptureSourceImage()
        => _sourceImages.Status.Available && SelectedCamera is not null;

    private bool CanExportTimelineClip()
    {
        var item = VideoTimeline.ItemAt(TimelinePosition);
        return item?.CanPlay == true;
    }

    private void ApplyLocalVideoStatus()
    {
        var status = _localVideo.LocalStatus;
        var remoteStatus = _localVideo.RemoteStatus;
        var timeline = _localVideo.Timeline;
        LocalRecordingStatus = LocalizeVideoText(status.Summary);
        LocalRecordingDetail = LocalizeVideoText(status.Detail);
        RemoteRecordingStatus = remoteStatus.Summary;
        RemoteRecordingDetail = remoteStatus.Detail;
        OnPropertyChanged(nameof(VideoTimeline));
        if (timeline.Mode == LocalVideoTimelineMode.Live)
        {
            _timelinePosition = 1;
            OnPropertyChanged(nameof(TimelinePosition));
        }
        TimelineRangeText = timeline.RangeStart is null || timeline.RangeEnd is null
            ? Text("VideoNoRecordings", "No recordings")
            : $"{timeline.RangeStart.Value.ToLocalTime():HH:mm:ss} – {timeline.RangeEnd.Value.ToLocalTime():HH:mm:ss} · {timeline.Items.Count} item(s)";
        ApplyNativeStatus();
        RaiseTimelineCommandStates();
    }

    private void RaiseTimelineCommandStates()
    {
        _playTimelineCommand.RaiseCanExecuteChanged();
        _pauseTimelineCommand.RaiseCanExecuteChanged();
        _resumeTimelineCommand.RaiseCanExecuteChanged();
        _goLiveCommand.RaiseCanExecuteChanged();
        _retainTimelineCommand.RaiseCanExecuteChanged();
        _downloadTimelineCommand.RaiseCanExecuteChanged();
        _exportTimelineClipCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        _openStreamCommand.RaiseCanExecuteChanged();
        _closeStreamCommand.RaiseCanExecuteChanged();
        _startTestPatternCommand.RaiseCanExecuteChanged();
        _startTestFileCommand.RaiseCanExecuteChanged();
        _stopNativeVideoCommand.RaiseCanExecuteChanged();
        _retryStreamCommand.RaiseCanExecuteChanged();
        _captureDisplayedFrameCommand.RaiseCanExecuteChanged();
        _captureSourceImageCommand.RaiseCanExecuteChanged();
        _exportTimelineClipCommand.RaiseCanExecuteChanged();
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var name in new[]
        {
            nameof(OpenStreamTooltip), nameof(RetryStreamTooltip), nameof(CloseStreamTooltip),
            nameof(RefreshTimelineTooltip), nameof(PlayTimelineTooltip), nameof(PauseTimelineTooltip),
            nameof(ResumeTimelineTooltip), nameof(CacheTimelineTooltip), nameof(RetainTimelineTooltip),
            nameof(ExportTimelineTooltip), nameof(GoLiveTooltip), nameof(CaptureFrameTooltip),
            nameof(CaptureSourceTooltip), nameof(OpenStreamLabel),
            nameof(ProtocolOptions), nameof(SelectedProtocolDisplay)
        })
        {
            OnPropertyChanged(name);
        }

        RefreshPresentation();
    }

    private string Text(string key, string fallback)
        => _localization?.Get(key) ?? fallback;

    private string LocalizeVideoText(string value)
        => value switch
        {
            "No stream" => Text("VideoNoStream", value),
            "Open a camera stream to start playback." => Text("VideoOpenStreamHint", value),
            "Local recording stopped" => Text("VideoLocalRecordingStopped", value),
            "Open a camera stream to start the configured console-side rolling buffer." => Text("VideoLocalRecordingHint", value),
            "Native video stopped" => Text("VideoNativeStopped", value),
            "No decoded frame" => Text("VideoNoDecodedFrame", value),
            "No console or vehicle recordings" => Text("VideoNoRecordings", value),
            _ => value
        };

    private string DisplayProtocol(VideoProtocolPreference preference)
        => preference switch
        {
            VideoProtocolPreference.Automatic => Text("VideoProtocolAutomatic", "Automatic"),
            VideoProtocolPreference.Rtsp => Text("VideoProtocolRtsp", "RTSP"),
            VideoProtocolPreference.Hls => Text("VideoProtocolHls", "HLS"),
            VideoProtocolPreference.WebRtc => Text("VideoProtocolWebRtc", "WebRTC/WHEP"),
            _ => preference.ToString()
        };

    private static string DisplayPreference(VideoProtocolPreference preference)
        => preference switch
        {
            VideoProtocolPreference.Automatic => "Server automatic",
            VideoProtocolPreference.WebRtc => "WebRTC/WHEP",
            VideoProtocolPreference.Hls => "HLS",
            VideoProtocolPreference.Rtsp => "RTSP",
            _ => preference.ToString()
        };

    private static bool IsTerminal(CameraStreamRecord stream)
        => stream.State.Contains("Closed", StringComparison.OrdinalIgnoreCase) ||
           stream.State.Contains("Failed", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _selection.Changed -= OnSelectionChanged;
        if (_unitAssociations is not null) _unitAssociations.Changed -= OnUnitAssociationsChanged;
        if (_routing is not null) _routing.Changed -= OnRoutingChanged;
        if (_mediaWorkflow is not null) _mediaWorkflow.Changed -= OnMediaWorkflowChanged;
        if (_localization is not null) _localization.PropertyChanged -= OnLocalizationChanged;
        _playback.Changed -= OnPlaybackChanged;
        _nativePipeline.Changed -= OnNativePipelineChanged;
        _localVideo.Changed -= OnLocalVideoChanged;
        _frameSource.FrameAvailable -= OnNativeFrameAvailable;
        Unsubscribe(_vehicles.Items);
        Unsubscribe(_telemetry.Items);
        Unsubscribe(_missions.Items);
        Unsubscribe(_tasks.Items);
        Unsubscribe(_cameraSources.Items);
        if (_cameraDefinitions is not null) Unsubscribe(_cameraDefinitions.Items);
        Unsubscribe(_cameraStreams.Items);
        Unsubscribe(_tracks.Items);
        _streamGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
