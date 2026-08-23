using System.Collections.Specialized;
using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Autonomy;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Missions;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

public sealed class AutonomyWorkflow : IAutonomyWorkflow, IDisposable
{
    private readonly IMissionTaskWorkspaceService _workspace;
    private readonly IAutonomyDeploymentBundleService _bundles;
    private readonly IEntityStore<string, MissionRecord> _missions;
    private readonly IEntityStore<string, OperationalTaskRecord> _tasks;
    private readonly ReviewedOperationWorkflow _reviewed;

    public AutonomyWorkflow(IMissionTaskWorkspaceService workspace, IAutonomyDeploymentBundleService bundles,
        IEntityStore<string, MissionRecord> missions, IEntityStore<string, OperationalTaskRecord> tasks, ReviewedOperationWorkflow reviewed)
    {
        _workspace = workspace; _bundles = bundles; _missions = missions; _tasks = tasks; _reviewed = reviewed;
        ((INotifyCollectionChanged)_missions.Items).CollectionChanged += StoreChanged;
        ((INotifyCollectionChanged)_tasks.Items).CollectionChanged += StoreChanged;
    }
    public event EventHandler? Changed;
    public AutonomyWorkflowSnapshot Current => new(new(_workspace.GatewayAvailable, _workspace.GatewayStatus),
        _missions.Items.Select(Map).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        _tasks.Items.Select(Map).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    public async Task RefreshAsync(string? connectionId = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(connectionId)) await _workspace.RefreshRemoteAsync(connectionId, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task<WorkflowDocumentSnapshot> ImportMissionAsync(string path, CancellationToken cancellationToken = default) { var item = await _workspace.ImportMissionAsync(path, cancellationToken); Changed?.Invoke(this, EventArgs.Empty); return Map(item); }
    public async Task<WorkflowDocumentSnapshot> ImportTaskAsync(string path, CancellationToken cancellationToken = default) { var item = await _workspace.ImportTaskAsync(path, cancellationToken); Changed?.Invoke(this, EventArgs.Empty); return Map(item); }
    public Task ExportMissionAsync(string missionId, string path, CancellationToken cancellationToken = default) => _workspace.ExportMissionAsync(missionId, path, cancellationToken);
    public Task ExportTaskAsync(string taskId, string path, CancellationToken cancellationToken = default) => _workspace.ExportTaskAsync(taskId, path, cancellationToken);
    public async Task<IReadOnlyList<WorkflowFinding>> ValidateMissionAsync(string missionId, CancellationToken cancellationToken = default) => Map(await _workspace.ValidateMissionAsync(missionId, cancellationToken));
    public async Task<IReadOnlyList<WorkflowFinding>> ValidateTaskAsync(string taskId, CancellationToken cancellationToken = default) => Map(await _workspace.ValidateTaskAsync(taskId, cancellationToken));
    public async Task<ReviewedOperationSnapshot> PlanMissionPublishAsync(string missionId, CancellationToken cancellationToken = default)
    {
        var findings = await ValidateMissionAsync(missionId, cancellationToken);
        return Plan(ReviewedOperationKind.MissionPublish, $"Publish mission {missionId}", findings, [missionId], token => _workspace.PublishMissionAsync(missionId, cancellationToken: token));
    }
    public async Task<ReviewedOperationSnapshot> PlanTaskPublishAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var findings = await ValidateTaskAsync(taskId, cancellationToken);
        return Plan(ReviewedOperationKind.TaskPublish, $"Publish task {taskId}", findings, [taskId], token => _workspace.PublishTaskAsync(taskId, cancellationToken: token));
    }
    public async Task<ReviewedOperationSnapshot> PlanTaskAssignmentAsync(TaskAssignmentWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        var findings = Map(await _workspace.ValidateTaskForVehicleAsync(request.TaskId, request.VehicleId, cancellationToken));
        return Plan(ReviewedOperationKind.TaskAssign, $"Assign task {request.TaskId}", findings, [request.TaskId, request.VehicleId], token => _workspace.AssignTaskAsync(request.TaskId, request.VehicleId, request.ValidateOnAssign, token));
    }
    public Task<ReviewedOperationSnapshot> PlanMissionCommandAsync(MissionCommandWorkflowRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Plan(ReviewedOperationKind.MissionCommand, $"{request.Command} mission {request.MissionId}", [], [request.MissionId], token => _workspace.ExecuteMissionCommandAsync(new(request.ConnectionId, request.MissionId, request.Command, Reason: request.Reason ?? "", Emergency: request.Emergency), token)));
    public Task<ReviewedOperationSnapshot> PlanTaskCommandAsync(TaskCommandWorkflowRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Plan(ReviewedOperationKind.TaskCommand, $"{request.Command} task {request.TaskId}", [], [request.TaskId], token => _workspace.ExecuteTaskCommandAsync(new(request.ConnectionId, request.TaskId, request.Command, Reason: request.Reason ?? "", Emergency: request.Emergency), token)));
    public async Task<WorkflowBundleSnapshot> ExportBundleAsync(string destinationPath, IReadOnlyList<string> missionIds, IReadOnlyList<string> taskIds, IReadOnlyList<string> behaviourIds, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default)
    {
        var manifest = await _bundles.ExportAsync(new(destinationPath, Path.GetFileNameWithoutExtension(destinationPath), "Robot Command autonomy bundle", behaviourIds.Select(ParseIdentity).ToArray(), geometryIds, missionIds, taskIds, [], []), cancellationToken);
        return new(manifest.DisplayName, destinationPath, $"Exported {manifest.AssetCount} asset(s).", manifest.CreatedAt);
    }
    public async Task<WorkflowBundleSnapshot> InspectBundleAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var result = await _bundles.InspectAsync(archivePath, cancellationToken);
        return new(result.Manifest?.DisplayName ?? Path.GetFileNameWithoutExtension(archivePath), archivePath, result.Summary, result.Manifest?.CreatedAt);
    }
    public async Task<ReviewedOperationSnapshot> PlanBundleImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var inspection = await _bundles.InspectAsync(archivePath, cancellationToken);
        var findings = inspection.Valid ? [] : inspection.Issues.Select(issue => new WorkflowFinding("BUNDLE_INVALID", WorkflowFindingSeverity.Blocking, issue)).ToArray();
        return _reviewed.Plan(ReviewedOperationKind.AutonomyBundleImport, $"Import autonomy bundle {Path.GetFileName(archivePath)}", findings, inspection.Plan.Select(step => step.Summary).ToArray(), inspection.Summary, [archivePath], async token =>
        { var result = await _bundles.ImportAsync(archivePath, new(), token); Changed?.Invoke(this, EventArgs.Empty); return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, result.Summary, result.Issues); });
    }
    private ReviewedOperationSnapshot Plan(ReviewedOperationKind kind, string title, IReadOnlyList<WorkflowFinding> findings, IReadOnlyList<string> targets, Func<CancellationToken, Task<GatewayCommandResult>> execute) => _reviewed.Plan(kind, title, findings, [], findings.Any(f => f.Severity == WorkflowFindingSeverity.Blocking) ? findings.First(f => f.Severity == WorkflowFindingSeverity.Blocking).Message : "Ready to execute.", targets, async token => { var result = await execute(token); Changed?.Invoke(this, EventArgs.Empty); return new("", result.Accepted ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Accepted, result.Message, []); });
    private static IReadOnlyList<WorkflowFinding> Map(DocumentValidationResult result) => result.Issues.Select(issue => new WorkflowFinding("VALIDATION", result.IsValid ? WorkflowFindingSeverity.Warning : WorkflowFindingSeverity.Blocking, issue)).DefaultIfEmpty(new("VALIDATION", result.IsValid ? WorkflowFindingSeverity.Info : WorkflowFindingSeverity.Blocking, result.Summary)).ToArray();
    private static WorkflowDocumentSnapshot Map(MissionRecord item) => new(item.Id, item.Name, item.ConnectionId ?? "", item.IsLocalDraft, item.ValidationState.ToString(), item.ValidationSummary, item.Objective, item.GeometryIds ?? [], item.ObservedAt);
    private static WorkflowDocumentSnapshot Map(OperationalTaskRecord item) => new(item.Id, item.Name, item.ConnectionId ?? "", item.IsLocalDraft, item.ValidationState.ToString(), item.ValidationSummary, item.Objective, item.GeometryIds ?? [], item.ObservedAt);
    private static BehaviourPackageIdentity ParseIdentity(string value) { var pieces = value.Split('@', 2); return new(pieces[0], pieces.Length == 2 ? pieces[1] : null); }
    private void StoreChanged(object? sender, NotifyCollectionChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        ((INotifyCollectionChanged)_missions.Items).CollectionChanged -= StoreChanged;
        ((INotifyCollectionChanged)_tasks.Items).CollectionChanged -= StoreChanged;
    }
}

