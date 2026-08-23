using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;

namespace RobotCommand.Services.Workflows;

public sealed class BehaviourWorkflow : IBehaviourWorkflow, IDisposable
{
    private readonly IBehaviourPackageStore _store;
    private readonly IBehaviourWorkspaceService _workspace;
    private readonly IBehaviourPackageDeploymentService _deployment;
    private readonly IBehaviourBindingWorkspaceService _bindings;
    private readonly ReviewedOperationWorkflow _reviewed;
    public BehaviourWorkflow(IBehaviourPackageStore store, IBehaviourWorkspaceService workspace, IBehaviourPackageDeploymentService deployment, IBehaviourBindingWorkspaceService bindings, ReviewedOperationWorkflow reviewed)
    {
        _store = store; _workspace = workspace; _deployment = deployment; _bindings = bindings; _reviewed = reviewed;
        _store.Changed += OnChanged; _workspace.Changed += OnChanged; _deployment.Changed += OnChanged; _bindings.Changed += OnChanged;
    }
    public event EventHandler? Changed;
    public IReadOnlyList<BehaviourWorkflowSnapshot> LocalPackages => _store.Packages.Select(Map).ToArray();
    public IReadOnlyList<string> LibraryIssues => _store.Issues.Select(item => item.Message).ToArray();
    public Task RefreshAsync(string connectionId, bool refreshLocal = false, bool refreshRemote = false, CancellationToken cancellationToken = default) => _workspace.RefreshAsync(connectionId, refreshLocal: refreshLocal, refreshRemote: refreshRemote, cancellationToken: cancellationToken);
    public async Task<BehaviourWorkflowSnapshot> ImportAsync(string path, CancellationToken cancellationToken = default)
    { var result = await _store.ImportDirectoryAsync(path, cancellationToken: cancellationToken); OnChanged(this, EventArgs.Empty); return Map(result.Package); }
    public Task ExportAsync(string packageId, string destinationPath, CancellationToken cancellationToken = default) => _store.ExportDirectoryAsync(ParseIdentity(packageId), destinationPath, cancellationToken: cancellationToken);
    public Task RemoveLocalAsync(string packageId, CancellationToken cancellationToken = default) => _store.RemoveAsync(ParseIdentity(packageId), cancellationToken);
    public Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGet(ParseIdentity(packageId), out var package) || package is null) throw new KeyNotFoundException("Behaviour package was not found.");
        var validation = package.Integrity;
        return Task.FromResult<IReadOnlyList<WorkflowFinding>>(validation.Findings.Select(item => new WorkflowFinding(item.Code, item.Severity == BehaviourPackageFindingSeverity.Error ? WorkflowFindingSeverity.Blocking : item.Severity == BehaviourPackageFindingSeverity.Warning ? WorkflowFindingSeverity.Warning : WorkflowFindingSeverity.Info, item.Message)).ToArray());
    }
    public async Task<ReviewedOperationSnapshot> PlanDeployAsync(string connectionId, string packageId, string operation = "install", CancellationToken cancellationToken = default)
    {
        var kind = string.Equals(operation, "update", StringComparison.OrdinalIgnoreCase) ? BehaviourPackageOperationKind.Update : BehaviourPackageOperationKind.Install;
        var assessment = await _deployment.AssessAsync(kind, connectionId, ParseIdentity(packageId), cancellationToken);
        var findings = assessment.Blockers.Select(item => new WorkflowFinding("BEHAVIOUR_BLOCKED", WorkflowFindingSeverity.Blocking, item))
            .Concat(assessment.Warnings.Select(item => new WorkflowFinding("BEHAVIOUR_WARNING", WorkflowFindingSeverity.Warning, item))).ToArray();
        return _reviewed.Plan(ReviewedOperationKind.BehaviourDeploy, $"{kind} behaviour {packageId}", findings, [], assessment.Summary, [connectionId, packageId], async token =>
        { var request = assessment.CreateRequest(); var result = kind == BehaviourPackageOperationKind.Update ? await _deployment.UpdateAsync(request, token) : await _deployment.InstallAsync(request, token); return new("", result.Accepted ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Accepted, result.Message, result.Warnings); });
    }
    public async Task<ReviewedOperationSnapshot> PlanRemoveAsync(string connectionId, string packageId, CancellationToken cancellationToken = default)
    {
        var assessment = await _deployment.AssessAsync(BehaviourPackageOperationKind.Remove, connectionId, ParseIdentity(packageId), cancellationToken);
        var findings = assessment.Blockers.Select(item => new WorkflowFinding("BEHAVIOUR_BLOCKED", WorkflowFindingSeverity.Blocking, item)).Concat(assessment.Warnings.Select(item => new WorkflowFinding("BEHAVIOUR_WARNING", WorkflowFindingSeverity.Warning, item))).ToArray();
        return _reviewed.Plan(ReviewedOperationKind.BehaviourRemove, $"Remove behaviour {packageId}", findings, [], assessment.Summary, [connectionId, packageId], async token =>
        { var result = await _deployment.RemoveAsync(assessment.CreateRequest(), token); return new("", result.Accepted ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Accepted, result.Message, result.Warnings); });
    }
    public async Task<BehaviourBindingWorkflowSnapshot> InspectBindingsAsync(string connectionId, string packageId, CancellationToken cancellationToken = default)
    {
        var result = await _bindings.InspectAsync(connectionId, ParseIdentity(packageId), cancellationToken: cancellationToken);
        return Map(result);
    }
    public Task<ReviewedOperationSnapshot> PlanSetBindingAsync(string connectionId, string packageId, string bindingName, string geometryId, CancellationToken cancellationToken = default)
        => Task.FromResult(PlanBinding(ReviewedOperationKind.BehaviourBindingSet, $"Bind {bindingName} to {geometryId}", new(connectionId, ParseIdentity(packageId).BehaviourId, ParseIdentity(packageId).Version ?? "", bindingName, geometryId), false));
    public Task<ReviewedOperationSnapshot> PlanClearBindingAsync(string connectionId, string packageId, string bindingName, CancellationToken cancellationToken = default)
        => Task.FromResult(PlanBinding(ReviewedOperationKind.BehaviourBindingClear, $"Clear binding {bindingName}", new(connectionId, ParseIdentity(packageId).BehaviourId, ParseIdentity(packageId).Version ?? "", bindingName), true));
    private ReviewedOperationSnapshot PlanBinding(ReviewedOperationKind kind, string title, BehaviourGeometryBindingCommandRequest request, bool clear) => _reviewed.Plan(kind, title, [], [], "Ready to execute binding update.", [request.ConnectionId, request.BehaviourId, request.SlotId], async token => { var result = clear ? await _bindings.ClearAsync(request, token) : await _bindings.SetAsync(request, token); return new("", result.Accepted ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Accepted, result.Message, result.Issues ?? []); });
    private static BehaviourWorkflowSnapshot Map(LocalBehaviourPackageRecord item) => new(item.Identity.Key, item.DisplayName, item.Identity.Version ?? "", item.State.ToString(), item.Integrity.Summary, Findings: item.Integrity.Findings.Select(f => new WorkflowFinding(f.Code, f.Severity == BehaviourPackageFindingSeverity.Error ? WorkflowFindingSeverity.Blocking : f.Severity == BehaviourPackageFindingSeverity.Warning ? WorkflowFindingSeverity.Warning : WorkflowFindingSeverity.Info, f.Message)).ToArray());
    private static BehaviourBindingWorkflowSnapshot Map(BehaviourBindingWorkspaceSnapshot item) => new(item.ConnectionId, item.Identity.BehaviourId, item.Identity.Version ?? "", item.Readiness.Bindings.Where(binding => binding.Bound && binding.GeometryId is not null).ToDictionary(binding => binding.SlotId, binding => binding.GeometryId!, StringComparer.Ordinal), item.Summary);
    private static BehaviourPackageIdentity ParseIdentity(string value) { var parts = value.Split('@', 2); return new(parts[0], parts.Length > 1 ? parts[1] : null); }
    private void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { _store.Changed -= OnChanged; _workspace.Changed -= OnChanged; _deployment.Changed -= OnChanged; _bindings.Changed -= OnChanged; }
}
