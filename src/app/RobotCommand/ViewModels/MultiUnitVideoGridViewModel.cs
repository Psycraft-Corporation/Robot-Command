using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Media;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Reconciliation;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed record VideoGridUnitOption(string Id, string Name);

public sealed class MultiUnitVideoGridViewModel : ObservableObject, IDisposable
{
    public const int MaximumVisibleTiles = 5;

    private readonly ISelectionService _selection;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, CameraSourceRecord> _cameraSources;
    private readonly IEntityStore<string, CameraStreamRecord> _streams;
    private readonly IUnitAssociationWorkflow? _units;
    private readonly IUnitRoutingWorkflow? _routing;
    private readonly ILogosConnectionManager _connections;
    private readonly IGStreamerRuntime _runtime;
    private readonly IVideoPlaybackAdapterFactory _playbackFactory;
    private readonly IVideoProtocolPolicy _protocolPolicy;
    private readonly IUiDispatcher? _dispatcher;
    private readonly IGhostCameraStreamProvider? _ghostStreams;
    private readonly ObservableCollection<MultiUnitVideoTileViewModel> _tiles = [];
    private readonly ObservableCollection<VideoGridUnitOption> _overflow = [];
    private MultiUnitVideoTileViewModel? _focusedTile;
    private bool _isVisible;
    private bool _disposed;

    public MultiUnitVideoGridViewModel(
        ISelectionService selection,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, CameraSourceRecord> cameraSources,
        IEntityStore<string, CameraStreamRecord> streams,
        ILogosConnectionManager connections,
        IGStreamerRuntime runtime,
        IVideoPlaybackAdapterFactory playbackFactory,
        IVideoProtocolPolicy protocolPolicy,
        IUiDispatcher? dispatcher = null,
        IUnitAssociationWorkflow? units = null,
        IUnitRoutingWorkflow? routing = null,
        IGhostCameraStreamProvider? ghostStreams = null)
    {
        _selection = selection;
        _vehicles = vehicles;
        _cameraSources = cameraSources;
        _streams = streams;
        _connections = connections;
        _runtime = runtime;
        _playbackFactory = playbackFactory;
        _protocolPolicy = protocolPolicy;
        _dispatcher = dispatcher;
        _units = units;
        _routing = routing;
        _ghostStreams = ghostStreams;
        Tiles = new ReadOnlyObservableCollection<MultiUnitVideoTileViewModel>(_tiles);
        OverflowUnits = new ReadOnlyObservableCollection<VideoGridUnitOption>(_overflow);
        FocusTileCommand = new RelayCommand(parameter => Focus(parameter as MultiUnitVideoTileViewModel));
        SwapTileCommand = new RelayCommand(parameter => _ = SwapAsync(parameter as MultiUnitVideoTileViewModel));
        _selection.Changed += OnSelectionChanged;
        ((INotifyCollectionChanged)_cameraSources.Items).CollectionChanged += OnSourcesChanged;
        ((INotifyCollectionChanged)_streams.Items).CollectionChanged += OnStreamsChanged;
        if (_units is not null) _units.Changed += OnSelectionChanged;
        if (_routing is not null) _routing.Changed += OnRoutingChanged;
        RefreshSelection();
    }

    public ReadOnlyObservableCollection<MultiUnitVideoTileViewModel> Tiles { get; }
    public ReadOnlyObservableCollection<VideoGridUnitOption> OverflowUnits { get; }
    public ICommand FocusTileCommand { get; }
    public ICommand SwapTileCommand { get; }
    internal void FocusTile(MultiUnitVideoTileViewModel tile) => Focus(tile);

    public MultiUnitVideoTileViewModel? FocusedTile
    {
        get => _focusedTile;
        private set
        {
            if (_focusedTile is not null) _focusedTile.IsFocused = false;
            if (!SetProperty(ref _focusedTile, value)) return;
            if (_focusedTile is not null) _focusedTile.IsFocused = true;
            OnPropertyChanged(nameof(HasFocusedTile));
        }
    }