public sealed class GeometryWorkflow : IGeometryWorkflow, IDisposable
{
    private readonly IGeometryWorkspaceService _workspace;
    private readonly IGeometryGroupStore _groups;
    private readonly GeometryDocumentCodec _codec;
    private readonly ReviewedOperationWorkflow _reviewed;
    public GeometryWorkflow(IGeometryWorkspaceService workspace, IGeometryGroupStore groups, GeometryDocumentCodec codec, ReviewedOperationWorkflow reviewed) { _workspace = workspace; _groups = groups; _codec = codec; _reviewed = reviewed; _workspace.Changed += OnChanged; _groups.Changed += OnChanged; }
    public event EventHandler? Changed;
    public WorkflowGatewaySnapshot Gateway => new(_workspace.GatewayAvailable, _workspace.GatewayStatus);
    public IReadOnlyList<GeometryWorkflowSnapshot> LocalDocuments => _workspace.LocalDocuments.Select(Map).ToArray();
    public IReadOnlyList<GeometryRemoteWorkflowSnapshot> RemoteDocuments => _workspace.RemoteRecords.Select(item => new GeometryRemoteWorkflowSnapshot(item.ConnectionId, item.GeometryId, item.DisplayName, item.Kind.ToString(), "Remote", item.Revision ?? "Current")).ToArray();
    public IReadOnlyList<GeometryGroupWorkflowSnapshot> Groups => _groups.Groups.Select(pair => new GeometryGroupWorkflowSnapshot(pair.Key, pair.Value.Count)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> LibraryIssues => _workspace.LibraryIssues.Select(item => item.Message).ToArray();
    public Task RefreshAsync(string? connectionId = null, CancellationToken cancellationToken = default) => _workspace.RefreshAsync(connectionId, cancellationToken);
    public async Task<GeometryWorkflowSnapshot> CreateAsync(GeometryCreateWorkflowRequest request, CancellationToken cancellationToken = default) => Map(await _workspace.CreateLocalDraftAsync(ParseKind(request.Kind), request.Id, request.Name, cancellationToken));
    public async Task<GeometryWorkflowSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default) => Map(await _workspace.ImportLocalAsync(path, replace, cancellationToken));
    public Task ImportSetAsync(string path, bool replace = false, CancellationToken cancellationToken = default) => _groups.ImportSetAsync(path, replace, cancellationToken);
    public Task ExportAsync(string geometryId, string path, CancellationToken cancellationToken = default) => _workspace.ExportLocalAsync(geometryId, path, cancellationToken);
    public Task ExportSetAsync(GeometrySetExportWorkflowRequest request, CancellationToken cancellationToken = default) => _groups.ExportSetAsync(request.Path, request.Name, request.GeometryIds, cancellationToken);
    public async Task<GeometryWorkflowSnapshot> SaveAsync(GeometrySaveWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentJson);
        var document = _codec.Deserialize(request.DocumentJson);
        var saved = await _workspace.SaveLocalAsync(document, cancellationToken);
        return Map(saved);
    }
    public async Task<GeometryWorkflowSnapshot> DuplicateAsync(string geometryId, string newGeometryId, CancellationToken cancellationToken = default) => Map(await _workspace.DuplicateLocalAsync(geometryId, newGeometryId, cancellationToken));
    public Task RemoveAsync(string geometryId, CancellationToken cancellationToken = default) => _workspace.RemoveLocalAsync(geometryId, cancellationToken);
    public Task CreateGroupAsync(string name, CancellationToken cancellationToken = default) => _groups.CreateAsync(name, cancellationToken);
    public Task RenameGroupAsync(string name, string replacement, CancellationToken cancellationToken = default) => _groups.RenameAsync(name, replacement, cancellationToken);
    public Task DeleteGroupAsync(string name, CancellationToken cancellationToken = default) => _groups.DeleteAsync(name, cancellationToken);
    public Task AssignGroupAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default) => _groups.AssignAsync(name, geometryIds, cancellationToken);
    public Task RemoveFromGroupAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default) => _groups.RemoveAsync(name, geometryIds, cancellationToken);
    public IReadOnlyList<string> GetGroups(string geometryId) => _groups.GetGroups(geometryId);
    public async Task<GeometryWorkflowSnapshot> PullAsync(string connectionId, string geometryId, bool replace = false, CancellationToken cancellationToken = default) => Map(await _workspace.PullAsync(connectionId, geometryId, replace, cancellationToken));
    public async Task<ReviewedOperationSnapshot> PlanRemoteAsync(string action, string connectionId, string geometryId, CancellationToken cancellationToken = default)
    {
        var operation = action.ToLowerInvariant() switch { "create" => GeometryRemoteOperationKind.Create, "update" => GeometryRemoteOperationKind.Update, "delete" => GeometryRemoteOperationKind.Delete, _ => throw new ArgumentException("Geometry action must be create, update, or delete.") };
        var assessment = await _workspace.AssessRemoteOperationAsync(connectionId, geometryId, operation, cancellationToken);
        var findings = assessment.Findings.Select(item => new WorkflowFinding(item.Code, item.Severity == GeometryValidationSeverity.Error ? WorkflowFindingSeverity.Blocking : item.Severity == GeometryValidationSeverity.Warning ? WorkflowFindingSeverity.Warning : WorkflowFindingSeverity.Info, item.Message)).ToArray();
        var kind = operation switch { GeometryRemoteOperationKind.Create => ReviewedOperationKind.GeometryRemoteCreate, GeometryRemoteOperationKind.Update => ReviewedOperationKind.GeometryRemoteUpdate, _ => ReviewedOperationKind.GeometryRemoteDelete };
        return _reviewed.Plan(kind, $"{action} remote geometry {geometryId}", findings, [], assessment.Summary, [connectionId, geometryId], async token =>
        { var result = operation switch { GeometryRemoteOperationKind.Create => await _workspace.CreateRemoteAsync(connectionId, geometryId, token), GeometryRemoteOperationKind.Update => await _workspace.UpdateRemoteAsync(connectionId, geometryId, token), _ => await _workspace.DeleteRemoteAsync(connectionId, geometryId, token) }; return new("", result.Accepted ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Accepted, result.Message, []); });
    }
    private static GeometryWorkflowSnapshot Map(GeometryDocument item) => new(item.GeometryId, item.DisplayName, item.Kind.ToString(), item.Origin.ToString(), item.IsDirty ? "Draft" : "Valid", item.IsDirty ? "Local changes not deployed." : "Saved.", item.SourceConnectionId, item.SourceRevision);
    private static GeometryDocumentKind ParseKind(string value) => value.Trim().ToLowerInvariant() switch { "poi" or "point" or "pointofinterest" => GeometryDocumentKind.PointOfInterest, "route" or "waypointsequence" => GeometryDocumentKind.WaypointSequence, "zone" => GeometryDocumentKind.Zone, _ => throw new ArgumentException("Geometry kind must be poi, route, or zone.") };
    private void OnChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { _workspace.Changed -= OnChanged; _groups.Changed -= OnChanged; }
}
