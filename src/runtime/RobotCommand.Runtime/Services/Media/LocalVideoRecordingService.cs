using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Media;

public sealed class LocalVideoRecordingService : ILocalVideoRecordingService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly IGStreamerRuntime _runtime;
    private readonly IGStreamerVideoPipeline _livePipeline;
    private readonly IGStreamerVideoPipeline _playbackPipeline;
    private readonly ILocalVideoRecordingCatalog _catalog;
    private readonly AppConfiguration _configuration;
    private readonly ILogger<LocalVideoRecordingService> _logger;
    private readonly SwitchableVideoFrameSource _presentationFrames = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly VideoFrameBuffer _pausedFrame = new();
    private CancellationTokenSource? _recordingCancellation;
    private Task? _recordingTask;
    private LocalVideoRecorderStatus _status = LocalVideoRecorderStatus.Stopped;
    private LocalVideoTimelineSnapshot _timeline = LocalVideoTimelineSnapshot.Empty;
    private LocalVideoSessionManifest? _activeSession;
    private string? _activeSessionDirectory;
    private string? _pausedSegmentId;
    private string? _playbackPath;
    private GStreamerRawVideoOptions? _playbackOutput;
    private string? _playbackItemId;
    private double _pausedPosition = 1;
    private int _disposed;

    public LocalVideoRecordingService(
        IGStreamerRuntime runtime,
        IGStreamerVideoPipeline livePipeline,
        IGStreamerVideoPipelineFactory pipelineFactory,
        ILocalVideoRecordingCatalog catalog,
        AppConfiguration configuration,
        ILogger<LocalVideoRecordingService> logger)
    {
        _runtime = runtime;
        _livePipeline = livePipeline;
        _playbackPipeline = pipelineFactory.Create();
        _catalog = catalog;
        _configuration = configuration;
        _logger = logger;
        _presentationFrames.SetSource(_livePipeline.Frames);
        _livePipeline.Frames.FrameAvailable += OnLiveFrameAvailable;
        _playbackPipeline.Changed += OnPlaybackPipelineChanged;
    }

    public event EventHandler? Changed;

    public LocalVideoRecorderStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    public LocalVideoTimelineSnapshot Timeline
    {
        get
        {
            lock (_stateGate)
            {
                return _timeline;
            }
        }
    }

    public IVideoFrameSource PresentationFrames => _presentationFrames;

    public async Task BeginSessionAsync(
        CameraStreamRecord stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopRecordingCoreAsync(cancellationToken);
            await StopPlaybackCoreAsync(cancellationToken);
            _presentationFrames.SetSource(_livePipeline.Frames);

            if (!_configuration.LocalVideoRecordingEnabled)
            {
                SetStatus(new LocalVideoRecorderStatus(
                    LocalVideoRecorderState.Disabled,
                    "Local recording disabled",
                    "Enable video.localRecordingEnabled to retain a console-side rolling buffer.",
                    DateTimeOffset.UtcNow));
                await RefreshCoreAsync(cancellationToken);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var sessionId = CreateSessionId(now, stream.CameraSourceId, stream.StreamId);
            var sessionDirectory = Path.Combine(_catalog.RootPath, sessionId);
            Directory.CreateDirectory(sessionDirectory);
            var output = ResolveOutput(stream);
            var manifest = new LocalVideoSessionManifest(
                LocalVideoRecordingCatalog.SessionSchema,
                sessionId,
                stream.CameraSourceId,
                stream.StreamId,
                stream.ConnectionId,
                string.IsNullOrWhiteSpace(stream.Protocol) ? "Unknown" : stream.Protocol,
                now,
                output.Width,
                output.Height,
                output.FrameRate,
                _configuration.LocalVideoSegmentSeconds);
            await _catalog.WriteSessionAsync(sessionDirectory, manifest, cancellationToken);

            _activeSession = manifest;
            _activeSessionDirectory = sessionDirectory;
            _recordingCancellation = new CancellationTokenSource();
            var token = _recordingCancellation.Token;
            SetStatus(new LocalVideoRecorderStatus(
                LocalVideoRecorderState.WaitingForFrame,
                "Waiting to record",
                "The rolling recorder will start when the native live pipeline publishes its first decoded frame.",
                DateTimeOffset.UtcNow,
                sessionId,
                sessionDirectory));
            _recordingTask = Task.Run(() => RunRecordingSessionAsync(manifest, sessionDirectory, token), CancellationToken.None);
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopRecordingCoreAsync(cancellationToken);
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PlaySelectedAsync(double position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
            var segment = Timeline.SegmentAt(position);
            if (segment is null)
            {
                throw new InvalidOperationException("The selected rolling-buffer position does not contain a local segment.");
            }
            await PlaySegmentCoreAsync(segment, Math.Clamp(position, 0, 1), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PlaySegmentAsync(string segmentId, double position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
            var segment = Timeline.Segments.FirstOrDefault(item => item.Id == segmentId)
                ?? throw new InvalidOperationException("The selected local video segment is no longer present in the catalogue.");
            await PlaySegmentCoreAsync(segment, Math.Clamp(position, 0, 1), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PlayExternalAsync(
        string path,
        string itemId,
        int width,
        int height,
        int frameRate,
        double position,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new ArgumentException("A timeline item ID is required.", nameof(itemId));
        }
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected cached vehicle recording is not available.", fullPath);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var output = new GStreamerRawVideoOptions(
                Math.Max(1, width),
                Math.Max(1, height),
                Math.Max(1, frameRate));
            await StartPlaybackCoreAsync(fullPath, itemId, output, Math.Clamp(position, 0, 1), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Timeline.Mode != LocalVideoTimelineMode.Playback)
            {
                return;
            }

            _pausedSegmentId = _playbackItemId ?? Timeline.ActiveSegmentId;
            _pausedPosition = Timeline.Position;
            var latest = _playbackPipeline.Frames.LatestInfo;
            if (latest is not null)
            {
                var pixels = new byte[latest.RequiredBytes];
                if (_playbackPipeline.Frames.TryCopyLatest(pixels, out var copied) && copied is not null)
                {
                    _pausedFrame.Publish(pixels, copied.Width, copied.Height, copied.Stride, copied.Timestamp);
                    _presentationFrames.SetSource(_pausedFrame);
                }
            }
            await StopPlaybackCoreAsync(cancellationToken, clearSelection: false);
            SetTimeline(Timeline with
            {
                Mode = LocalVideoTimelineMode.Paused,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Timeline.Mode != LocalVideoTimelineMode.Paused || string.IsNullOrWhiteSpace(_pausedSegmentId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_playbackPath) || _playbackOutput is null || !File.Exists(_playbackPath))
            {
                throw new FileNotFoundException("The paused video item is no longer available.");
            }

            await _playbackPipeline.StartFileAsync(_playbackPath, _playbackOutput, cancellationToken);
            _presentationFrames.SetSource(_playbackPipeline.Frames);
            SetTimeline(Timeline with
            {
                Mode = LocalVideoTimelineMode.Playback,
                ActiveSegmentId = _pausedSegmentId,
                Position = _pausedPosition,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GoLiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopPlaybackCoreAsync(cancellationToken);
            _presentationFrames.SetSource(_livePipeline.Frames);
            ClearPlaybackSelection();
            SetTimeline(Timeline with
            {
                Mode = LocalVideoTimelineMode.Live,
                ActiveSegmentId = null,
                Position = 1,
                LiveEdge = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetainSelectedAsync(double position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
            var segment = Timeline.SegmentAt(position);
            if (segment is null)
            {
                throw new InvalidOperationException("No local video segment exists at the selected timeline position.");
            }
            await RetainSegmentCoreAsync(segment, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetainSegmentAsync(string segmentId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
            var segment = Timeline.Segments.FirstOrDefault(item => item.Id == segmentId)
                ?? throw new InvalidOperationException("The selected local video segment is no longer present in the catalogue.");
            await RetainSegmentCoreAsync(segment, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _livePipeline.Frames.FrameAvailable -= OnLiveFrameAvailable;
        _playbackPipeline.Changed -= OnPlaybackPipelineChanged;
        await _gate.WaitAsync();
        try
        {
            await StopRecordingCoreAsync(CancellationToken.None);
            await StopPlaybackCoreAsync(CancellationToken.None);
            await _playbackPipeline.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _frameSignal.Dispose();
        }
    }

    private async Task RunRecordingSessionAsync(
        LocalVideoSessionManifest manifest,
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        TcpListener? listener = null;
        Task? stderrTask = null;
        try
        {
            var firstFrame = await WaitForFirstFrameAsync(cancellationToken);
            var diagnostics = await _runtime.InspectAsync(cancellationToken: cancellationToken);
            if (!diagnostics.Available || string.IsNullOrWhiteSpace(diagnostics.LaunchExecutable))
            {
                throw new InvalidOperationException(diagnostics.Detail);
            }

            var missing = GStreamerRecordingArguments.RequiredPlugins
                .Where(plugin => !diagnostics.HasPlugin(plugin))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The installed GStreamer runtime cannot record the rolling buffer because these plugins are missing: {string.Join(", ", missing)}.");
            }

            var actualManifest = manifest with
            {
                Width = firstFrame.Width,
                Height = firstFrame.Height
            };
            await _catalog.WriteSessionAsync(sessionDirectory, actualManifest, cancellationToken);
            _activeSession = actualManifest;
            manifest = actualManifest;
            var output = new GStreamerRawVideoOptions(firstFrame.Width, firstFrame.Height, manifest.FrameRate);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var locationPattern = Path.Combine(sessionDirectory, "segment-%05d.mkv");
            var arguments = GStreamerRecordingArguments.BuildConsoleRecorder(
                output,
                port,
                locationPattern,
                manifest.SegmentSeconds,
                _configuration.LocalVideoEncodingBitrateKbps);
            process = CreateProcess(diagnostics.LaunchExecutable, arguments);
            SetStatus(new LocalVideoRecorderStatus(
                LocalVideoRecorderState.Starting,
                "Starting rolling buffer",
                "Launching the local GStreamer segment recorder.",
                DateTimeOffset.UtcNow,
                manifest.SessionId,
                sessionDirectory));
            if (!process.Start())
            {
                throw new InvalidOperationException("gst-launch-1.0 did not start the local recorder.");
            }

            stderrTask = ReadStandardErrorAsync(process, cancellationToken);
            using var client = await AcceptClientAsync(process, listener, cancellationToken);
            listener = null;
            await using var network = client.GetStream();
            SetStatus(new LocalVideoRecorderStatus(
                LocalVideoRecorderState.Recording,
                "Recording received video",
                $"Console-side rolling buffer active · {output.Width}×{output.Height} · {output.FrameRate} fps · {manifest.SegmentSeconds}s Matroska segments.",
                DateTimeOffset.UtcNow,
                manifest.SessionId,
                sessionDirectory));

            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sessionToken = sessionCancellation.Token;
            var pumpTask = PumpFramesAsync(network, output, sessionToken);
            var monitorTask = MonitorSegmentsAsync(sessionToken);
            var exitTask = process.WaitForExitAsync(sessionToken);
            var completed = await Task.WhenAny(pumpTask, exitTask);
            if (completed == exitTask && !cancellationToken.IsCancellationRequested)
            {
                sessionCancellation.Cancel();
                try
                {
                    await monitorTask;
                }
                catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
                {
                }
                await exitTask;
                throw new InvalidOperationException($"The local GStreamer recorder exited with code {process.ExitCode}.");
            }

            await pumpTask;
            sessionCancellation.Cancel();
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var detail = GStreamerPipelineArguments.RedactText(ex.Message);
            _logger.LogWarning(ex, "The local rolling video recorder failed");
            SetStatus(new LocalVideoRecorderStatus(
                LocalVideoRecorderState.Faulted,
                "Local recording failed",
                detail,
                DateTimeOffset.UtcNow,
                manifest.SessionId,
                sessionDirectory,
                detail));
        }
        finally
        {
            listener?.Stop();
            if (process is not null)
            {
                TryStopProcess(process);
                process.Dispose();
            }
            if (stderrTask is not null)
            {
                try
                {
                    await stderrTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                }
            }

            try
            {
                await RefreshCatalogueAsync(cleanup: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not refresh the local video catalogue after recorder shutdown");
            }
        }
    }

    private async Task PumpFramesAsync(
        NetworkStream network,
        GStreamerRawVideoOptions output,
        CancellationToken cancellationToken)
    {
        var frameSize = checked(output.Width * output.Height * 4);
        var buffer = new byte[frameSize];
        long lastSequence = -1;
        while (!cancellationToken.IsCancellationRequested)
        {
            await _frameSignal.WaitAsync(cancellationToken);
            var info = _livePipeline.Frames.LatestInfo;
            if (info is null || info.Sequence == lastSequence ||
                info.Width != output.Width || info.Height != output.Height || info.RequiredBytes != frameSize)
            {
                continue;
            }

            if (!_livePipeline.Frames.TryCopyLatest(buffer, out var copied) || copied is null || copied.Sequence == lastSequence)
            {
                continue;
            }

            lastSequence = copied.Sequence;
            await network.WriteAsync(buffer, cancellationToken);
        }
    }

    private async Task MonitorSegmentsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RefreshCatalogueAsync(cleanup: true, cancellationToken);
        }
    }

    private async Task<VideoFrameInfo> WaitForFirstFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var info = _livePipeline.Frames.LatestInfo;
            if (info is not null)
            {
                return info;
            }

            await _frameSignal.WaitAsync(cancellationToken);
        }
    }

    private async Task StopRecordingCoreAsync(CancellationToken cancellationToken)
    {
        var cancellation = _recordingCancellation;
        var task = _recordingTask;
        _recordingCancellation = null;
        _recordingTask = null;
        _activeSession = null;
        _activeSessionDirectory = null;
        if (cancellation is null && task is null)
        {
            if (Status.State != LocalVideoRecorderState.Disabled)
            {
                SetStatus(LocalVideoRecorderStatus.Stopped with { UpdatedAt = DateTimeOffset.UtcNow });
            }
            return;
        }

        SetStatus(Status with
        {
            State = LocalVideoRecorderState.Stopping,
            Summary = "Stopping local recording",
            Detail = "Finalizing the active rolling-buffer segment.",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        cancellation?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for the local video recorder to stop");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation?.Dispose();
        if (Status.State != LocalVideoRecorderState.Faulted)
        {
            SetStatus(LocalVideoRecorderStatus.Stopped with { UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    private async Task StopPlaybackCoreAsync(CancellationToken cancellationToken, bool clearSelection = true)
    {
        await _playbackPipeline.StopAsync(cancellationToken);
        if (clearSelection)
        {
            ClearPlaybackSelection();
        }
    }

    private async Task PlaySegmentCoreAsync(
        LocalVideoSegment segment,
        double position,
        CancellationToken cancellationToken)
    {
        if (!segment.Finalized || !File.Exists(segment.Path))
        {
            throw new InvalidOperationException("The selected rolling-buffer segment is not available for playback yet.");
        }
        var output = new GStreamerRawVideoOptions(segment.Width, segment.Height, segment.FrameRate);
        await StartPlaybackCoreAsync(segment.Path, segment.Id, output, position, cancellationToken);
    }

    private async Task StartPlaybackCoreAsync(
        string path,
        string itemId,
        GStreamerRawVideoOptions output,
        double position,
        CancellationToken cancellationToken)
    {
        await StopPlaybackCoreAsync(cancellationToken);
        await _playbackPipeline.StartFileAsync(path, output, cancellationToken);
        _playbackPath = path;
        _playbackOutput = output;
        _playbackItemId = itemId;
        _pausedSegmentId = null;
        _presentationFrames.SetSource(_playbackPipeline.Frames);
        SetTimeline(Timeline with
        {
            Mode = LocalVideoTimelineMode.Playback,
            ActiveSegmentId = itemId,
            Position = Math.Clamp(position, 0, 1),
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task RetainSegmentCoreAsync(
        LocalVideoSegment segment,
        CancellationToken cancellationToken)
    {
        if (!segment.Finalized || !File.Exists(segment.Path))
        {
            throw new InvalidOperationException("Only finalized local video segments can be retained.");
        }
        await _catalog.RetainAsync(segment, cancellationToken);
        await RefreshCoreAsync(cancellationToken);
    }

    private void ClearPlaybackSelection()
    {
        _pausedSegmentId = null;
        _playbackPath = null;
        _playbackOutput = null;
        _playbackItemId = null;
    }

    private Task RefreshCoreAsync(CancellationToken cancellationToken)
        => RefreshCatalogueAsync(cleanup: true, cancellationToken);

    private async Task RefreshCatalogueAsync(bool cleanup, CancellationToken cancellationToken)
    {
        var context = new LocalVideoCatalogContext(
            _activeSession?.SessionId,
            Status.State,
            Timeline.ActiveSegmentId,
            _activeSessionDirectory);
        var segments = cleanup
            ? await _catalog.CleanupAndLoadAsync(context, cancellationToken)
            : await _catalog.LoadAsync(context, cancellationToken);
        ApplySegments(segments);
    }

    private void ApplySegments(IReadOnlyList<LocalVideoSegment> segments)
    {
        var totalBytes = segments.Sum(item => item.SizeBytes);
        DateTimeOffset? rangeStart = segments.Count == 0 ? null : segments[0].StartedAt;
        DateTimeOffset? rangeEnd = segments.Count == 0 ? null : segments[^1].EndedAt;
        var switchToLive = false;
        lock (_stateGate)
        {
            var existing = _timeline;
            var mode = existing.Mode;
            var activeSegmentId = existing.ActiveSegmentId;
            if (activeSegmentId is not null &&
                segments.All(item => item.Id != activeSegmentId) &&
                !string.Equals(activeSegmentId, _playbackItemId, StringComparison.Ordinal))
            {
                mode = LocalVideoTimelineMode.Live;
                activeSegmentId = null;
                switchToLive = true;
                ClearPlaybackSelection();
            }

            _timeline = new LocalVideoTimelineSnapshot(
                segments,
                rangeStart,
                rangeEnd,
                DateTimeOffset.UtcNow,
                mode,
                activeSegmentId,
                mode == LocalVideoTimelineMode.Live ? 1 : existing.Position,
                totalBytes,
                DateTimeOffset.UtcNow);
        }

        if (switchToLive)
        {
            _presentationFrames.SetSource(_livePipeline.Frames);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Process CreateProcess(string executable, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        var pluginPath = GStreamerExecutableLocator.ResolvePluginPath(_configuration.GStreamerPluginPath);
        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            process.StartInfo.Environment["GST_PLUGIN_PATH"] = pluginPath;
        }
        return process;
    }

    private async Task ReadStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogDebug("Local recorder: {Line}", GStreamerPipelineArguments.RedactText(line));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<TcpClient> AcceptClientAsync(
        Process process,
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            var accept = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var exit = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(accept, exit);
            if (completed == exit)
            {
                await exit;
                throw new IOException($"The local GStreamer recorder exited with code {process.ExitCode} before connecting to the frame source.");
            }
            return await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private GStreamerRawVideoOptions ResolveOutput(CameraStreamRecord stream)
        => new(
            (int)(stream.Width > 0 ? stream.Width : (uint)_configuration.GStreamerTestWidth),
            (int)(stream.Height > 0 ? stream.Height : (uint)_configuration.GStreamerTestHeight),
            (int)Math.Clamp(stream.FrameRateHz > 0 ? Math.Round(stream.FrameRateHz) : _configuration.GStreamerTestFrameRate, 1, 60));

    private void OnLiveFrameAvailable(object? sender, EventArgs e)
    {
        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake-up already represents the newest available frame.
        }
    }

    private void OnPlaybackPipelineChanged(object? sender, EventArgs e)
    {
        if (_playbackPipeline.Status.State == NativeVideoPipelineState.Stopped && Timeline.Mode == LocalVideoTimelineMode.Playback)
        {
            SetTimeline(Timeline with
            {
                Mode = LocalVideoTimelineMode.Paused,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private void SetStatus(LocalVideoRecorderStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetTimeline(LocalVideoTimelineSnapshot timeline)
    {
        lock (_stateGate)
        {
            _timeline = timeline;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string CreateSessionId(DateTimeOffset startedAt, string cameraSourceId, string streamId)
    {
        var camera = Sanitize(cameraSourceId);
        var stream = Sanitize(streamId);
        camera = camera[..Math.Min(camera.Length, 32)];
        stream = stream[..Math.Min(stream.Length, 32)];
        var unique = Guid.NewGuid().ToString("N")[..8];
        return $"{startedAt:yyyyMMdd-HHmmss-fff}-{unique}-{camera}-{stream}";
    }

    private static string Sanitize(string value)
    {
        var chars = value
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .Take(48)
            .ToArray();
        var result = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }


    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}
