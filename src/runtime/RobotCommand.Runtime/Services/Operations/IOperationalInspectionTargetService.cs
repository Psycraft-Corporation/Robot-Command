using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperationalInspectionTargetService
{
    event EventHandler? Changed;

    OperationalInspectionRequest? Current { get; }

    bool OpenRequested { get; }

    OperationalInspectionRequest Publish(
        OperationalExecutionTarget target,
        OperationalInspectionSource source,
        string sourceDescription,
        bool openInspector = false);

    void AcknowledgeOpenRequest();

    void Clear();
}

public sealed class NullOperationalInspectionTargetService : IOperationalInspectionTargetService
{
    public static NullOperationalInspectionTargetService Instance { get; } = new();

    private NullOperationalInspectionTargetService()
    {
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public OperationalInspectionRequest? Current => null;

    public bool OpenRequested => false;

    public OperationalInspectionRequest Publish(
        OperationalExecutionTarget target,
        OperationalInspectionSource source,
        string sourceDescription,
        bool openInspector = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new OperationalInspectionRequest(
            0,
            target,
            source,
            sourceDescription,
            openInspector,
            DateTimeOffset.UtcNow);
    }

    public void AcknowledgeOpenRequest()
    {
    }

    public void Clear()
    {
    }
}