    public bool HasFocusedTile => FocusedTile is not null;

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    private void Focus(MultiUnitVideoTileViewModel? tile)
    {
        if (tile is not null && _tiles.Contains(tile)) FocusedTile = tile;
    }

    private async Task SwapAsync(MultiUnitVideoTileViewModel? tile)
    {
        if (tile is null || string.IsNullOrWhiteSpace(tile.SelectedSwapUnitId)) return;
        var replacementId = tile.SelectedSwapUnitId;
        if (!_selection.SelectedUnitIds.Contains(replacementId, StringComparer.Ordinal)) return;
        await tile.CloseAsync();
        if (ReferenceEquals(FocusedTile, tile)) FocusedTile = null;
        var option = _overflow.FirstOrDefault(item => item.Id == replacementId);
        if (option is null) return;
        tile.SetUnit(option.Id, option.Name, ResolveCamera(option.Id));
        RefreshOverflow();
    }

    private void RefreshSelection()
    {
        if (_disposed) return;
        var selected = _selection.SelectedUnitIds.Distinct(StringComparer.Ordinal).ToArray();
        IsVisible = selected.Length > 1;
        if (!IsVisible)
        {
            FocusedTile = null;
            foreach (var tile in _tiles.ToArray()) _ = RemoveTileAsync(tile);
            RefreshOverflow();
            return;
        }

        var selectedSet = selected.ToHashSet(StringComparer.Ordinal);
        foreach (var tile in _tiles.Where(tile => !selectedSet.Contains(tile.UnitId)).ToArray())
            _ = RemoveTileAsync(tile);

        // Keep visible tile order stable as long as those units remain selected.
        var present = _tiles.Select(tile => tile.UnitId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in selected.Where(id => !present.Contains(id)).Take(MaximumVisibleTiles - _tiles.Count))
        {
            var vehicle = ResolveVehicle(id);
            var name = ResolveUnitName(id, vehicle);
            var tile = new MultiUnitVideoTileViewModel(this, id, name, ResolveCamera(id), _connections,
                _runtime, _playbackFactory, _protocolPolicy, _dispatcher, _ghostStreams);
            _tiles.Add(tile);
            present.Add(id);
        }

        foreach (var tile in _tiles)
        {
            var nextCamera = ResolveCamera(tile.UnitId);
            if (tile.Camera?.Id != nextCamera?.Id)
                _ = tile.UpdateCameraAsync(nextCamera);
        }

        RefreshOverflow();
    }

    private void RefreshOverflow()
    {
        var visible = _tiles.Select(tile => tile.UnitId).ToHashSet(StringComparer.Ordinal);
        var next = _selection.SelectedUnitIds
            .Where(id => !visible.Contains(id))
            .Select(id => new VideoGridUnitOption(id, ResolveUnitName(id, ResolveVehicle(id))))
            .ToArray();
        _overflow.Clear();
        foreach (var item in next) _overflow.Add(item);
        foreach (var tile in _tiles)
        {
            var selected = tile.SelectedSwapUnitId;
            tile.SwapOptions.Clear();
            foreach (var item in _overflow) tile.SwapOptions.Add(item);
            tile.SelectedSwapUnitId = _overflow.Any(item => item.Id == selected) ? selected : null;
            tile.OnOverflowChanged();
        }
    }

    private CameraSourceRecord? ResolveCamera(string selectionId)
    {
        var unit = _units?.FindByVehicle(selectionId);
        if (unit is null && _units?.TryGet(selectionId, out var directUnit) == true) unit = directUnit;
        var vehicle = ResolveVehicle(selectionId);
        var options = CameraPanelViewModel.BuildUnitCameraOptions(_cameraSources.Items, unit, vehicle);

        // Ghost cameras are intrinsic to their simulated vehicle, not an independently
        // configured video route. Resolve only the camera owned by this Ghost; hardware
        // cameras below continue to require the unit's active Video route.
        if (vehicle?.IsGhost == true)
        {
            return options.FirstOrDefault(camera =>
                camera.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal) &&
                vehicle.ConnectionIds.Contains(camera.ConnectionId, StringComparer.Ordinal));
        }

