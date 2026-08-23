namespace RobotCommand.Models;

public static class BehaviourOperationConfirmation
{
    public static string ForRemote(BehaviourPackageOperationAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return $"{assessment.Operation.ToString().ToUpperInvariant()} {assessment.Identity.Key}";
    }

    public static string ForLocalRemoval(BehaviourPackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"REMOVE LOCAL {identity.Key}";
    }

    public static bool Matches(string? entered, string required)
        => !string.IsNullOrWhiteSpace(required) &&
           string.Equals(entered?.Trim(), required, StringComparison.Ordinal);
}
