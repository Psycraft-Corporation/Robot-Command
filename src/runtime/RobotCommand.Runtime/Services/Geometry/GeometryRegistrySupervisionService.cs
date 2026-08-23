using System.Net.Http;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;

namespace RobotCommand.Services.Geometry;

/// <summary>
/// Maintains one live GeometryService registry watch for every active Logos
/// operational session. Remote snapshots and events are projected into the
/// application workspace without mutating local geometry documents.
/// </summary>
public sealed class GeometryRegistrySupervisionService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly IGeometryGateway _gateway;
    private readonly IGeometryRegistryStateSink _sink;
    private readonly ILogger<GeometryRegistrySupervisionService> _logger;
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private long _nextGeneration;

    public GeometryRegistrySupervisionService(
        ILogosOperationalSessionRegistry sessions,
        IGeometryGateway gateway,
        IGeometryRegistryStateSink sink,
        ILogger<GeometryRegistrySupervisionService> logger)
    {
        _sessions = sessions;
        _gateway = gateway;
        _sink = sink;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileAsync(stoppingToken);
            using var timer = new PeriodicTimer(ReconcileInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAllAsync();
        }
    }

    /// <summary>
    /// Exposed for deterministic service tests; production calls this from the
    /// hosted reconciliation loop.
    /// </summary>
    public Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
        => ReconcileAsync(cancellationToken);

    private Task ReconcileAsync(CancellationToken stoppingToken)
    {
        var active = _sessions.ConnectionIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Registration[] removed;
        lock (_gate)
        {
            removed = _registrations
                .Where(item => !active.Contains(item.Key))
                .Select(item => item.Value)
                .ToArray();
            foreach (var registration in removed)
            {
                _registrations.Remove(registration.ConnectionId);
            }
        }

        foreach (var registration in removed)
        {
            registration.Cancellation.Cancel();
            _sink.ClearRemoteConnection(
                registration.ConnectionId,
                GeometryRegistryWatchState.Stopped(
                    registration.ConnectionId,
                    "The Logos connection is no longer active; live registry state was cleared."));
        }

        foreach (var connectionId in active)
        {
            StartIfNeeded(connectionId, stoppingToken);
        }

        MarkStaleWatches();
        return Task.CompletedTask;
    }

    private void StartIfNeeded(string connectionId, CancellationToken stoppingToken)
    {
        Registration registration;
        lock (_gate)
        {
            if (_registrations.ContainsKey(connectionId))
            {
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            registration = new Registration(
                connectionId,
                Interlocked.Increment(ref _nextGeneration),
                cancellation);
            _registrations.Add(connectionId, registration);
        }

        _sink.SetRegistryWatchState(GeometryRegistryWatchState.Starting(connectionId));
        registration.Lifetime = RunWatchLoopAsync(registration);
        _ = ObserveLifetimeAsync(registration);
    }

    private async Task RunWatchLoopAsync(Registration registration)
    {
        var restarts = 0;
        var cancellationToken = registration.Cancellation.Token;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(registration))
        {
            try
            {
                Interlocked.Exchange(ref registration.Live, 0);
                SetStatus(
                    registration,
                    GeometryRegistryWatchStatusKind.Connecting,
                    restarts == 0
                        ? "Connecting to the Logos geometry registry stream."
                        : "Reconnecting to the Logos geometry registry stream.",
                    restarts,
                    null);

                await LoadSnapshotAsync(registration, cancellationToken);
                AnnounceLive(registration, restarts, "Geometry registry snapshot loaded; live watch is active.");

                await foreach (var registryEvent in _gateway.WatchAsync(
                                   registration.ConnectionId,
                                   new GeometryQuery(IncludeObjects: true),
                                   cancellationToken))
                {
                    if (!IsCurrent(registration))
                    {
                        return;
                    }

                    var recovered = Touch(registration, registryEvent.ObservedAt);
                    _sink.ApplyRegistryEvent(registryEvent);
                    if (recovered)
                    {
                        AnnounceLive(
                            registration,
                            restarts,
                            "Geometry registry messages resumed after the watch became stale.");
                    }

                    if (registryEvent.Kind == GeometryRegistryEventKind.Reloaded)
                    {
                        await LoadSnapshotAsync(registration, cancellationToken);
                        AnnounceLive(
                            registration,
                            restarts,
                            "Geometry registry reload reconciled from a fresh snapshot.");
                    }
                }

                throw new EndOfStreamException("The geometry registry stream completed without cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                SetStatus(
                    registration,
                    GeometryRegistryWatchStatusKind.Unsupported,
                    "This Logos runtime does not implement live geometry registry watching.",
                    restarts,
                    ErrorMessage(ex));
                await WaitUntilCancelledAsync(cancellationToken);
                return;
            }
            catch (InvalidOperationException ex) when (IsUnsupported(ex))
            {
                SetStatus(
                    registration,
                    GeometryRegistryWatchStatusKind.Unsupported,
                    "This Logos runtime does not expose a usable geometry registry watch.",
                    restarts,
                    ErrorMessage(ex));
                await WaitUntilCancelledAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                restarts++;
                Interlocked.Exchange(ref registration.Live, 0);
                var delay = Backoff(restarts);
                SetStatus(
                    registration,
                    GeometryRegistryWatchStatusKind.BackingOff,
                    $"Geometry registry watch interrupted; retrying in {delay.TotalSeconds:0} second(s).",
                    restarts,
                    ErrorMessage(ex));
                _logger.LogWarning(
                    ex,
                    "Geometry registry watch for {ConnectionId} will restart after {Delay}",
                    registration.ConnectionId,
                    delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                SetStatus(
                    registration,
                    GeometryRegistryWatchStatusKind.Faulted,
                    "Geometry registry supervision stopped after a non-transient error.",
                    restarts,
                    ErrorMessage(ex));
                _logger.LogError(
                    ex,
                    "Geometry registry supervision faulted for {ConnectionId}",
                    registration.ConnectionId);
                await WaitUntilCancelledAsync(cancellationToken);
                return;
            }
        }
    }

    private async Task LoadSnapshotAsync(
        Registration registration,
        CancellationToken cancellationToken)
    {
        var records = await _gateway.ListAsync(
            registration.ConnectionId,
            new GeometryQuery(IncludeObjects: false, Refresh: true),
            cancellationToken);
        var registry = await _gateway.GetRegistryStatusAsync(
            registration.ConnectionId,
            includeDetails: true,
            cancellationToken);
        if (!IsCurrent(registration))
        {
            return;
        }

        _sink.ReplaceRemoteSnapshot(registration.ConnectionId, records, registry);
        Touch(registration, DateTimeOffset.UtcNow);
    }

    private void MarkStaleWatches()
    {
        Registration[] registrations;
        lock (_gate)
        {
            registrations = _registrations.Values.ToArray();
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var registration in registrations)
        {
            if (Volatile.Read(ref registration.Live) == 0)
            {
                continue;
            }

            var ticks = Interlocked.Read(ref registration.LastMessageUtcTicks);
            if (ticks <= 0 || now - new DateTimeOffset(ticks, TimeSpan.Zero) <= StaleAfter)
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref registration.Stale, 1, 0) != 0)
            {
                continue;
            }

            SetStatus(
                registration,
                GeometryRegistryWatchStatusKind.Stale,
                "The geometry registry watch is open but no snapshot, update, or heartbeat has arrived recently.",
                registration.RestartCount,
                "No registry message received within the freshness window.");
        }
    }

    private static bool Touch(Registration registration, DateTimeOffset observedAt)
    {
        var value = observedAt == default ? DateTimeOffset.UtcNow : observedAt.ToUniversalTime();
        Interlocked.Exchange(ref registration.LastMessageUtcTicks, value.UtcDateTime.Ticks);
        return Interlocked.Exchange(ref registration.Stale, 0) == 1;
    }

    private void AnnounceLive(Registration registration, int restarts, string summary)
    {
        Interlocked.Exchange(ref registration.Live, 1);
        Interlocked.Exchange(ref registration.Stale, 0);
        registration.RestartCount = restarts;
        var ticks = Interlocked.Read(ref registration.LastMessageUtcTicks);
        SetStatus(
            registration,
            GeometryRegistryWatchStatusKind.Live,
            summary,
            restarts,
            null,
            ticks <= 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    private void SetStatus(
        Registration registration,
        GeometryRegistryWatchStatusKind status,
        string summary,
        int restarts,
        string? error,
        DateTimeOffset? lastMessageAt = null)
    {
        if (!IsCurrent(registration))
        {
            return;
        }

        registration.RestartCount = restarts;
        var messageTicks = Interlocked.Read(ref registration.LastMessageUtcTicks);
        lastMessageAt ??= messageTicks <= 0
            ? null
            : new DateTimeOffset(messageTicks, TimeSpan.Zero);
        _sink.SetRegistryWatchState(new GeometryRegistryWatchState(
            registration.ConnectionId,
            status,
            summary,
            lastMessageAt,
            restarts,
            error,
            DateTimeOffset.UtcNow));
    }

    private bool IsCurrent(Registration registration)
    {
        lock (_gate)
        {
            return _registrations.TryGetValue(registration.ConnectionId, out var current) &&
                   ReferenceEquals(current, registration) &&
                   current.Generation == registration.Generation;
        }
    }

    private async Task ObserveLifetimeAsync(Registration registration)
    {
        try
        {
            await registration.Lifetime;
        }
        catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unobserved geometry registry supervision failure for {ConnectionId}",
                registration.ConnectionId);
        }
        finally
        {
            if (!IsCurrent(registration))
            {
                registration.DisposeCancellation();
            }
        }
    }

    private async Task StopAllAsync()
    {
        Registration[] registrations;
        lock (_gate)
        {
            registrations = _registrations.Values.ToArray();
            _registrations.Clear();
        }

        foreach (var registration in registrations)
        {
            registration.Cancellation.Cancel();
        }

        foreach (var registration in registrations)
        {
            try
            {
                await registration.Lifetime;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Geometry registry watch stopped with an error for {ConnectionId}",
                    registration.ConnectionId);
            }
            finally
            {
                registration.DisposeCancellation();
                _sink.ClearRemoteConnection(
                    registration.ConnectionId,
                    GeometryRegistryWatchState.Stopped(registration.ConnectionId));
            }
        }
    }

    private static bool IsUnsupported(Exception exception)
        => exception.Message.Contains("unimplemented", StringComparison.OrdinalIgnoreCase) ||
           exception.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(Exception exception)
        => exception switch
        {
            InvalidOperationException => true,
            RpcException rpc => rpc.StatusCode is
                StatusCode.Unavailable or
                StatusCode.DeadlineExceeded or
                StatusCode.ResourceExhausted or
                StatusCode.Internal or
                StatusCode.Unknown or
                StatusCode.Cancelled,
            IOException => true,
            HttpRequestException => true,
            TimeoutException => true,
            _ => false
        };

    private static TimeSpan Backoff(int restartCount)
    {
        var seconds = Math.Min(
            MaximumBackoff.TotalSeconds,
            Math.Pow(2, Math.Clamp(restartCount - 1, 0, 4)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string ErrorMessage(Exception exception)
        => exception is RpcException rpc && !string.IsNullOrWhiteSpace(rpc.Status.Detail)
            ? rpc.Status.Detail
            : exception.Message;

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed class Registration(
        string connectionId,
        long generation,
        CancellationTokenSource cancellation)
    {
        public string ConnectionId { get; } = connectionId;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Lifetime { get; set; } = Task.CompletedTask;
        public long LastMessageUtcTicks;
        public int Live;
        public int Stale;
        public int RestartCount;
        private int _disposed;

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }
}
