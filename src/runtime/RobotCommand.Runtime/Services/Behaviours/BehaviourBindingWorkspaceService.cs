using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Missions;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Shared application workflow for installed behaviour geometry requirements,
/// live Logos bindings, candidate geometry, and post-command verification.
/// The underlying Logos services remain authoritative for package validity,
/// geometry registration, and binding mutation acceptance.
/// </summary>
public sealed class BehaviourBindingWorkspaceService : IBehaviourBindingWorkspaceService
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IBehaviourWorkspaceService _behaviours;
    private readonly IBehaviourGeometryBindingService _bindings;
    private readonly IGeometryGateway _geometry;
    private readonly ILogger<BehaviourBindingWorkspaceService> _logger;
    private readonly Dictionary<string, BehaviourBindingWorkspaceSnapshot> _snapshots =
        new(StringComparer.Ordinal);

    public BehaviourBindingWorkspaceService(
        IBehaviourWorkspaceService behaviours,
        IBehaviourGeometryBindingService bindings,
        IGeometryGateway geometry,
        ILogger<BehaviourBindingWorkspaceService> logger)
    {
        _behaviours = behaviours;
        _bindings = bindings;
        _geometry = geometry;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public bool IsAvailable => _bindings.IsAvailable && _geometry.IsAvailable;

    public string AvailabilityMessage => IsAvailable
        ? "Behaviour geometry binding and geometry registry services are available."
        : string.Join(" ", new[]
            {
                _bindings.IsAvailable ? null : _bindings.AvailabilityMessage,
                _geometry.IsAvailable ? null : _geometry.AvailabilityMessage
            }
            .Where(item => !string.IsNullOrWhiteSpace(item)));

    public BehaviourBindingWorkspaceSnapshot? GetSnapshot(
        string connectionId,
        BehaviourPackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = SnapshotKey(Normalize(connectionId, "connection ID"), identity);
        lock (_stateGate)
        {
            return _snapshots.GetValueOrDefault(key);
        }
    }

    public async Task<IReadOnlyList<BehaviourBindingPackageOption>> ListPackagesAsync(
        string connectionId,
        bool refreshPackages = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedConnection = Normalize(connectionId, "connection ID");
        var workspace = await _behaviours.RefreshAsync(
            normalizedConnection,
            refreshRemote: refreshPackages,
            cancellationToken: cancellationToken);
        return workspace.Entries
            .Where(item => item.Remote is not null)
            .Select(item => BehaviourBindingPackageOption.FromRemote(item.Remote!))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<BehaviourBindingWorkspaceSnapshot> InspectAsync(
        string connectionId,
        BehaviourPackageIdentity identity,
        bool refreshPackages = false,
        bool refreshGeometry = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var normalizedConnection = Normalize(connectionId, "connection ID");
        var workspace = await _behaviours.RefreshAsync(
            normalizedConnection,
            refreshRemote: refreshPackages,
            cancellationToken: cancellationToken);
        var entry = workspace.Entries.FirstOrDefault(item => item.Identity == identity);
        var package = entry?.Remote is null
            ? MissingPackage(identity)
            : BehaviourBindingPackageOption.FromRemote(entry.Remote);

        if (entry?.Remote is null)
        {
            return Store(BehaviourBindingWorkspaceSnapshot.Unavailable(
                normalizedConnection,
                package,
                $"Behaviour package '{identity}' is not installed on the selected Logos runtime."));
        }

        if (!workspace.RemoteInventory.Available || workspace.RemoteInventory.Stale)
        {
            return Store(BehaviourBindingWorkspaceSnapshot.Unavailable(
                normalizedConnection,
                package,
                workspace.RemoteInventory.Summary));
        }

        if (!package.CanManageBindings)
        {
            return Store(BehaviourBindingWorkspaceSnapshot.Unavailable(
                normalizedConnection,
                package,
                $"Behaviour package '{identity.BehaviourId}' does not report a version, so its bindings cannot be addressed safely."));
        }

        if (package.GeometrySlots.Count == 0)
        {
            var noRequirements = BehaviourGeometryReadiness.NoRequirements(
                normalizedConnection,
                package.BehaviourId,
                package.Version);
            return Store(new BehaviourBindingWorkspaceSnapshot(
                normalizedConnection,
                package,
                true,
                true,
                $"{package.Label} does not declare geometry slots.",
                noRequirements,
                [],
                [],
                DateTimeOffset.UtcNow));
        }

        BehaviourGeometryReadiness readiness;
        try
        {
            readiness = await _bindings.AssessAsync(
                normalizedConnection,
                package.ToLegacyOption(),
                refreshGeometry,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not assess behaviour geometry bindings for {BehaviourId}@{Version}",
                package.BehaviourId,
                package.Version);
            return Store(BehaviourBindingWorkspaceSnapshot.Unavailable(
                normalizedConnection,
                package,
                $"Could not inspect behaviour geometry bindings: {ex.Message}"));
        }

        var candidateWarnings = new List<string>();
        IReadOnlyDictionary<string, IReadOnlyList<BehaviourGeometryCandidate>> candidatesBySlot;
        var candidateInventoryAvailable = true;
        try
        {
            candidatesBySlot = await BuildCandidatesAsync(
                normalizedConnection,
                package.GeometrySlots,
                refreshGeometry,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            candidateInventoryAvailable = false;
            candidateWarnings.Add($"Could not load compatible geometry candidates: {ex.Message}");
            candidatesBySlot = package.GeometrySlots.ToDictionary(
                item => item.SlotId,
                _ => (IReadOnlyList<BehaviourGeometryCandidate>)[],
                StringComparer.Ordinal);
            _logger.LogWarning(
                ex,
                "Could not load geometry candidates for {BehaviourId}@{Version}",
                package.BehaviourId,
                package.Version);
        }

        var bySlot = readiness.Bindings.ToDictionary(item => item.SlotId, StringComparer.Ordinal);
        var slots = package.GeometrySlots
            .Select(requirement =>
            {
                bySlot.TryGetValue(requirement.SlotId, out var binding);
                candidatesBySlot.TryGetValue(requirement.SlotId, out var candidates);
                return BehaviourBindingSlotAssessment.Create(
                    requirement,
                    binding,
                    candidates ?? []);
            })
            .ToArray();
        var summary = readiness.Ready
            ? $"All required geometry bindings are ready for {package.Label}."
            : string.Join(" ", readiness.Blockers);
        if (candidateWarnings.Count > 0)
        {
            summary = $"{summary} {string.Join(" ", candidateWarnings)}".Trim();
        }

        return Store(new BehaviourBindingWorkspaceSnapshot(
            normalizedConnection,
            package,
            true,
            candidateInventoryAvailable,
            summary,
            readiness,
            slots,
            candidateWarnings,
            DateTimeOffset.UtcNow));
    }

    public Task<BehaviourBindingMutationResult> SetAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default)
        => MutateAsync(request, clear: false, cancellationToken);

    public Task<BehaviourBindingMutationResult> ClearAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default)
        => MutateAsync(request, clear: true, cancellationToken);

    private async Task<BehaviourBindingMutationResult> MutateAsync(
        BehaviourGeometryBindingCommandRequest request,
        bool clear,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = new BehaviourPackageIdentity(request.BehaviourId, request.Version);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var before = await InspectAsync(
                request.ConnectionId,
                identity,
                refreshGeometry: true,
                cancellationToken: cancellationToken);
            if (!before.Available)
            {
                return new BehaviourBindingMutationResult(
                    false,
                    false,
                    before.Summary,
                    before);
            }

            var slot = before.Slots.FirstOrDefault(item =>
                string.Equals(item.SlotId, request.SlotId, StringComparison.Ordinal));
            if (slot is null)
            {
                return new BehaviourBindingMutationResult(
                    false,
                    false,
                    $"Behaviour '{identity}' does not declare geometry slot '{request.SlotId}'.",
                    before);
            }

            if (!clear)
            {
                if (string.IsNullOrWhiteSpace(request.GeometryId))
                {
                    return new BehaviourBindingMutationResult(
                        false,
                        false,
                        "A geometry ID is required to set a behaviour binding.",
                        before);
                }

                var candidate = slot.Candidates.FirstOrDefault(item =>
                    string.Equals(item.GeometryId, request.GeometryId, StringComparison.Ordinal));
                if (candidate is null)
                {
                    return new BehaviourBindingMutationResult(
                        false,
                        false,
                        $"Geometry '{request.GeometryId}' is not present in the selected Logos registry.",
                        before);
                }

                if (!candidate.Compatible)
                {
                    return new BehaviourBindingMutationResult(
                        false,
                        false,
                        candidate.Summary,
                        before,
                        candidate.Issues);
                }
            }

            BehaviourGeometryBindingCommandResult command;
            try
            {
                command = clear
                    ? await _bindings.DeleteAsync(request, cancellationToken)
                    : await _bindings.SetAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not {Operation} behaviour geometry binding {BehaviourId}/{SlotId}",
                    clear ? "clear" : "set",
                    request.BehaviourId,
                    request.SlotId);
                return new BehaviourBindingMutationResult(
                    false,
                    false,
                    $"Could not {(clear ? "clear" : "set")} the behaviour geometry binding: {ex.Message}",
                    before);
            }

            if (!command.Accepted)
            {
                return new BehaviourBindingMutationResult(
                    false,
                    false,
                    command.Message,
                    before,
                    command.Issues);
            }

            var verification = await VerifyMutationAsync(request, clear, cancellationToken);
            var after = await InspectAsync(
                request.ConnectionId,
                identity,
                refreshGeometry: true,
                cancellationToken: cancellationToken);
            var message = verification.Verified
                ? command.Message
                : $"{command.Message} {verification.Message}".Trim();
            return new BehaviourBindingMutationResult(
                true,
                verification.Verified,
                message,
                after,
                (command.Issues ?? [])
                    .Concat(verification.Issues)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<(bool Verified, string Message, IReadOnlyList<string> Issues)> VerifyMutationAsync(
        BehaviourGeometryBindingCommandRequest request,
        bool clear,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _bindings.ListAsync(
                request.ConnectionId,
                request.BehaviourId,
                request.Version,
                checkObjectExists: true,
                cancellationToken);
            var binding = current.FirstOrDefault(item =>
                string.Equals(item.SlotId, request.SlotId, StringComparison.Ordinal));
            if (clear)
            {
                if (binding is null ||
                    !binding.Bound ||
                    string.IsNullOrWhiteSpace(binding.GeometryId))
                {
                    return (true, "Logos confirmed that the explicit binding is cleared.", []);
                }

                return (false,
                    $"Logos still reports slot '{request.SlotId}' bound to '{binding.GeometryId}'.",
                    binding.Issues);
            }

            var verified = binding is
            {
                Bound: true,
                ObjectExists: true,
                Issues: { Count: 0 }
            } && string.Equals(binding.GeometryId, request.GeometryId, StringComparison.Ordinal);
            return verified
                ? (true, "Logos confirmed the requested binding.", [])
                : (false,
                    "Logos accepted the command but the refreshed binding does not match the requested geometry.",
                    binding?.Issues ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (
                false,
                $"The command was accepted, but Robot Command could not verify the refreshed binding: {ex.Message}",
                []);
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<BehaviourGeometryCandidate>>> BuildCandidatesAsync(
        string connectionId,
        IReadOnlyList<BehaviourGeometryRequirement> requirements,
        bool refreshGeometry,
        CancellationToken cancellationToken)
    {
        var records = await _geometry.ListAsync(
            connectionId,
            new GeometryQuery(IncludeObjects: false, Refresh: refreshGeometry),
            cancellationToken);
        var objectCache = new Dictionary<string, RemoteGeometryObject?>(StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<BehaviourGeometryCandidate>>(StringComparer.Ordinal);

        foreach (var requirement in requirements)
        {
            var candidates = new List<BehaviourGeometryCandidate>(records.Count);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var issues = new List<string>();
                var kindCompatible = BehaviourGeometryCompatibility.KindMatches(
                    requirement.KindHint,
                    record.Kind);
                var policyKind = string.Empty;
                var policyConstraint = string.Empty;
                var policyCompatible = true;

                if (kindCompatible && RequiresPolicyInspection(requirement.ExpectedPolicyKind))
                {
                    if (!objectCache.TryGetValue(record.GeometryId, out var geometry))
                    {
                        geometry = await _geometry.GetAsync(
                            connectionId,
                            record.GeometryId,
                            refreshGeometry,
                            cancellationToken);
                        objectCache[record.GeometryId] = geometry;
                    }

                    if (geometry is null)
                    {
                        policyCompatible = false;
                        issues.Add($"Geometry '{record.GeometryId}' could not be loaded for policy inspection.");
                    }
                    else
                    {
                        policyKind = geometry.Document.Policy.Kind;
                        policyConstraint = geometry.Document.Policy.Constraint;
                        policyCompatible = BehaviourGeometryCompatibility.PolicyMatches(
                            requirement.ExpectedPolicyKind,
                            geometry.Document.Policy);
                        if (!policyCompatible)
                        {
                            issues.Add(
                                $"Slot '{requirement.SlotId}' expects policy kind '{requirement.ExpectedPolicyKind}', " +
                                $"but geometry '{record.GeometryId}' is '{policyKind}/{policyConstraint}'.");
                        }
                    }
                }

                if (!kindCompatible)
                {
                    issues.Add(
                        $"Slot '{requirement.SlotId}' expects {BehaviourGeometryCompatibility.ExpectedKindLabel(requirement.KindHint)}, " +
                        $"but geometry '{record.GeometryId}' is {record.Kind}.");
                }

                var compatible = kindCompatible && policyCompatible;
                candidates.Add(new BehaviourGeometryCandidate(
                    record.GeometryId,
                    string.IsNullOrWhiteSpace(record.DisplayName)
                        ? record.GeometryId
                        : record.DisplayName,
                    record.Kind,
                    record.Frame,
                    policyKind,
                    policyConstraint,
                    compatible,
                    compatible
                        ? $"Geometry '{record.GeometryId}' is compatible with slot '{requirement.SlotId}'."
                        : string.Join(" ", issues),
                    issues));
            }

            result[requirement.SlotId] = candidates
                .OrderByDescending(item => item.Compatible)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
                .ToArray();
        }

        return result;
    }

    private BehaviourBindingWorkspaceSnapshot Store(BehaviourBindingWorkspaceSnapshot snapshot)
    {
        lock (_stateGate)
        {
            _snapshots[snapshot.Key] = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    private static BehaviourBindingPackageOption MissingPackage(BehaviourPackageIdentity identity)
        => new(
            identity,
            identity.BehaviourId,
            string.Empty,
            "Not installed",
            string.Empty,
            [],
            [],
            []);

    private static bool RequiresPolicyInspection(string? expectedPolicyKind)
        => !BehaviourGeometryCompatibility.PolicyMatches(
            expectedPolicyKind,
            GeometryPolicyAnnotation.None);

    private static string SnapshotKey(string connectionId, BehaviourPackageIdentity identity)
        => $"{connectionId}:{identity.Key}";

    private static string Normalize(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"A {field} is required.");
        }

        return value.Trim();
    }
}