        var activeVideo = unit is null
            ? null
            : _routing?.ForUnit(unit.Id).FirstOrDefault(item => item.Role == UnitRouteRole.Video)?.Active;
        if (activeVideo is null) return null;
        var sourceId = activeVideo.MediaSourceId ?? activeVideo.CameraSourceId;
        return string.IsNullOrWhiteSpace(sourceId)
            ? null
            : options.FirstOrDefault(camera => camera.CameraSourceId == sourceId);
    }

    private VehicleRecord? ResolveVehicle(string selectionId)
        => _vehicles.TryGet(selectionId, out var vehicle)
            ? vehicle
            : _units?.FindByVehicle(selectionId) is { } unit
                ? _vehicles.Items.FirstOrDefault(item => unit.VehicleSources.Any(source => source.VehicleId == item.Id))
                : null;

    private string ResolveUnitName(string id, VehicleRecord? vehicle)
    {
        var unit = _units?.FindByVehicle(id);
        if (unit is null && _units?.TryGet(id, out var direct) == true) unit = direct;
        return unit?.DisplayName ?? vehicle?.Name ?? id;
    }

    private async Task RemoveTileAsync(MultiUnitVideoTileViewModel tile)
    {
        await tile.CloseAsync();
        tile.Dispose();
        _tiles.Remove(tile);
        if (ReferenceEquals(FocusedTile, tile)) FocusedTile = null;
        RefreshOverflow();
        if (_selection.SelectedUnitIds.Count > 1) RefreshSelection();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => RunOnUi(RefreshSelection);
    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RunOnUi(RefreshSelection);

    private void OnStreamsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RunOnUi(() =>
        {
            foreach (var tile in _tiles) tile.RefreshStreamStatus(_streams.Items);
        });
    }

    private void OnRoutingChanged(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            foreach (var tile in _tiles)
                _ = tile.UpdateCameraAsync(ResolveCamera(tile.UnitId));
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess()) action();
        else _ = _dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selection.Changed -= OnSelectionChanged;
        ((INotifyCollectionChanged)_cameraSources.Items).CollectionChanged -= OnSourcesChanged;
        ((INotifyCollectionChanged)_streams.Items).CollectionChanged -= OnStreamsChanged;
        if (_units is not null) _units.Changed -= OnSelectionChanged;
        if (_routing is not null) _routing.Changed -= OnRoutingChanged;
        foreach (var tile in _tiles.ToArray()) _ = RemoveTileAsync(tile);
    }
}

public sealed class MultiUnitVideoTileViewModel : ObservableObject, IDisposable
{
    private readonly MultiUnitVideoGridViewModel _owner;
    private readonly ILogosConnectionManager _connections;
    private readonly IGStreamerRuntime _runtime;
    private readonly IVideoPlaybackAdapterFactory _playbackFactory;
    private readonly IVideoProtocolPolicy _protocolPolicy;
    private readonly IUiDispatcher? _dispatcher;
    private readonly IGhostCameraStreamProvider? _ghostStreams;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SwitchableVideoFrameSource _frameSource = new();
    private VideoPlaybackSession? _playback;
    private GhostCameraStreamSession? _ghostSession;
    private CameraStreamRecord? _stream;
    private CameraSourceRecord? _camera;
    private string _unitId;
    private string _unitName;
    private string _status = "Closed";
    private string _detail = "";
    private bool _isFocused;
    private string? _selectedSwapUnitId;
    private int _disposed;

