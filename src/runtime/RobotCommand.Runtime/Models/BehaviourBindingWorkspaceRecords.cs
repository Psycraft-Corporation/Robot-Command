namespace RobotCommand.Models;

public sealed record BehaviourBindingPackageOption(
    BehaviourPackageIdentity Identity,
    string DisplayName,
    string Description,
    string Status,
    string Channel,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ProvidedCapabilities,
    IReadOnlyList<BehaviourGeometryRequirement> GeometrySlots,
    DateTimeOffset? UpdatedAt = null,
    bool Active = false,
    bool InUse = false)
{
    public string BehaviourId => Identity.BehaviourId;

    public string Version => Identity.Version ?? string.Empty;

    public string Key => Identity.Key;

    public string Label => string.IsNullOrWhiteSpace(Version)
        ? DisplayName
        : $"{DisplayName} {Version}";

    public bool CanManageBindings => !string.IsNullOrWhiteSpace(Version);

    public BehaviourPackageOption ToLegacyOption(BehaviourGeometryReadiness? readiness = null)
        => new(
            BehaviourId,
            Version,
            DisplayName,
            Description,
            Status,
            Channel,
            RequiredCapabilities,
            ProvidedCapabilities,
            GeometrySlots,
            UpdatedAt,
            readiness);

    public static BehaviourBindingPackageOption FromRemote(RemoteBehaviourPackageRecord package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new BehaviourBindingPackageOption(
            package.Identity,
            string.IsNullOrWhiteSpace(package.DisplayName)
                ? package.Identity.BehaviourId
                : package.DisplayName,
            package.Description,
            package.Status,
            package.Channel,
            package.RequiredCapabilities,
            package.ProvidedCapabilities,
            package.GeometrySlots,
            package.UpdatedAt,
            package.Active,
            package.InUse);
    }
}

public enum BehaviourBindingSlotState
{
    Unknown,
    Ready,
    OptionalUnbound,
    RequiredUnbound,
    GeometryMissing,
    Incompatible,
    BindingIssue,
    Unavailable
}

public sealed record BehaviourGeometryCandidate(
    string GeometryId,
    string DisplayName,
    GeometryDocumentKind Kind,
    GeometryCoordinateFrame Frame,
    string PolicyKind,
    string PolicyConstraint,
    bool Compatible,
    string Summary,
    IReadOnlyList<string> Issues)
{
    public string PolicySummary => string.IsNullOrWhiteSpace(PolicyKind) &&
                                   string.IsNullOrWhiteSpace(PolicyConstraint)
        ? "Not inspected"
        : $"{PolicyKind}/{PolicyConstraint}";
}

