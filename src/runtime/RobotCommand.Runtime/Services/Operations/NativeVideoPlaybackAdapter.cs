using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Media;

namespace RobotCommand.Services.Operations;

public sealed class NativeVideoPlaybackAdapter : IVideoPlaybackAdapter, IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly IGStreamerVideoPipeline _pipeline;
    private readonly AppConfiguration _configuration;
    private readonly ILogger<NativeVideoPlaybackAdapter> _logger;
    private CameraStreamRecord? _activeStream;
    private NativeVideoProtocol _activeProtocol;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _reconnectTask;
    private VideoPlaybackStatus _status = VideoPlaybackStatus.Detached;
    private int _generation;
    private int _disposed;

    public NativeVideoPlaybackAdapter(
        IGStreamerVideoPipeline pipeline,
        AppConfiguration configuration,
        ILogger<NativeVideoPlaybackAdapter> logger)
    {
        _pipeline = pipeline;
        _configuration = configuration;
        _logger = logger;
        _pipeline.Changed += OnPipelineChanged;
    }

    public event EventHandler? Changed;

    public VideoPlaybackStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    public async Task AttachAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken);
        Task? previousReconnect = null;
        try
        {
            previousReconnect = await ResetSessionCoreAsync(cancellationToken);
            var protocol = NativeVideoProtocolResolver.Resolve(stream);
            var safeEndpoint = GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl);
            if (protocol == NativeVideoProtocol.Unknown)
            {
                SetStatus(new VideoPlaybackStatus(
                    VideoPlaybackState.Unsupported,
                    "Unsupported stream protocol",
                    $"The Logos stream descriptor could not be mapped to RTSP, SRT, HLS, or WebRTC/WHEP. Returned protocol: '{stream.Protocol}'.",
                    stream.StreamId,
                    stream.Protocol,
                    safeEndpoint,
                    UpdatedAt: DateTimeOffset.UtcNow));
                return;
            }

            if (!TryValidateEndpoint(stream.StreamUrl, protocol, out var endpointError))
            {
                SetStatus(new VideoPlaybackStatus(
                    VideoPlaybackState.Faulted,
                    $"Invalid {NativeVideoProtocolResolver.DisplayName(protocol)} descriptor",
                    endpointError,
                    stream.StreamId,
                    stream.Protocol,
                    safeEndpoint,
                    UpdatedAt: DateTimeOffset.UtcNow));
                return;
            }

            if (protocol == NativeVideoProtocol.Whep && !TryValidateWhepCodec(stream.Codec, out var codecError))
            {
                SetStatus(new VideoPlaybackStatus(
                    VideoPlaybackState.Unsupported,
                    "Unsupported WebRTC/WHEP codec",
                    codecError,
                    stream.StreamId,
                    NativeVideoProtocolResolver.DisplayName(protocol),
                    safeEndpoint,
                    UpdatedAt: DateTimeOffset.UtcNow));
                return;
            }

            if (IsTerminal(stream.State))
            {
                SetStatus(new VideoPlaybackStatus(
                    VideoPlaybackState.Offline,
                    "Logos stream is not active",
                    $"Logos returned the stream in terminal state '{stream.State}'.",
                    stream.StreamId,
                    NativeVideoProtocolResolver.DisplayName(protocol),
                    safeEndpoint,
                    UpdatedAt: DateTimeOffset.UtcNow));
                return;
            }

            _activeStream = stream;
            _activeProtocol = protocol;
            _sessionCancellation = new CancellationTokenSource();
            var generation = ++_generation;
            SetStatus(new VideoPlaybackStatus(
                VideoPlaybackState.SessionReady,
                $"{NativeVideoProtocolResolver.DisplayName(protocol)} session negotiated",
                $"Logos returned a native {NativeVideoProtocolResolver.DisplayName(protocol)} endpoint for camera '{stream.CameraSourceId}'.",
                stream.StreamId,
                NativeVideoProtocolResolver.DisplayName(protocol),
                safeEndpoint,
                UpdatedAt: DateTimeOffset.UtcNow));

            try
            {
                await StartPipelineCoreAsync(stream, protocol, generation, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = GStreamerPipelineArguments.RedactText(ex.Message);
                _logger.LogWarning(
                    "Initial {Protocol} playback start failed for stream {StreamId}: {Error}",
                    NativeVideoProtocolResolver.DisplayName(protocol),
                    stream.StreamId,
                    error);
                ScheduleReconnectCore(error);
            }
        }
        finally
        {
            _gate.Release();
        }

        await IgnoreCancellationAsync(previousReconnect);
    }

    public async Task DetachAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Task? reconnectTask;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            reconnectTask = await ResetSessionCoreAsync(cancellationToken);
            SetStatus(VideoPlaybackStatus.Detached with { UpdatedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            _gate.Release();
        }
        await IgnoreCancellationAsync(reconnectTask);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _pipeline.Changed -= OnPipelineChanged;
        Task? reconnectTask;
        await _gate.WaitAsync();
        try
        {
            reconnectTask = await ResetSessionCoreAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
        await IgnoreCancellationAsync(reconnectTask);
    }

    private async Task StartPipelineCoreAsync(
        CameraStreamRecord stream,
        NativeVideoProtocol protocol,
        int generation,
        CancellationToken cancellationToken)
    {
        if (generation != _generation || !ReferenceEquals(stream, _activeStream)) return;

        var safeEndpoint = GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl);
        SetStatus(new VideoPlaybackStatus(
            Status.State == VideoPlaybackState.Reconnecting ? VideoPlaybackState.Reconnecting : VideoPlaybackState.Connecting,
            $"Connecting to {NativeVideoProtocolResolver.DisplayName(protocol)} stream",
            $"Opening {safeEndpoint} through GStreamer.",
            stream.StreamId,
            NativeVideoProtocolResolver.DisplayName(protocol),
            safeEndpoint,
            Status.ReconnectAttempt,
            DateTimeOffset.UtcNow));

        switch (protocol)
        {
            case NativeVideoProtocol.Rtsp:
                await _pipeline.StartRtspAsync(CreateRtspOptions(stream), cancellationToken);
                break;
            case NativeVideoProtocol.Srt:
                await _pipeline.StartSrtAsync(CreateSrtOptions(stream), cancellationToken);
                break;
            case NativeVideoProtocol.Hls:
                await _pipeline.StartHlsAsync(CreateHlsOptions(stream), cancellationToken);
                break;
            case NativeVideoProtocol.Whep:
                await _pipeline.StartWhepAsync(CreateWhepOptions(stream), cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Native protocol '{protocol}' is not supported.");
        }
    }

    private async Task<Task?> ResetSessionCoreAsync(CancellationToken cancellationToken)
    {
        _generation++;
        var sessionCancellation = _sessionCancellation;
        var reconnectTask = _reconnectTask;
        _sessionCancellation = null;
        _reconnectTask = null;
        _activeStream = null;
        _activeProtocol = NativeVideoProtocol.Unknown;
        sessionCancellation?.Cancel();
        try
        {
            await _pipeline.StopAsync(cancellationToken);
        }
        finally
        {
            sessionCancellation?.Dispose();
        }
        return reconnectTask;
    }

    private void OnPipelineChanged(object? sender, EventArgs e) => _ = HandlePipelineChangedAsync();

    private async Task HandlePipelineChangedAsync()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var stream = _activeStream;
            if (stream is null) return;
            if (Status.State == VideoPlaybackState.Unsupported) return;
            var name = NativeVideoProtocolResolver.DisplayName(_activeProtocol);
            var safeEndpoint = GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl);
            var pipelineStatus = _pipeline.Status;
            switch (pipelineStatus.State)
            {
                case NativeVideoPipelineState.InspectingRuntime:
                case NativeVideoPipelineState.Starting:
                    if (Status.State != VideoPlaybackState.Reconnecting)
                    {
                        SetStatus(new VideoPlaybackStatus(
                            VideoPlaybackState.Connecting,
                            $"Connecting to {name} stream",
                            GStreamerPipelineArguments.RedactText(pipelineStatus.Detail),
                            stream.StreamId,
                            name,
                            safeEndpoint,
                            UpdatedAt: DateTimeOffset.UtcNow));
                    }
                    break;
                case NativeVideoPipelineState.Playing:
                    SetStatus(new VideoPlaybackStatus(
                        VideoPlaybackState.Live,
                        $"{name} video live",
                        BuildLiveDetail(stream, pipelineStatus),
                        stream.StreamId,
                        name,
                        safeEndpoint,
                        UpdatedAt: DateTimeOffset.UtcNow));
                    break;
                case NativeVideoPipelineState.Faulted:
                    ScheduleReconnectCore(pipelineStatus.LastError ?? pipelineStatus.Detail);
                    break;
                case NativeVideoPipelineState.Stopped:
                    ScheduleReconnectCore($"The native {name} pipeline stopped before the stream was detached.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to project the native pipeline state into playback state");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ScheduleReconnectCore(string reason)
    {
        reason = GStreamerPipelineArguments.RedactText(reason);
        var stream = _activeStream;
        var sessionCancellation = _sessionCancellation;
        if (stream is null || sessionCancellation is null || sessionCancellation.IsCancellationRequested) return;

        if (IsPermanentCapabilityFailure(reason))
        {
            SetStatus(new VideoPlaybackStatus(
                VideoPlaybackState.Unsupported,
                $"{NativeVideoProtocolResolver.DisplayName(_activeProtocol)} playback unavailable",
                reason,
                stream.StreamId,
                NativeVideoProtocolResolver.DisplayName(_activeProtocol),
                GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl),
                UpdatedAt: DateTimeOffset.UtcNow));
            return;
        }

        if (_configuration.GStreamerRtspReconnectAttempts <= 0)
        {
            SetTerminalFailure(stream, $"{NativeVideoProtocolResolver.DisplayName(_activeProtocol)} playback failed", reason, 0);
            return;
        }
        if (_reconnectTask is { IsCompleted: false }) return;
        _reconnectTask = ReconnectLoopAsync(stream, _activeProtocol, _generation, reason, sessionCancellation.Token);
    }

    private async Task ReconnectLoopAsync(
        CameraStreamRecord stream,
        NativeVideoProtocol protocol,
        int generation,
        string initialReason,
        CancellationToken cancellationToken)
    {
        var lastError = initialReason;
        for (var attempt = 1; attempt <= _configuration.GStreamerRtspReconnectAttempts; attempt++)
        {
            var delay = CalculateReconnectDelay(attempt);
            var name = NativeVideoProtocolResolver.DisplayName(protocol);
            SetStatus(new VideoPlaybackStatus(
                VideoPlaybackState.Reconnecting,
                $"Reconnecting {name} video",
                $"Attempt {attempt}/{_configuration.GStreamerRtspReconnectAttempts} in {delay.TotalSeconds:0.0}s. {lastError}",
                stream.StreamId,
                name,
                GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl),
                attempt,
                DateTimeOffset.UtcNow));
            try
            {
                await Task.Delay(delay, cancellationToken);
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != _generation || !ReferenceEquals(stream, _activeStream)) return;
                    await StartPipelineCoreAsync(stream, protocol, generation, cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }

                var outcome = await WaitForReconnectOutcomeAsync(generation, cancellationToken);
                if (outcome == ReconnectOutcome.Live) return;
                lastError = GStreamerPipelineArguments.RedactText(_pipeline.Status.LastError ?? _pipeline.Status.Detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lastError = GStreamerPipelineArguments.RedactText(ex.Message);
                _logger.LogDebug("{Protocol} reconnect attempt {Attempt} failed: {Error}", name, attempt, lastError);
            }
        }

        if (generation == _generation && ReferenceEquals(stream, _activeStream))
        {
            SetTerminalFailure(
                stream,
                $"{NativeVideoProtocolResolver.DisplayName(protocol)} reconnect exhausted",
                lastError,
                _configuration.GStreamerRtspReconnectAttempts);
        }
    }

    private void SetTerminalFailure(CameraStreamRecord stream, string summary, string detail, int attempts)
        => SetStatus(new VideoPlaybackStatus(
            VideoPlaybackState.Faulted,
            summary,
            detail,
            stream.StreamId,
            NativeVideoProtocolResolver.DisplayName(_activeProtocol),
            GStreamerPipelineArguments.RedactEndpoint(stream.StreamUrl),
            attempts,
            DateTimeOffset.UtcNow));

    private async Task<ReconnectOutcome> WaitForReconnectOutcomeAsync(int generation, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation) return ReconnectOutcome.Cancelled;
            var state = _pipeline.Status.State;
            if (state == NativeVideoPipelineState.Playing) return ReconnectOutcome.Live;
            if (state is NativeVideoPipelineState.Faulted or NativeVideoPipelineState.Stopped) return ReconnectOutcome.Failed;
            await Task.Delay(100, cancellationToken);
        }
        return ReconnectOutcome.Failed;
    }

    private RtspPlaybackOptions CreateRtspOptions(CameraStreamRecord stream)
        => new(stream.StreamUrl, CreateOutput(stream), _configuration.GStreamerRtspLatencyMilliseconds,
            _configuration.GStreamerRtspTransport, stream.StreamId, stream.CameraSourceId, stream.Codec);

    private SrtPlaybackOptions CreateSrtOptions(CameraStreamRecord stream)
        => new(stream.StreamUrl, CreateOutput(stream), _configuration.GStreamerSrtLatencyMilliseconds,
            stream.StreamId, stream.CameraSourceId, stream.Codec);

    private HlsPlaybackOptions CreateHlsOptions(CameraStreamRecord stream)
        => new(stream.StreamUrl, CreateOutput(stream), _configuration.GStreamerHlsTimeoutSeconds,
            stream.StreamId, stream.CameraSourceId, stream.Codec);

    private WhepPlaybackOptions CreateWhepOptions(CameraStreamRecord stream)
    {
        var payload = WhepNegotiationPayload.Parse(stream.NegotiationPayload);
        return new WhepPlaybackOptions(
            stream.StreamUrl,
            CreateOutput(stream),
            stream.Codec,
            payload.PayloadType,
            payload.AuthToken,
            _configuration.GStreamerWhepTimeoutSeconds,
            payload.UseLinkHeaders && _configuration.GStreamerWhepUseLinkHeaders,
            payload.StunServer,
            payload.TurnServer,
            stream.StreamId,
            stream.CameraSourceId);
    }

    private GStreamerRawVideoOptions CreateOutput(CameraStreamRecord stream)
    {
        var width = stream.Width is >= 16 and <= 7680 ? checked((int)stream.Width) : _configuration.GStreamerTestWidth;
        var height = stream.Height is >= 16 and <= 4320 ? checked((int)stream.Height) : _configuration.GStreamerTestHeight;
        var frameRate = stream.FrameRateHz is >= 1 and <= 120
            ? (int)Math.Round(stream.FrameRateHz)
            : _configuration.GStreamerTestFrameRate;
        return new GStreamerRawVideoOptions(width, height, Math.Clamp(frameRate, 1, 120));
    }

    private TimeSpan CalculateReconnectDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromMilliseconds(Math.Min(_configuration.GStreamerRtspReconnectDelayMilliseconds * multiplier, 30_000));
    }

    private static string BuildLiveDetail(CameraStreamRecord stream, NativeVideoPipelineStatus pipelineStatus)
    {
        var codec = string.IsNullOrWhiteSpace(stream.Codec) ? "server codec" : stream.Codec;
        return $"{stream.Width}×{stream.Height} · {stream.FrameRateHz:0.0} fps · {codec} · {pipelineStatus.Detail}";
    }

    private static bool IsTerminal(string state)
        => state.Contains("Closed", StringComparison.OrdinalIgnoreCase) ||
           state.Contains("Failed", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateWhepCodec(string codec, out string error)
    {
        try
        {
            _ = GStreamerPipelineArguments.WhepDepayloader(codec);
            error = string.Empty;
            return true;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsPermanentCapabilityFailure(string reason)
        => reason.Contains("missing required playback plugins", StringComparison.OrdinalIgnoreCase) ||
           reason.Contains("codec", StringComparison.OrdinalIgnoreCase) &&
           reason.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateEndpoint(string endpoint, NativeVideoProtocol protocol, out string error)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            error = "Logos returned an empty or invalid stream URL.";
            return false;
        }
        var valid = protocol switch
        {
            NativeVideoProtocol.Rtsp => uri.Scheme is "rtsp" or "rtsps",
            NativeVideoProtocol.Srt => uri.Scheme == "srt",
            NativeVideoProtocol.Hls or NativeVideoProtocol.Whep => uri.Scheme is "http" or "https",
            _ => false
        };
        error = valid
            ? string.Empty
            : $"The endpoint scheme '{uri.Scheme}' does not match {NativeVideoProtocolResolver.DisplayName(protocol)} playback.";
        return valid;
    }

    private void SetStatus(VideoPlaybackStatus status)
    {
        lock (_statusGate) _status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null) return;
        try { await task; } catch (OperationCanceledException) { }
    }

    private enum ReconnectOutcome { Live, Failed, Cancelled }
}
