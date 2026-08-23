using RobotCommand.Models;

namespace RobotCommand.ViewModels;

public sealed class BehaviourBindingSlotItemViewModel
{
    public BehaviourBindingSlotItemViewModel(BehaviourBindingSlotAssessment assessment)
    {
        Assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
    }

    public BehaviourBindingSlotAssessment Assessment { get; }

    public string SlotId => Assessment.SlotId;

    public string RequirementLabel => Assessment.Required ? "Required" : "Optional";

    public string ExpectedKind => BehaviourGeometryCompatibility.ExpectedKindLabel(
        Assessment.Requirement.KindHint);

    public string ExpectedPolicy => string.IsNullOrWhiteSpace(Assessment.Requirement.ExpectedPolicyKind)
        ? "Any policy"
        : Assessment.Requirement.ExpectedPolicyKind;

    public string GeometryId => Assessment.GeometryId ?? string.Empty;

    public bool Bound => Assessment.Bound;

    public bool Ready => Assessment.Ready;

    public string State => Assessment.State.ToString();

    public string Summary => Assessment.Summary;

    public string Issues => Assessment.Issues.Count == 0
        ? "No binding issues"
        : string.Join(Environment.NewLine, Assessment.Issues);

    public IReadOnlyList<BehaviourGeometryCandidate> CompatibleCandidates =>
        Assessment.CompatibleCandidates;
}
