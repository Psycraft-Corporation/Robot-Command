using RobotCommand.Core;

namespace RobotCommand.Services.Workflows;

/// <summary>Runtime-only executor registry for reviewed, session-scoped work.</summary>
public sealed class ReviewedOperationWorkflow : IReviewedOperationWorkflow
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _operations = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public IReadOnlyList<ReviewedOperationSnapshot> Operations
    {
        get
        {
            lock (_gate)
            {
                ExpireUnsafe();
                return _operations.Values.Select(entry => entry.Snapshot)
                    .OrderByDescending(item => item.CreatedAt).ToArray();
            }
        }
    }

    public bool TryGet(string id, out ReviewedOperationSnapshot? operation)
    {
        lock (_gate)
        {
            ExpireUnsafe();
            operation = _operations.TryGetValue(id, out var entry) ? entry.Snapshot : null;
            return operation is not null;
        }
    }

    /// <summary>Creates a session-only plan for a Runtime workflow implementation.</summary>
    public ReviewedOperationSnapshot Plan(
        ReviewedOperationKind kind,
        string title,
        IReadOnlyList<WorkflowFinding> findings,
        IReadOnlyList<string> impact,
        string summary,
        IReadOnlyList<string> targetIds,
        Func<CancellationToken, Task<ReviewedOperationExecutionResult>> execute,
        TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ReviewedOperationSnapshot(
            $"review-{Guid.NewGuid():N}", kind, title,
            findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking)
                ? ReviewedOperationState.Unavailable : ReviewedOperationState.Ready,
            now, now.Add(lifetime ?? TimeSpan.FromMinutes(2)), findings, impact, summary, targetIds);
        lock (_gate) _operations[snapshot.Id] = new Entry(snapshot, execute);
        Changed?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    public async Task<ReviewedOperationExecutionResult> ExecuteAsync(string id, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            ExpireUnsafe();
            if (!_operations.TryGetValue(id, out entry!))
                throw new KeyNotFoundException($"Reviewed operation '{id}' was not found.");
            if (!entry.Snapshot.CanExecute)
                return new(id, entry.Snapshot.State, false, entry.Snapshot.Summary, entry.Snapshot.Findings.Select(item => item.Message).ToArray());
            entry = entry with { Snapshot = entry.Snapshot with { State = ReviewedOperationState.Executing } };
            _operations[id] = entry;
        }
        Changed?.Invoke(this, EventArgs.Empty);

        ReviewedOperationExecutionResult result;
        try { result = await entry.Execute(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(id, ReviewedOperationState.Cancelled, false, "Operation cancelled.", []);
        }
        catch (Exception exception)
        {
            result = new(id, ReviewedOperationState.Failed, false, exception.Message, []);
        }

        result = result with { Id = id };

        lock (_gate)
        {
            if (_operations.TryGetValue(id, out var current))
                _operations[id] = current with { Snapshot = current.Snapshot with { State = result.State, Summary = result.Summary } };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public Task CancelAsync(string id, string message = "Operation cancelled by operator.", CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_operations.TryGetValue(id, out var entry))
                _operations[id] = entry with { Snapshot = entry.Snapshot with { State = ReviewedOperationState.Cancelled, Summary = message } };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void ExpireUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in _operations.ToArray())
        {
            if (entry.Snapshot.State == ReviewedOperationState.Ready && entry.Snapshot.ExpiresAt <= now)
                _operations[id] = entry with { Snapshot = entry.Snapshot with { State = ReviewedOperationState.Expired, Summary = "Reviewed operation expired; plan it again before executing." } };
        }
    }

    private sealed record Entry(ReviewedOperationSnapshot Snapshot, Func<CancellationToken, Task<ReviewedOperationExecutionResult>> Execute);
}
