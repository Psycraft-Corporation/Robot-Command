using RobotCommand.Models;

namespace RobotCommand.ViewModels;

public sealed class BehaviourGeometrySlotViewModel
{
    public BehaviourGeometrySlotViewModel(BehaviourBindingSlotAssessment assessment)
    {
        Assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
    }

    public BehaviourGeometrySlotViewModel(
        BehaviourGeometryRequirement requirement,
        BehaviourGeometryBindingRecord? binding)
        : this(BehaviourBindingSlotAssessment.Create(requirement, binding))
    {
    }

    public BehaviourBindingSlotAssessment Assessment { get; }

    public BehaviourGeometryRequirement Requirement => Assessment.Requirement;

    public BehaviourGeometryBindingRecord? Binding => Assessment.Binding;

    public string SlotId => Requirement.SlotId;

    public string ExpectedKind => BehaviourGeometryCompatibility.ExpectedKindLabel(Requirement.KindHint);

    public string ExpectedPolicy => string.IsNullOrWhiteSpace(Requirement.ExpectedPolicyKind)
        ? "Any policy"
        : Requirement.ExpectedPolicyKind;

    public bool Required => Assessment.Required;

    public string RequirementLabel => Required ? "Required" : "Optional";

    public string GeometryId => Assessment.GeometryId ?? string.Empty;

    public bool Bound => Assessment.Bound;

    public bool ObjectExists => Binding?.ObjectExists == true;

    public bool Ready => Assessment.Ready;

    public string Status => Assessment.Summary;

    public string Issues => Assessment.Issues.Count > 0
        ? string.Join(Environment.NewLine, Assessment.Issues)
        : string.Empty;

    public IReadOnlyList<string> CompatibleGeometryIds => Assessment.CompatibleCandidates
        .Select(item => item.GeometryId)
        .ToArray();
}
