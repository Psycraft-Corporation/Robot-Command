using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class UnifiedVideoTimelineService : IUnifiedVideoTimelineService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly ILocalVideoRecordingService _local;
    private readonly IRemoteVideoRecordingCatalog _remote;
    private readonly ILogger<UnifiedVideoTimelineService> _logger;
    private UnifiedVideoTimelineSnapshot _timeline = UnifiedVideoTimelineSnapshot.Empty;
    private int _disposed;

    public UnifiedVideoTimelineService(
        ILocalVideoRecordingService local,
        IRemoteVideoRecordingCatalog remote,
        ILogger<UnifiedVideoTimelineService> logger)
    {
        _local = local;
        _remote = remote;
        _logger = logger;
        _local.Changed += OnSourceChanged;
        _remote.Changed += OnSourceChanged;
        RebuildTimeline();
    }

    public event EventHandler? Changed;

    public LocalVideoRecorderStatus LocalStatus => _local.Status;

    public RemoteVideoRecordingStatus RemoteStatus => _remote.Status;

    public UnifiedVideoTimelineSnapshot Timeline
    {
        get
        {
            lock (_stateGate)
            {
                return _timeline;
            }
        }
    }

    public IVideoFrameSource PresentationFrames => _local.PresentationFrames;

    public async Task BeginSessionAsync(
        CameraStreamRecord stream,
        ConnectionDefinition? connection,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _local.BeginSessionAsync(stream, cancellationToken);
            await _remote.BeginSessionAsync(stream, connection, cancellationToken);
            RebuildTimeline();
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
            Exception? localError = null;
            try
            {
                await _local.EndSessionAsync(cancellationToken);
                await _local.GoLiveAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                localError = ex;
            }
            _remote.ProtectPlayback(null);
            await _remote.EndSessionAsync(cancellationToken);
            RebuildTimeline();
            if (localError is not null)
            {
                throw localError;
            }
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
            await _local.RefreshAsync(cancellationToken);
            await _remote.RefreshAsync(cancellationToken);
            RebuildTimeline();
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
            RebuildTimeline();
            var item = Timeline.ItemAt(position)
                ?? throw new InvalidOperationException("The selected timeline position does not contain playable video.");
            if (!item.CanPlay)
            {
                throw new InvalidOperationException("The selected timeline item is not available for playback.");
            }

            if (!item.IsVehicleSide)
            {
                _remote.ProtectPlayback(null);
                await _local.PlaySegmentAsync(item.SourceId ?? item.Id, position, cancellationToken);
            }
            else
            {
                var spanId = item.SourceId ?? item.Id;
                _remote.ProtectPlayback(spanId);
                try
                {
                    var cached = item.Availability == VideoTimelineItemAvailability.Local
                        ? _remote.Spans.FirstOrDefault(span => span.Id == spanId)
                        : await _remote.EnsureCachedAsync(spanId, cancellationToken);
                    if (cached is null || string.IsNullOrWhiteSpace(cached.CachedPath))
                    {
                        throw new InvalidOperationException("The selected vehicle recording could not be cached for playback.");
                    }
                    await _local.PlayExternalAsync(
                        cached.CachedPath,
                        item.Id,
                        cached.Width,
                        cached.Height,
                        cached.FrameRate,
                        position,
                        cancellationToken);
                }
                catch
                {
                    _remote.ProtectPlayback(null);
                    throw;
                }
            }
            RebuildTimeline();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await RunLocalAsync(_local.PauseAsync, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await RunLocalAsync(_local.ResumeAsync, cancellationToken);
    }

    public async Task GoLiveAsync(CancellationToken cancellationToken = default)
    {
        await RunLocalAsync(_local.GoLiveAsync, cancellationToken);
        _remote.ProtectPlayback(null);
    }

    public async Task RetainSelectedAsync(double position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RebuildTimeline();
            var item = Timeline.ItemAt(position)
                ?? throw new InvalidOperationException("The selected timeline position does not contain video.");
            if (item.IsVehicleSide)
            {
                await _remote.RetainAsync(item.SourceId ?? item.Id, cancellationToken);
            }
            else
            {
                await _local.RetainSegmentAsync(item.SourceId ?? item.Id, cancellationToken);
            }
            RebuildTimeline();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadSelectedAsync(double position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RebuildTimeline();
            var item = Timeline.ItemAt(position)
                ?? throw new InvalidOperationException("The selected timeline position does not contain video.");
            if (!item.IsVehicleSide)
            {
                throw new InvalidOperationException("Console-side video is already stored locally.");
            }
            await _remote.EnsureCachedAsync(item.SourceId ?? item.Id, cancellationToken);
            RebuildTimeline();
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
        _local.Changed -= OnSourceChanged;
        _remote.Changed -= OnSourceChanged;
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
    }

    private async Task RunLocalAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await action(cancellationToken);
            RebuildTimeline();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        try
        {
            RebuildTimeline();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not rebuild the unified video timeline");
        }
    }

    private void RebuildTimeline()
    {
        var localTimeline = _local.Timeline;
        var items = new List<VideoTimelineItem>();
        items.AddRange(localTimeline.Segments.Select(segment => new VideoTimelineItem(
            "console:" + segment.Id,
            segment.Retained ? VideoTimelineItemOrigin.ConsoleRetained : VideoTimelineItemOrigin.ConsoleRolling,
            segment.Finalized ? VideoTimelineItemAvailability.Local : VideoTimelineItemAvailability.Unavailable,
            segment.StartedAt,
            segment.EndedAt,
            segment.CameraSourceId,
            segment.StreamId,
            segment.Protocol,
            segment.Width,
            segment.Height,
            segment.FrameRate,
            segment.SizeBytes,
            segment.Retained,
            segment.GapBefore,
            segment.Path,
            segment.Id)));
        items.AddRange(_remote.Spans.Select(span => new VideoTimelineItem(
            "vehicle:" + span.Id,
            span.Cached ? VideoTimelineItemOrigin.VehicleCached : VideoTimelineItemOrigin.VehicleRecording,
            span.Cached ? VideoTimelineItemAvailability.Local :
                _remote.Status.State == RemoteVideoRecordingState.Offline
                    ? VideoTimelineItemAvailability.Unavailable
                    : VideoTimelineItemAvailability.Remote,
            span.StartedAt,
            span.EndedAt,
            span.CameraSourceId,
            span.StreamId,
            span.Protocol,
            span.Width,
            span.Height,
            span.FrameRate,
            span.CachedSizeBytes,
            span.Retained,
            false,
            span.CachedPath,
            span.Id)));

        var ordered = items.OrderBy(item => item.StartedAt).ThenBy(item => item.Origin).ToArray();
        DateTimeOffset? rangeStart = ordered.Length == 0 ? null : ordered.Min(item => item.StartedAt);
        DateTimeOffset? rangeEnd = ordered.Length == 0 ? null : ordered.Max(item => item.EndedAt);
        var activeItemId = localTimeline.ActiveSegmentId is null
            ? null
            : (localTimeline.ActiveSegmentId.StartsWith("vehicle:", StringComparison.Ordinal) ||
               localTimeline.ActiveSegmentId.StartsWith("console:", StringComparison.Ordinal))
                ? localTimeline.ActiveSegmentId
                : ordered.FirstOrDefault(item => item.SourceId == localTimeline.ActiveSegmentId)?.Id;
        var snapshot = new UnifiedVideoTimelineSnapshot(
            ordered,
            rangeStart,
            rangeEnd,
            DateTimeOffset.UtcNow,
            localTimeline.Mode,
            activeItemId,
            localTimeline.Mode == LocalVideoTimelineMode.Live ? 1 : localTimeline.Position,
            localTimeline.TotalBytes,
            _remote.Spans.Where(span => span.Cached).Sum(span => span.CachedSizeBytes),
            DateTimeOffset.UtcNow);
        lock (_stateGate)
        {
            _timeline = snapshot;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
