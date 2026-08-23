using System.Collections.Specialized;
using Microsoft.Extensions.Hosting;
using RobotCommand.Models;
using RobotCommand.State;

namespace RobotCommand.Services.Missions;

public sealed class MissionTaskEventProjectionService : IHostedService, IDisposable
{
    private readonly IEntityStore<string, ConsoleEventRecord> _events;
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly INotifyCollectionChanged _observableEvents;

    public MissionTaskEventProjectionService(
        IEntityStore<string, ConsoleEventRecord> events,
        IEntityStore<string, MissionRecord> missions,
        IEntityStore<string, OperationalTaskRecord> tasks)
    {
        _events = events;
        _missions = missions;
        _tasks = tasks;
        _observableEvents = (INotifyCollectionChanged)events.Items;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var item in _events.Items.OrderBy(item => item.Timestamp))
        {
            Project(item);
        }

        _observableEvents.CollectionChanged += OnEventsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _observableEvents.CollectionChanged -= OnEventsChanged;
        return Task.CompletedTask;
    }

    public void Dispose() => _observableEvents.CollectionChanged -= OnEventsChanged;

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems.OfType<ConsoleEventRecord>())
        {
            Project(item);
        }
    }

    private void Project(ConsoleEventRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.SubjectId))
        {
            return;
        }

        var domain = item.Domain ?? string.Empty;
        if (domain.Contains("Mission", StringComparison.OrdinalIgnoreCase))
        {
            ProjectMission(item);
        }
        else if (domain.Contains("Task", StringComparison.OrdinalIgnoreCase))
        {
            ProjectTask(item);
        }
    }

    private void ProjectMission(ConsoleEventRecord item)
    {
        var id = item.SubjectId!;
        var inferredState = InferState(item, MissionStates);
        if (_missions.TryGet(id, out var existing) && existing is not null)
        {
            _missions.Upsert(existing with
            {
                State = inferredState ?? existing.State,
                ConnectionId = existing.ConnectionId ?? item.ConnectionId,
                ObservedAt = item.Timestamp,
                IsLocalDraft = false
            });
            return;
        }

        _missions.Upsert(new MissionRecord(
            id,
            id,
            inferredState ?? "Observed",
            ConnectionId: item.ConnectionId,
            Objective: item.Message,
            ValidationState: PlanValidationState.Unavailable,
            ValidationSummary: "Plan was not retrieved; state is projected from Logos events.",
            ObservedAt: item.Timestamp,
            IsLocalDraft: false));
    }

    private void ProjectTask(ConsoleEventRecord item)
    {
        var id = item.SubjectId!;
        var inferredState = InferState(item, TaskStates);
        if (_tasks.TryGet(id, out var existing) && existing is not null)
        {
            _tasks.Upsert(existing with
            {
                State = inferredState ?? existing.State,
                ConnectionId = existing.ConnectionId ?? item.ConnectionId,
                ObservedAt = item.Timestamp,
                IsLocalDraft = false
            });
            return;
        }

        _tasks.Upsert(new OperationalTaskRecord(
            id,
            id,
            inferredState ?? "Observed",
            ConnectionId: item.ConnectionId,
            Objective: item.Message,
            ValidationState: PlanValidationState.Unavailable,
            ValidationSummary: "Plan was not retrieved; state is projected from Logos events.",
            ObservedAt: item.Timestamp,
            IsLocalDraft: false));
    }

    private static string? InferState(
        ConsoleEventRecord item,
        IReadOnlyList<string> candidates)
    {
        var text = string.Join(' ', item.Kind, item.Code, item.Message).ToLowerInvariant();
        foreach (var candidate in candidates)
        {
            var normalized = candidate.Replace(" ", string.Empty).ToLowerInvariant();
            if (text.Replace("_", string.Empty).Replace("-", string.Empty).Contains(normalized, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static readonly string[] MissionStates =
    [
        "Completed", "Cancelled", "Aborted", "Faulted", "Blocked", "Paused",
        "Running", "Ready", "Idle", "Boot"
    ];

    private static readonly string[] TaskStates =
    [
        "Succeeded", "Failed", "Aborted", "Cancelled", "Timed Out", "Blocked",
        "Running", "Accepted", "Rejected", "Assigned", "Ready", "Draft"
    ];
}
