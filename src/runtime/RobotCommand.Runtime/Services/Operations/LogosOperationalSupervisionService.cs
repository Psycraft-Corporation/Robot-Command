using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Missions;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Operations;

public sealed class LogosOperationalSupervisionService : IOperationalSupervisionService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(15);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly ILogger<LogosOperationalSupervisionService> _logger;

    private OperationalExecutionSnapshot _snapshot = OperationalExecutionSnapshot.Empty;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchLifetime;
    private int _generation;
    private int _disposed;

    public LogosOperationalSupervisionService(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata,
        ILogger<LogosOperationalSupervisionService> logger)
    {
        _sessions = sessions;
        _metadata = metadata;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public OperationalExecutionSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot;
            }
        }
    }

    public async Task BeginAsync(
        OperationalExecutionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(clear: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.TryGet(target.ConnectionId, out var session) || session is null)
            {
                throw new InvalidOperationException(
                    $"Connection '{target.ConnectionId}' does not have an active Logos operational session.");
            }

            var generation = unchecked(++_generation);
            var cancellation = new CancellationTokenSource();
            _watchCancellation = cancellation;
            ReplaceSnapshot(new OperationalExecutionSnapshot(
                target,
                null,
                null,
                null,
                [],
                Starting("Mission"),
                Starting("Task"),
                Starting("Autonomy"),
                DateTimeOffset.UtcNow));

            _watchLifetime = Task.WhenAll(
                WatchMissionLoopAsync(generation, target, cancellation.Token),
                WatchTaskLoopAsync(generation, target, cancellation.Token),
                WatchAutonomyLoopAsync(generation, target, cancellation.Token));
            _ = ObserveLifetimeAsync(_watchLifetime, generation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(clear: false);
            UpdateSnapshot(current => current with
            {
                MissionWatch = OperationalWatchStatus.Stopped("Mission"),
                TaskWatch = OperationalWatchStatus.Stopped("Task"),
                AutonomyWatch = OperationalWatchStatus.Stopped("Autonomy"),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(clear: true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync(clear: true);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task WatchMissionLoopAsync(
        int generation,
        OperationalExecutionTarget target,
        CancellationToken cancellationToken)
    {
        var restarts = 0;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
        {
            try
            {
                SetMissionWatch(generation, Connecting("Mission", restarts));
                var session = RequireSession(target.ConnectionId);
                using var call = session.Clients.Missions.WatchMission(
                    new V1.WatchMissionRequest
                    {
                        RequestId = _metadata.CreateRequestId(),
                        CorrelationId = _metadata.CreateCorrelationId(target.CorrelationId),
                        MissionId = target.MissionId,
                        MissionExecutionId = target.MissionExecutionId ?? string.Empty,
                        IncludeHeartbeats = true,
                        HeartbeatInterval = Duration.FromTimeSpan(HeartbeatInterval),
                        IncludeTasks = true,
                        IncludeDetails = true
                    },
                    cancellationToken: cancellationToken);

                SetMissionWatch(generation, Live("Mission", restarts));
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var response = call.ResponseStream.Current;
                    var mapped = OperationalSupervisionMapper.MapMission(response);
                    UpdateIfCurrent(generation, current => current with
                    {
                        Mission = mapped ?? current.Mission,
                        MissionWatch = Message("Mission", response.EventType.ToString(), restarts),
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }

                SetMissionWatch(generation, Completed("Mission", restarts));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                SetMissionWatch(generation, Unsupported("Mission", ex));
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                restarts++;
                SetMissionWatch(generation, BackingOff("Mission", ex, restarts));
                await DelayForRestartAsync(restarts, cancellationToken);
            }
            catch (Exception ex)
            {
                SetMissionWatch(generation, Faulted("Mission", ex, restarts));
                return;
            }
        }
    }

    private async Task WatchTaskLoopAsync(
        int generation,
        OperationalExecutionTarget target,
        CancellationToken cancellationToken)
    {
        var restarts = 0;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
        {
            try
            {
                SetTaskWatch(generation, Connecting("Task", restarts));
                var session = RequireSession(target.ConnectionId);
                using var call = session.Clients.Tasks.WatchTask(
                    new V1.WatchTaskRequest
                    {
                        RequestId = _metadata.CreateRequestId(),
                        CorrelationId = _metadata.CreateCorrelationId(target.CorrelationId),
                        TaskId = target.TaskId,
                        TaskExecutionId = target.TaskExecutionId ?? string.Empty,
                        IncludeHeartbeats = true,
                        HeartbeatInterval = Duration.FromTimeSpan(HeartbeatInterval),
                        IncludeDetails = true
                    },
                    cancellationToken: cancellationToken);

                SetTaskWatch(generation, Live("Task", restarts));
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var response = call.ResponseStream.Current;
                    var mapped = OperationalSupervisionMapper.MapTask(response);
                    UpdateIfCurrent(generation, current => current with
                    {
                        Task = mapped ?? current.Task,
                        TaskWatch = Message("Task", response.EventType.ToString(), restarts),
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }

                SetTaskWatch(generation, Completed("Task", restarts));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                SetTaskWatch(generation, Unsupported("Task", ex));
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                restarts++;
                SetTaskWatch(generation, BackingOff("Task", ex, restarts));
                await DelayForRestartAsync(restarts, cancellationToken);
            }
            catch (Exception ex)
            {
                SetTaskWatch(generation, Faulted("Task", ex, restarts));
                return;
            }
        }
    }

    private async Task WatchAutonomyLoopAsync(
        int generation,
        OperationalExecutionTarget target,
        CancellationToken cancellationToken)
    {
        var restarts = 0;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
        {
            try
            {
                SetAutonomyWatch(generation, Connecting("Autonomy", restarts));
                var session = RequireSession(target.ConnectionId);
                using var call = session.Clients.Autonomy.WatchAutonomyRuntime(
                    new V1.WatchAutonomyRuntimeRequest
                    {
                        RequestId = _metadata.CreateRequestId(),
                        CorrelationId = _metadata.CreateCorrelationId(target.CorrelationId),
                        VehicleId = target.VehicleId,
                        LogosInstanceId = target.LogosInstanceId ?? string.Empty,
                        IncludeHeartbeats = true,
                        HeartbeatInterval = Duration.FromTimeSpan(HeartbeatInterval),
                        IncludeBehaviour = true,
                        IncludeStatechart = true,
                        IncludeGeometryBindings = true,
                        IncludePackageUpdates = false,
                        IncludeTreeStatus = true,
                        IncludeAllTreeNodes = true,
                        IncludeDetails = true
                    },
                    cancellationToken: cancellationToken);

                SetAutonomyWatch(generation, Live("Autonomy", restarts));
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    var response = call.ResponseStream.Current;
                    UpdateIfCurrent(generation, current =>
                    {
                        var mapped = OperationalSupervisionMapper.MapAutonomy(response);
                        var nodes = OperationalSupervisionMapper.IsDifferentTree(current.Autonomy, mapped)
                            ? OperationalSupervisionMapper.MergeNodes([], response)
                            : OperationalSupervisionMapper.MergeNodes(current.TreeNodes, response);
                        return current with
                        {
                            Autonomy = mapped ?? current.Autonomy,
                            TreeNodes = nodes,
                            AutonomyWatch = Message("Autonomy", response.EventType.ToString(), restarts),
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                    });
                }

                SetAutonomyWatch(generation, Completed("Autonomy", restarts));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                SetAutonomyWatch(generation, Unsupported("Autonomy", ex));
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                restarts++;
                SetAutonomyWatch(generation, BackingOff("Autonomy", ex, restarts));
                await DelayForRestartAsync(restarts, cancellationToken);
            }
            catch (Exception ex)
            {
                SetAutonomyWatch(generation, Faulted("Autonomy", ex, restarts));
                return;
            }
        }
    }

    private ILogosOperationalSession RequireSession(string connectionId)
        => _sessions.TryGet(connectionId, out var session) && session is not null
            ? session
            : throw new InvalidOperationException(
                $"Connection '{connectionId}' no longer has an active Logos operational session.");

    private async Task StopCoreAsync(bool clear)
    {
        var cancellation = _watchCancellation;
        var lifetime = _watchLifetime;
        _watchCancellation = null;
        _watchLifetime = null;
        unchecked { _generation++; }

        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
        }

        if (lifetime is not null)
        {
            try
            {
                await lifetime;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Operational supervision watch stopped with an error");
            }
        }

        cancellation?.Dispose();
        if (clear)
        {
            ReplaceSnapshot(OperationalExecutionSnapshot.Empty);
        }
    }

    private async Task ObserveLifetimeAsync(Task lifetime, int generation)
    {
        try
        {
            await lifetime;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Operational supervision lifetime faulted");
            UpdateIfCurrent(generation, current => current with
            {
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private void SetMissionWatch(int generation, OperationalWatchStatus status)
        => UpdateIfCurrent(generation, current => current with
        {
            MissionWatch = status,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private void SetTaskWatch(int generation, OperationalWatchStatus status)
        => UpdateIfCurrent(generation, current => current with
        {
            TaskWatch = status,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private void SetAutonomyWatch(int generation, OperationalWatchStatus status)
        => UpdateIfCurrent(generation, current => current with
        {
            AutonomyWatch = status,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    private void UpdateIfCurrent(
        int generation,
        Func<OperationalExecutionSnapshot, OperationalExecutionSnapshot> update)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        UpdateSnapshot(current => IsCurrent(generation) ? update(current) : current);
    }

    private bool IsCurrent(int generation)
        => Volatile.Read(ref _generation) == generation && Volatile.Read(ref _disposed) == 0;

    private void UpdateSnapshot(
        Func<OperationalExecutionSnapshot, OperationalExecutionSnapshot> update)
    {
        var changed = false;
        lock (_stateGate)
        {
            var replacement = update(_snapshot);
            if (!ReferenceEquals(replacement, _snapshot) && replacement != _snapshot)
            {
                _snapshot = replacement;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReplaceSnapshot(OperationalExecutionSnapshot snapshot)
    {
        lock (_stateGate)
        {
            _snapshot = snapshot;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static OperationalWatchStatus Starting(string domain) => new(
        OperationalWatchState.Starting,
        $"Starting {domain.ToLowerInvariant()} watch",
        "Opening an authoritative Logos stream.",
        DateTimeOffset.UtcNow);

    private static OperationalWatchStatus Connecting(string domain, int restarts) => new(
        OperationalWatchState.Starting,
        $"Connecting {domain.ToLowerInvariant()} watch",
        restarts == 0 ? "Opening the stream." : "Reopening the stream after an interruption.",
        DateTimeOffset.UtcNow,
        RestartCount: restarts);

    private static OperationalWatchStatus Live(string domain, int restarts) => new(
        OperationalWatchState.Live,
        $"{domain} watch live",
        "Receiving authoritative Logos updates.",
        DateTimeOffset.UtcNow,
        RestartCount: restarts);

    private static OperationalWatchStatus Message(string domain, string eventType, int restarts) => new(
        OperationalWatchState.Live,
        $"{domain} watch live",
        string.IsNullOrWhiteSpace(eventType) ? "Update received." : $"Latest event: {eventType}.",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        restarts);

    private static OperationalWatchStatus Completed(string domain, int restarts) => new(
        OperationalWatchState.Completed,
        $"{domain} watch completed",
        "The server closed the stream cleanly.",
        DateTimeOffset.UtcNow,
        RestartCount: restarts);

    private static OperationalWatchStatus BackingOff(
        string domain,
        Exception exception,
        int restarts) => new(
        OperationalWatchState.BackingOff,
        $"{domain} watch reconnecting",
        "The stream was interrupted; Robot Command will retry.",
        DateTimeOffset.UtcNow,
        RestartCount: restarts,
        LastError: SafeMessage(exception));

    private static OperationalWatchStatus Unsupported(string domain, Exception exception) => new(
        OperationalWatchState.Unsupported,
        $"{domain} watch unsupported",
        "This connected Logos runtime does not implement the requested watch API.",
        DateTimeOffset.UtcNow,
        LastError: SafeMessage(exception));

    private static OperationalWatchStatus Faulted(
        string domain,
        Exception exception,
        int restarts) => new(
        OperationalWatchState.Faulted,
        $"{domain} watch faulted",
        "The watch stopped because of a non-transient error.",
        DateTimeOffset.UtcNow,
        RestartCount: restarts,
        LastError: SafeMessage(exception));

    private static bool IsTransient(Exception exception)
        => exception is InvalidOperationException ||
           exception is RpcException rpc && rpc.StatusCode is
               StatusCode.Unavailable or
               StatusCode.DeadlineExceeded or
               StatusCode.ResourceExhausted or
               StatusCode.Aborted or
               StatusCode.Internal or
               StatusCode.Unknown;

    private static async Task DelayForRestartAsync(
        int restartCount,
        CancellationToken cancellationToken)
    {
        var exponent = Math.Min(4, Math.Max(0, restartCount - 1));
        var seconds = Math.Min(MaximumBackoff.TotalSeconds, Math.Pow(2, exponent));
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    private static string SafeMessage(Exception exception)
        => exception is RpcException rpc
            ? $"{rpc.StatusCode}: {rpc.Status.Detail}"
            : exception.Message;

    private static void ValidateTarget(OperationalExecutionTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ConnectionId) ||
            string.IsNullOrWhiteSpace(target.VehicleId) ||
            string.IsNullOrWhiteSpace(target.MissionId) ||
            string.IsNullOrWhiteSpace(target.TaskId))
        {
            throw new ArgumentException(
                "Operational supervision requires connection, vehicle, mission, and task IDs.",
                nameof(target));
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
