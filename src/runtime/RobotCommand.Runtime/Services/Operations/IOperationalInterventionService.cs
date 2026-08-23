using RobotCommand.Models;

namespace RobotCommand.Services.Operations;

public interface IOperationalInterventionService
{
    event EventHandler? Changed;

    OperationalInterventionPreparation? Preparation { get; }

    OperationalInterventionResult? LastResult { get; }

    Task<OperationalInterventionPreparation> PrepareAsync(
        OperationalInterventionKind kind,
        string reason,
        CancellationToken cancellationToken = default);

    Task<OperationalInterventionResult> ExecuteAsync(
        string preparationId,
        string confirmationText,
        CancellationToken cancellationToken = default);

    Task DiscardAsync(
        string? preparationId = null,
        CancellationToken cancellationToken = default);
}

public sealed class NullOperationalInterventionService : IOperationalInterventionService
{
    public static NullOperationalInterventionService Instance { get; } = new();

    private NullOperationalInterventionService()
    {
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public OperationalInterventionPreparation? Preparation => null;

    public OperationalInterventionResult? LastResult => null;

    public Task<OperationalInterventionPreparation> PrepareAsync(
        OperationalInterventionKind kind,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Operational intervention is unavailable.");
    }

    public Task<OperationalInterventionResult> ExecuteAsync(
        string preparationId,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Operational intervention is unavailable.");
    }

    public Task DiscardAsync(
        string? preparationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
