namespace RobotCommand.Models;

public sealed record TaskBehaviourChoice(
    BehaviourWorkspaceEntry Package,
    BehaviourBindingWorkspaceSnapshot Bindings)
{
    public BehaviourPackageIdentity Identity => Package.Identity;

    public string Label => string.IsNullOrWhiteSpace(Identity.Version)
        ? Package.DisplayName
        : $"{Package.DisplayName} {Identity.Version}";

    public bool Ready =>
        Package.Remote is not null &&
        Package.Compatible &&
        Bindings.Ready &&
        !string.IsNullOrWhiteSpace(Identity.Version);

    public string Summary => !Package.Compatible
        ? Package.Compatibility?.Summary ?? "The selected vehicle is incompatible."
        : !Bindings.Ready
            ? Bindings.Summary
            : $"Installed package {Identity.Key} is compatible and its required geometry is ready.";
}

public static class TaskBehaviourComposition
{
    public static OperationalTaskRecord Apply(
        OperationalTaskRecord task,
        TaskBehaviourChoice choice,
        string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(choice);
        if (!task.IsLocalDraft)
        {
            throw new InvalidOperationException("Only local task drafts can be composed in Robot Command.");
        }
        if (!choice.Ready)
        {
            throw new InvalidOperationException(choice.Summary);
        }
        if (choice.Package.Remote is null)
        {
            throw new InvalidOperationException("The selected behaviour package is not installed on Logos.");
        }
        if (string.IsNullOrWhiteSpace(choice.Identity.Version))
        {
            throw new InvalidOperationException("Task plans require an explicit installed behaviour version.");
        }
        if (string.IsNullOrWhiteSpace(choice.Bindings.ConnectionId))
        {
            throw new InvalidOperationException("The selected installed behaviour does not have a Logos connection identity.");
        }

        var validation = global::RobotCommand.Services.Behaviours.BehaviourParameterValueCodec.ValidateRawJson(parametersJson);
        if (!validation.Valid)
        {
            throw new InvalidOperationException(validation.Summary);
        }

        var geometryIds = (task.GeometryIds ?? [])
            .Concat(choice.Bindings.Readiness.GeometryIds)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        return task with
        {
            BehaviourId = choice.Identity.BehaviourId,
            BehaviourVersion = choice.Identity.Version,
            PackageId = choice.Identity.BehaviourId,
            ConnectionId = choice.Bindings.ConnectionId,
            ParametersJson = validation.CanonicalJson,
            GeometryIds = geometryIds,
            ValidationState = PlanValidationState.NotValidated,
            ValidationSummary = "Behaviour, parameters, or geometry changed; validate the task again."
        };
    }
}
