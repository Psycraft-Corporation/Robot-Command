using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public sealed class OperationalInspectionTargetService : IOperationalInspectionTargetService
{
    private readonly object _gate = new();
    private OperationalInspectionRequest? _current;
    private bool _openRequested;
    private long _sequence;

    public event EventHandler? Changed;

    public OperationalInspectionRequest? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool OpenRequested
    {
        get
        {
            lock (_gate)
            {
                return _openRequested;
            }
        }
    }

    public OperationalInspectionRequest Publish(
        OperationalExecutionTarget target,
        OperationalInspectionSource source,
        string sourceDescription,
        bool openInspector = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateTarget(target);

        OperationalInspectionRequest request;
        lock (_gate)
        {
            request = new OperationalInspectionRequest(
                unchecked(++_sequence),
                target,
                source,
                sourceDescription?.Trim() ?? string.Empty,
                openInspector,
                DateTimeOffset.UtcNow);
            _current = request;
            _openRequested = openInspector;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return request;
    }

    public void AcknowledgeOpenRequest()
    {
        var changed = false;
        lock (_gate)
        {
            if (_openRequested)
            {
                _openRequested = false;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear()
    {
        var changed = false;
        lock (_gate)
        {
            if (_current is not null || _openRequested)
            {
                _current = null;
                _openRequested = false;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void ValidateTarget(OperationalExecutionTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ConnectionId) ||
            string.IsNullOrWhiteSpace(target.VehicleId) ||
            string.IsNullOrWhiteSpace(target.MissionId) ||
            string.IsNullOrWhiteSpace(target.TaskId))
        {
            throw new ArgumentException(
                "An inspection target requires connection, vehicle, mission, and task IDs.",
                nameof(target));
        }
    }
}