public sealed record BehaviourBindingSlotAssessment(
    BehaviourGeometryRequirement Requirement,
    BehaviourGeometryBindingRecord? Binding,
    BehaviourBindingSlotState State,
    string Summary,
    IReadOnlyList<BehaviourGeometryCandidate> Candidates,
    IReadOnlyList<string> Issues)
{
    public string SlotId => Requirement.SlotId;

    public bool Required =>
        (Requirement.RequiredRegistration || Requirement.RequireObjectAtStart) &&
        !Requirement.AllowEmptyGeometry;

    public string? GeometryId => Binding?.GeometryId ?? Requirement.DefaultGeometryId;

    public bool Bound => !string.IsNullOrWhiteSpace(GeometryId);

    public bool Ready => State is BehaviourBindingSlotState.Ready or BehaviourBindingSlotState.OptionalUnbound;

    public IReadOnlyList<BehaviourGeometryCandidate> CompatibleCandidates => Candidates
        .Where(item => item.Compatible)
        .ToArray();

    public static BehaviourBindingSlotAssessment Create(
        BehaviourGeometryRequirement requirement,
        BehaviourGeometryBindingRecord? binding,
        IReadOnlyList<BehaviourGeometryCandidate>? candidates = null,
        string? unavailableMessage = null)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var normalizedCandidates = candidates ?? [];
        var required =
            (requirement.RequiredRegistration || requirement.RequireObjectAtStart) &&
            !requirement.AllowEmptyGeometry;
        var geometryId = binding?.GeometryId ?? requirement.DefaultGeometryId;
        var issues = (binding?.Issues ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(unavailableMessage))
        {
            return new BehaviourBindingSlotAssessment(
                requirement,
                binding,
                BehaviourBindingSlotState.Unavailable,
                unavailableMessage.Trim(),
                normalizedCandidates,
                issues);
        }

        if (string.IsNullOrWhiteSpace(geometryId))
        {
            return new BehaviourBindingSlotAssessment(
                requirement,
                binding,
                required
                    ? BehaviourBindingSlotState.RequiredUnbound
                    : BehaviourBindingSlotState.OptionalUnbound,
                required
                    ? $"Required geometry slot '{requirement.SlotId}' is unbound."
                    : $"Optional geometry slot '{requirement.SlotId}' is unbound.",
                normalizedCandidates,
                issues);
        }

        if (binding is null)
        {
            return new BehaviourBindingSlotAssessment(
                requirement,
                null,
                BehaviourBindingSlotState.Unknown,
                $"Geometry '{geometryId}' has not been verified on the selected Logos runtime.",
                normalizedCandidates,
                issues);
        }

        if (!binding.ObjectExists)
        {
            return new BehaviourBindingSlotAssessment(
                requirement,
                binding,
                BehaviourBindingSlotState.GeometryMissing,
                $"Geometry '{geometryId}' is not present on the selected Logos runtime.",
                normalizedCandidates,
                issues);
        }

        var selectedCandidate = normalizedCandidates.FirstOrDefault(item =>
            string.Equals(item.GeometryId, geometryId, StringComparison.Ordinal));
        if (selectedCandidate is { Compatible: false })
        {
            return new BehaviourBindingSlotAssessment(
                requirement,
                binding,
                BehaviourBindingSlotState.Incompatible,
                selectedCandidate.Summary,
                normalizedCandidates,
                issues.Concat(selectedCandidate.Issues).Distinct(StringComparer.Ordinal).ToArray());
        }

        if (issues.Length > 0)
        {
            return new BehaviourBindingSlotAssessment(
                requirement,
                binding,
                BehaviourBindingSlotState.BindingIssue,
                string.Join(" ", issues),
                normalizedCandidates,
                issues);
        }

        return new BehaviourBindingSlotAssessment(
            requirement,
            binding,
            BehaviourBindingSlotState.Ready,
            $"Geometry '{geometryId}' is ready for slot '{requirement.SlotId}'.",
            normalizedCandidates,
            issues);
    }
}

public sealed record BehaviourBindingWorkspaceSnapshot(
    string ConnectionId,
    BehaviourBindingPackageOption Package,
    bool Available,
    bool CandidateInventoryAvailable,
    string Summary,
    BehaviourGeometryReadiness Readiness,
    IReadOnlyList<BehaviourBindingSlotAssessment> Slots,
    IReadOnlyList<string> Warnings,
    DateTimeOffset RefreshedAt)
{
    public BehaviourPackageIdentity Identity => Package.Identity;

    public bool Ready => Available && Readiness.Ready;

    public string Key => $"{ConnectionId}:{Identity.Key}";

    public static BehaviourBindingWorkspaceSnapshot Unavailable(
        string connectionId,
        BehaviourBindingPackageOption package,
        string message,
        DateTimeOffset? refreshedAt = null)
    {
        var blocker = string.IsNullOrWhiteSpace(message)
            ? "Behaviour geometry bindings are unavailable."
            : message.Trim();
        var readiness = new BehaviourGeometryReadiness(
            connectionId,
            package.BehaviourId,
            package.Version,
            [],
            [],
            [],
            [blocker]);
        return new BehaviourBindingWorkspaceSnapshot(
            connectionId,
            package,
            false,
            false,
            blocker,
            readiness,
            package.GeometrySlots
                .Select(item => BehaviourBindingSlotAssessment.Create(
                    item,
                    null,
                    unavailableMessage: blocker))
                .ToArray(),
            [],
            refreshedAt ?? DateTimeOffset.UtcNow);
    }
}

public sealed record BehaviourBindingMutationResult(
    bool Accepted,
    bool Verified,
    string Message,
    BehaviourBindingWorkspaceSnapshot? Snapshot = null,
    IReadOnlyList<string>? Issues = null)
{
    public bool Succeeded => Accepted && Verified;
}