    internal MultiUnitVideoTileViewModel(
        MultiUnitVideoGridViewModel owner,
        string unitId,
        string unitName,
        CameraSourceRecord? camera,
        ILogosConnectionManager connections,
        IGStreamerRuntime runtime,
        IVideoPlaybackAdapterFactory playbackFactory,
        IVideoProtocolPolicy protocolPolicy,
        IUiDispatcher? dispatcher,
        IGhostCameraStreamProvider? ghostStreams)
    {
        _owner = owner;
        _unitId = unitId;
        _unitName = unitName;
        _camera = camera;
        _connections = connections;
        _runtime = runtime;
        _playbackFactory = playbackFactory;
        _protocolPolicy = protocolPolicy;
        _dispatcher = dispatcher;
        _ghostStreams = ghostStreams;
        SwapOptions = [];
        OpenCommand = new AsyncRelayCommand(OpenAsync, CanOpen);
        CloseCommand = new AsyncRelayCommand(CloseAsync, () => _stream is not null || _playback is not null || _ghostSession is not null);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => Camera is not null && _status == "Faulted");
        FocusCommand = new RelayCommand(_ => _owner.FocusTile(this));
        SwapCommand = _owner.SwapTileCommand;
        SetStatus(camera is null ? "No source" : "Closed", "");
    }

    public string UnitId { get => _unitId; private set => SetProperty(ref _unitId, value); }
    public string UnitName { get => _unitName; private set => SetProperty(ref _unitName, value); }
    public CameraSourceRecord? Camera { get => _camera; private set { if (SetProperty(ref _camera, value)) RaiseCommands(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool IsFocused { get => _isFocused; internal set => SetProperty(ref _isFocused, value); }
    public string? SelectedSwapUnitId { get => _selectedSwapUnitId; set => SetProperty(ref _selectedSwapUnitId, value); }
    public ObservableCollection<VideoGridUnitOption> SwapOptions { get; }
    public IVideoFrameSource Frames => _frameSource;
    public bool HasCamera => Camera is not null;
    public bool IsGhost => Camera?.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal) == true;
    public bool ShowPlaybackPlaceholder => Status != "Playing";
    public bool HasSwapOptions => SwapOptions.Count > 0;
    public string EmptyMessage => !HasCamera ? "No video source is configured for this unit." :
        Status == "Faulted" ? "Stream unavailable" : Status == "Opening" ? "Opening stream…" : "Stream closed";
    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand FocusCommand { get; }
    public ICommand SwapCommand { get; }

    internal void OnOverflowChanged() => OnPropertyChanged(nameof(HasSwapOptions));

    private bool CanOpen() => Camera is not null && _stream is null && Status == "Closed";

    internal void SetUnit(string id, string name, CameraSourceRecord? camera)
    {
        UnitId = id;
        UnitName = name;
        Camera = camera;
        SetStatus(camera is null ? "No source" : "Closed", camera is null ? EmptyMessage : "");
    }

    internal async Task UpdateCameraAsync(CameraSourceRecord? camera)
    {
        if (Camera?.Id == camera?.Id) return;
        var wasOpen = _stream is not null || Status == "Opening";
        await CloseAsync();
        Camera = camera;
        SetStatus(camera is null ? "No source" : "Closed", camera is null ? EmptyMessage : "");
        if (wasOpen && camera is not null) await OpenAsync(CancellationToken.None);
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        var camera = Camera;
        if (camera is null || _stream is not null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Camera?.Id != camera.Id || _stream is not null) return;
            SetStatus("Opening", "Connecting to camera.");
            if (camera.ConnectionId.StartsWith("ghost-connection-", StringComparison.Ordinal) && _ghostStreams is not null)
            {
                _ghostSession = await _ghostStreams.OpenAsync(camera, cancellationToken);
                _stream = _ghostSession.Stream;
                _frameSource.SetSource(_ghostSession.Frames);
                SetStatus(_stream.State, _stream.Message);
                return;
            }

            _connections.TryGetDefinition(camera.ConnectionId, out var definition);
            var requested = definition?.Mode == ConnectionMode.Media
                ? new[] { VideoProtocolPreference.Rtsp }
                : _protocolPolicy.BuildPlan(definition, VideoProtocolPreference.Automatic,
                    await _runtime.InspectAsync(cancellationToken: cancellationToken));
            if (requested.Count == 0) throw new InvalidOperationException("No supported video protocol is available.");

            Exception? lastError = null;
            foreach (var preference in requested)
            {
                CameraStreamRecord? stream = null;
                try
                {
                    stream = await _connections.OpenCameraStreamAsync(camera.ConnectionId,
                        new CameraStreamOpenRequest(camera.CameraSourceId, preference), cancellationToken);
                    var session = _playbackFactory.Create();
                    _playback = session;
                    _playback.Playback.Changed += OnPlaybackChanged;
                    _stream = stream;
                    await _playback.Playback.AttachAsync(stream, cancellationToken);
                    _frameSource.SetSource(_playback.Frames);
                    if (_playback.Playback.Status.State is VideoPlaybackState.Faulted or VideoPlaybackState.Unsupported or VideoPlaybackState.Offline)
                        throw new InvalidOperationException(_playback.Playback.Status.Detail);
                    SetStatus(_playback.Playback.Status.State == VideoPlaybackState.Live ? "Playing" : "Opening",
                        _playback.Playback.Status.Summary);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    if (stream is not null)
                    {
                        try { await _connections.CloseCameraStreamAsync(stream.ConnectionId, stream.StreamId, cancellationToken); }
                        catch (Exception) { }
                    }
                    await DisposePlaybackAsync(cancellationToken);
                    _stream = null;
                    _frameSource.SetSource(null);
                }
            }
            throw lastError ?? new InvalidOperationException("Unable to open camera stream.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetStatus("Faulted", GStreamerPipelineArguments.RedactText(ex.Message));
        }
        finally
        {
            _gate.Release();
            RaiseCommands();
        }
    }

    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        await CloseAsync(cancellationToken);
        await OpenAsync(cancellationToken);
    }

    internal void RefreshStreamStatus(IEnumerable<CameraStreamRecord> streams)
    {
        if (_stream is null) return;
        var latest = streams.FirstOrDefault(item => item.Id == _stream.Id);
        if (latest is null) return;
        _stream = latest;
        if (_ghostSession is not null) SetStatus(latest.State, latest.Message);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stream = _stream;
            _stream = null;
            _frameSource.SetSource(null);
            Exception? closeError = null;
            if (_ghostSession is not null && stream is not null && _ghostStreams is not null)
            {
                try { await _ghostStreams.CloseAsync(stream, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { closeError = ex; }
            }
            _ghostSession = null;
            await DisposePlaybackAsync(cancellationToken);
            if (stream is not null && !stream.Protocol.Equals("Ghost3D", StringComparison.OrdinalIgnoreCase))
            {
                try { await _connections.CloseCameraStreamAsync(stream.ConnectionId, stream.StreamId, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { closeError = ex; }
            }
            SetStatus(closeError is not null ? "Faulted" : Camera is null ? "No source" : "Closed",
                closeError is not null ? GStreamerPipelineArguments.RedactText(closeError.Message) : "");
        }
        finally
        {
            _gate.Release();
            RaiseCommands();
        }
    }

    private async Task DisposePlaybackAsync(CancellationToken cancellationToken = default)
    {
        if (_playback is null) return;
        _playback.Playback.Changed -= OnPlaybackChanged;
        await _playback.Playback.DetachAsync(cancellationToken);
        if (_playback.Playback is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_playback.Playback is IDisposable disposable)
            disposable.Dispose();
        _playback = null;
    }

    private void OnPlaybackChanged(object? sender, EventArgs e)
    {
        void Update()
        {
            if (_playback is null) return;
            var status = _playback.Playback.Status;
            SetStatus(status.State switch
            {
                VideoPlaybackState.Live => "Playing",
                VideoPlaybackState.Faulted or VideoPlaybackState.Unsupported or VideoPlaybackState.Offline => "Faulted",
                VideoPlaybackState.Detached => "Closed",
                _ => "Opening"
            }, status.Summary);
        }

        if (_dispatcher is null || _dispatcher.CheckAccess()) Update();
        else _ = _dispatcher.InvokeAsync(Update);
    }

    private void SetStatus(string status, string detail)
    {
        Status = status;
        Detail = detail;
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ShowPlaybackPlaceholder));
        OnPropertyChanged(nameof(IsGhost));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        (OpenCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CloseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasCamera));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = CloseAsync(CancellationToken.None);
    }
}
