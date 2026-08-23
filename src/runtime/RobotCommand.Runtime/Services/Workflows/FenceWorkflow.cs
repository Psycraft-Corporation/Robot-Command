using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Simulation;

namespace RobotCommand.Services.Workflows;

/// <summary>
/// Shared fence workflow. It owns the target-neutral library and dispatches
/// the reviewed operation to Ghost-local or MAVLink-backed execution.
/// </summary>
public sealed class FenceWorkflow : IFenceWorkflow, IPx4GeofenceWorkflow
{
    private const byte GlobalFrame = 5;
    private const ushort FencePolygonInclusion = 5001;
    private const ushort FencePolygonExclusion = 5002;
    private readonly FenceLibraryStore _store;
    private readonly IGeometryDocumentStore _geometry;
    private readonly IUnitObservationWorkflow _units;
    private readonly IMavlinkConnectionRegistry _connections;
    private readonly IGhostUnitService _ghosts;
    private readonly ReviewedOperationWorkflow _reviewed;

    public FenceWorkflow(
        FenceLibraryStore store,
        IGeometryDocumentStore geometry,
        IUnitObservationWorkflow units,
        IMavlinkConnectionRegistry connections,
        IGhostUnitService ghosts,
        ReviewedOperationWorkflow reviewed)
    {
        _store = store;
        _geometry = geometry;
        _units = units;
        _connections = connections;
        _ghosts = ghosts;
        _reviewed = reviewed;
        _geometry.Changed += OnChanged;
        _store.Changed += OnChanged;
        _units.Changed += OnChanged;
    }

    public event EventHandler? Changed;
    public IReadOnlyList<FenceSnapshot> Fences => _store.Fences.Select(item => new FenceSnapshot(item, ValidateDocument(item, null))).ToArray();

    public IReadOnlyList<FenceTargetSnapshot> Targets => _units.Units
        .Select(target =>
        {
            var backend = Backend(target);
            var findings = TargetFindings(target.Id);
            return new FenceTargetSnapshot(
                target.Id, target.Name, backend, target.VehicleClass,
                target.State == ManagedConnectionState.Online,
                target.Telemetry is { IsStale: false, LatitudeDegrees: not null, LongitudeDegrees: not null },
                target.IsGhost,
                target.IsGhost || backend is "PX4" or "ArduPilot",
                findings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking) ? "Unavailable" : "Ready",
                findings);
        }).ToArray();

    public async Task<FenceSnapshot> CreateFromZoneAsync(string geometryId, string? name = null, FenceKind kind = FenceKind.Inclusion, CancellationToken cancellationToken = default)
    {
        if (!_geometry.TryGet(geometryId, out var geometry) || geometry is null || geometry.Kind != GeometryDocumentKind.Zone)
            throw new InvalidOperationException("Select a saved zone before creating a fence.");
        var points = ((geometry.Rings.Count > 0 ? geometry.Rings[0].Points : null) ?? geometry.Points)
            .Select(point => new FlightMissionCoordinate(point.LatitudeDegrees, point.LongitudeDegrees)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var document = await _store.SaveAsync(new(
            FenceDocument.CurrentSchemaVersion, $"fence-{Guid.NewGuid():N}", name?.Trim() ?? geometry.DisplayName,
            kind, points, geometry.GeometryId, geometry.DisplayName, geometry.ContentSha256,
            null, null, now, now), false, cancellationToken).ConfigureAwait(false);
        return new FenceSnapshot(document, ValidateDocument(document, null));
    }

    public Task DeleteAsync(string fenceId, CancellationToken cancellationToken = default) => _store.RemoveAsync(fenceId, cancellationToken);
    public async Task<FenceSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default)
    {
        var document = await _store.ImportAsync(path, replace, cancellationToken).ConfigureAwait(false);
        return new FenceSnapshot(document, ValidateDocument(document, null));
    }
    public Task ExportAsync(string fenceId, string path, CancellationToken cancellationToken = default) => _store.ExportAsync(fenceId, path, cancellationToken);
    public Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string fenceId, string? vehicleId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkflowFinding>>(ValidateDocument(Require(fenceId), Target(vehicleId)));

    public Task<ReviewedOperationSnapshot> PlanUploadAsync(string fenceId, string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var fence = Require(fenceId);
        var target = Target(vehicleId);
        var findings = ValidateDocument(fence, target);
        var backend = target is null ? "target" : Backend(target);
        var resolvedConnection = ResolveMavlinkConnectionId(connectionId, vehicleId);
        findings = AddConnectionFinding(findings, target, resolvedConnection);
        var operationTargets = target?.IsGhost == true ? new[] { vehicleId } : new[] { resolvedConnection, vehicleId };
        return Task.FromResult(_reviewed.Plan(
            ReviewedOperationKind.FenceUpload,
            $"Upload {backend} fence",
            findings,
            operationTargets,
            "Upload replaces the target's current polygon fence set.",
            operationTargets,
            async token =>
            {
                var current = Require(fenceId);
                var currentTarget = Target(vehicleId);
                var currentFindings = ValidateDocument(current, currentTarget);
                if (currentFindings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking))
                    return new("", ReviewedOperationState.Failed, false, "Fence upload is no longer available.", currentFindings.Select(item => item.Message).ToArray());
                if (currentTarget?.IsGhost == true)
                {
                    await _ghosts.ApplyFenceAsync(vehicleId, current, token).ConfigureAwait(false);
                    return new("", ReviewedOperationState.Succeeded, true, "Ghost fence applied locally.", [vehicleId]);
                }
                var result = await Client(resolvedConnection).UploadFenceAsync(CommandVehicle(vehicleId), Compile(current), token).ConfigureAwait(false);
                return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded,
                    $"{backend} fence upload: {result.Summary}", []);
            }));
    }

    public Task<ReviewedOperationSnapshot> PlanDownloadAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var target = Target(vehicleId);
        var backend = target is null ? "target" : Backend(target);
        var findings = TargetFindings(vehicleId);
        var resolvedConnection = ResolveMavlinkConnectionId(connectionId, vehicleId);
        findings = AddConnectionFinding(findings, target, resolvedConnection);
        var operationTargets = target?.IsGhost == true ? new[] { vehicleId } : new[] { resolvedConnection, vehicleId };
        return Task.FromResult(_reviewed.Plan(
            ReviewedOperationKind.FenceDownload, $"Download {backend} fences", findings, operationTargets,
            "Read the target's current polygon fence set.", operationTargets,
            async token =>
            {
                var currentFindings = TargetFindings(vehicleId);
                if (currentFindings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking))
                    return new("", ReviewedOperationState.Failed, false, "Fence download is no longer available.", currentFindings.Select(item => item.Message).ToArray());
                if (target?.IsGhost == true)
                {
                    var active = await _ghosts.DownloadFenceAsync(vehicleId, token).ConfigureAwait(false);
                    return new("", ReviewedOperationState.Succeeded, true,
                        active is null ? "Ghost has no locally applied fence." : $"Ghost fence available locally: {active.DisplayName}.", active is null ? [] : [active.FenceId]);
                }
                var result = await Client(resolvedConnection).DownloadFenceAsync(CommandVehicle(vehicleId), token).ConfigureAwait(false);
                if (!result.Succeeded) return new("", ReviewedOperationState.Failed, false, $"{backend} fence download: {result.Summary}", []);
                var imported = await ImportDownloadedAsync(result.Items, token).ConfigureAwait(false);
                return new("", ReviewedOperationState.Succeeded, true, $"{backend} fence download: {result.Summary} Saved {imported.Count} local fence asset(s).", imported.Select(item => item.FenceId).ToArray());
            }));
    }

    public Task<ReviewedOperationSnapshot> PlanClearAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var target = Target(vehicleId);
        var backend = target is null ? "target" : Backend(target);
        var findings = TargetFindings(vehicleId);
        var resolvedConnection = ResolveMavlinkConnectionId(connectionId, vehicleId);
        findings = AddConnectionFinding(findings, target, resolvedConnection);
        var operationTargets = target?.IsGhost == true ? new[] { vehicleId } : new[] { resolvedConnection, vehicleId };
        return Task.FromResult(_reviewed.Plan(
            ReviewedOperationKind.FenceClear, $"Clear {backend} fences", findings, operationTargets,
            "Clear the target's current polygon fence set.", operationTargets,
            async token =>
            {
                var currentFindings = TargetFindings(vehicleId);
                if (currentFindings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking))
                    return new("", ReviewedOperationState.Failed, false, "Fence clear is no longer available.", currentFindings.Select(item => item.Message).ToArray());
                if (target?.IsGhost == true)
                {
                    await _ghosts.ClearFenceAsync(vehicleId, token).ConfigureAwait(false);
                    return new("", ReviewedOperationState.Succeeded, true, "Ghost fence cleared locally.", [vehicleId]);
                }
                var result = await Client(resolvedConnection).ClearFenceAsync(CommandVehicle(vehicleId), token).ConfigureAwait(false);
                return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, $"{backend} fence clear: {result.Summary}", []);
            }));
    }

    private IReadOnlyList<WorkflowFinding> ValidateDocument(FenceDocument document, UnitObservationSnapshot? target)
    {
        var findings = new List<WorkflowFinding>();
        try { FenceLibraryStore.Validate(document); }
        catch (Exception ex) { findings.Add(new("FENCE_INVALID", WorkflowFindingSeverity.Blocking, ex.Message)); }
        if (target is null) return findings;
        findings.AddRange(TargetFindings(target.Id));
        if (target.Telemetry?.LatitudeDegrees is { } latitude && target.Telemetry.LongitudeDegrees is { } longitude)
        {
            var inside = Contains(document.Coordinates, latitude, longitude);
            if (document.Kind == FenceKind.Inclusion && !inside)
                findings.Add(new("FENCE_HOME_OUTSIDE", WorkflowFindingSeverity.Blocking, "The inclusion fence must contain the target's current position."));
            if (document.Kind == FenceKind.Exclusion && inside)
                findings.Add(new("FENCE_CURRENTLY_BREACHED", WorkflowFindingSeverity.Blocking, "The target is currently inside this exclusion fence."));
        }
        if (!target.IsGhost && (document.MinimumAltitudeMetres.HasValue || document.MaximumAltitudeMetres.HasValue) && !SupportsAltitudeBounds(target))
            findings.Add(new("FENCE_ALTITUDE_UNSUPPORTED", WorkflowFindingSeverity.Blocking, $"{Backend(target)} cannot represent polygon altitude bounds through the selected fence transfer; remove the altitude bounds before upload."));
        return findings;
    }

    private IReadOnlyList<WorkflowFinding> TargetFindings(string vehicleId) => TargetFindings(Target(vehicleId));
    private IReadOnlyList<WorkflowFinding> AddConnectionFinding(IReadOnlyList<WorkflowFinding> findings, UnitObservationSnapshot? target, string connectionId)
    {
        if (target?.IsGhost == true || ClientAvailable(connectionId)) return findings;
        return findings.Concat([new WorkflowFinding("FENCE_MAVLINK_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "A MAVLink connection is required for this fence target.")]).ToArray();
    }
    private static IReadOnlyList<WorkflowFinding> TargetFindings(UnitObservationSnapshot? target)
    {
        if (target is null) return [new("FENCE_TARGET_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "The selected fence target was not found.")];
        var backend = Backend(target);
        var findings = new List<WorkflowFinding>();
        if (backend is not ("Ghost" or "PX4" or "ArduPilot"))
            findings.Add(new("FENCE_BACKEND_UNSUPPORTED", WorkflowFindingSeverity.Blocking, $"Fence execution is not supported for {backend} targets."));
        if (!target.IsGhost && !target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase))
            findings.Add(new("FENCE_VEHICLE_UNSUPPORTED", WorkflowFindingSeverity.Blocking, "Fence execution requires a multicopter target."));
        if (target.State != ManagedConnectionState.Online)
            findings.Add(new("FENCE_TARGET_OFFLINE", WorkflowFindingSeverity.Blocking, "The fence target is offline."));
        if (target.Telemetry is null || target.Telemetry.IsStale)
            findings.Add(new("FENCE_TELEMETRY_STALE", WorkflowFindingSeverity.Blocking, "Fresh target telemetry is required for fence operations."));
        if (target.Telemetry?.LatitudeDegrees is null || target.Telemetry.LongitudeDegrees is null)
            findings.Add(new("FENCE_GLOBAL_POSITION_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "A current global position is required for fence operations."));
        if (target.Diagnostics?.Blockers is { Count: > 0 } blockers)
            findings.Add(new("FENCE_PREFLIGHT_BLOCKED", WorkflowFindingSeverity.Blocking, blockers[0]));
        return findings;
    }

    private static string Backend(UnitObservationSnapshot target)
        => target.IsGhost ? "Ghost" : target.ProfileKey.Contains("ardu", StringComparison.OrdinalIgnoreCase) ? "ArduPilot" : target.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase) ? "PX4" : target.ProfileKey;
    private static bool SupportsAltitudeBounds(UnitObservationSnapshot target) => target.IsGhost;
    private static bool Contains(IReadOnlyList<FlightMissionCoordinate> polygon, double latitude, double longitude)
    {
        var inside = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            var previous = polygon[(index + polygon.Count - 1) % polygon.Count];
            if ((previous.LatitudeDegrees > latitude) != (polygon[index].LatitudeDegrees > latitude) && longitude < (polygon[index].LongitudeDegrees - previous.LongitudeDegrees) * (latitude - previous.LatitudeDegrees) / (polygon[index].LatitudeDegrees - previous.LatitudeDegrees) + previous.LongitudeDegrees) inside = !inside;
        }
        return inside;
    }

    private static IReadOnlyList<MavlinkMissionItem> Compile(FenceDocument fence)
        => fence.Coordinates.Select((point, index) => new MavlinkMissionItem(
            (ushort)index,
            fence.Kind == FenceKind.Inclusion ? FencePolygonInclusion : FencePolygonExclusion,
            GlobalFrame,
            (int)Math.Round(point.LatitudeDegrees * 1e7),
            (int)Math.Round(point.LongitudeDegrees * 1e7), 0,
            Param1: fence.Coordinates.Count, MissionType: 1)).ToArray();

    private async Task<IReadOnlyList<FenceDocument>> ImportDownloadedAsync(IReadOnlyList<MavlinkMissionItem> items, CancellationToken token)
    {
        if (items.Any(item => item.Command is not (FencePolygonInclusion or FencePolygonExclusion)))
            throw new InvalidDataException("The target returned an unsupported fence command.");
        var ordered = items.OrderBy(item => item.Sequence).ToArray();
        var imported = new List<FenceDocument>();
        for (var index = 0; index < ordered.Length;)
        {
            var first = ordered[index];
            var count = (int)Math.Round(first.Param1);
            if (count < 3 || index + count > ordered.Length) throw new InvalidDataException("The target returned an incomplete polygon fence.");
            var vertices = ordered.Skip(index).Take(count).ToArray();
            if (vertices.Any(item => item.Command != first.Command || (int)Math.Round(item.Param1) != count)) throw new InvalidDataException("The target returned an inconsistent polygon fence.");
            var now = DateTimeOffset.UtcNow;
            var coordinates = vertices.Select(item => new FlightMissionCoordinate(item.LatitudeE7 / 10_000_000d, item.LongitudeE7 / 10_000_000d)).ToArray();
            var kind = first.Command == FencePolygonInclusion ? FenceKind.Inclusion : FenceKind.Exclusion;
            var document = new FenceDocument(FenceDocument.CurrentSchemaVersion, $"fence-{Guid.NewGuid():N}", $"Downloaded {kind} fence", kind, coordinates, null, null, null, null, null, now, now);
            imported.Add(await _store.SaveAsync(document, false, token).ConfigureAwait(false));
            index += count;
        }
        return imported;
    }

    private FenceDocument Require(string id) => _store.TryGet(id, out var fence) && fence is not null ? fence : throw new KeyNotFoundException("Fence was not found.");
    private UnitObservationSnapshot? Target(string? id) { _units.TryGet(id ?? string.Empty, out var target); return target; }
    private IMavlinkMissionClient Client(string id) => _connections.TryGet(id, out var connection) && connection is IMavlinkMissionClient client ? client : throw new InvalidOperationException("The selected MAVLink connection is unavailable.");
    private bool ClientAvailable(string id) => _connections.TryGet(id, out var connection) && connection is IMavlinkMissionClient;
    private string ResolveMavlinkConnectionId(string requested, string vehicleId)
    {
        if (_connections.TryGet(requested, out var connection) && connection is IMavlinkMissionClient) return requested;
        return Target(vehicleId)?.ConnectionIds.FirstOrDefault(id => _connections.TryGet(id, out var candidate) && candidate is IMavlinkMissionClient) ?? requested;
    }
    private string CommandVehicle(string unitId) => Target(unitId)?.CommandAuthorityVehicleId ?? unitId;
    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    // Compatibility surface for existing PX4 callers.
    IReadOnlyList<Px4FenceSnapshot> IPx4GeofenceWorkflow.Fences => Fences.Select(ToLegacy).ToArray();
    async Task<Px4FenceSnapshot> IPx4GeofenceWorkflow.CreateFromZoneAsync(string geometryId, string? name, Px4FenceKind kind, CancellationToken token)
        => ToLegacy(await CreateFromZoneAsync(geometryId, name, kind == Px4FenceKind.Inclusion ? FenceKind.Inclusion : FenceKind.Exclusion, token).ConfigureAwait(false));
    async Task<Px4FenceSnapshot> IPx4GeofenceWorkflow.ImportAsync(string path, bool replace, CancellationToken token)
        => ToLegacy(await ImportAsync(path, replace, token).ConfigureAwait(false));
    Task IPx4GeofenceWorkflow.DeleteAsync(string id, CancellationToken token) => DeleteAsync(id, token);
    Task IPx4GeofenceWorkflow.ExportAsync(string id, string path, CancellationToken token) => ExportAsync(id, path, token);
    Task<IReadOnlyList<WorkflowFinding>> IPx4GeofenceWorkflow.ValidateAsync(string id, string? vehicleId, CancellationToken token) => ValidateAsync(id, vehicleId, token);
    Task<ReviewedOperationSnapshot> IPx4GeofenceWorkflow.PlanUploadAsync(string id, string connection, string vehicle, CancellationToken token) => PlanUploadAsync(id, connection, vehicle, token);
    Task<ReviewedOperationSnapshot> IPx4GeofenceWorkflow.PlanDownloadAsync(string connection, string vehicle, CancellationToken token) => PlanDownloadAsync(connection, vehicle, token);
    Task<ReviewedOperationSnapshot> IPx4GeofenceWorkflow.PlanClearAsync(string connection, string vehicle, CancellationToken token) => PlanClearAsync(connection, vehicle, token);

    private static Px4FenceSnapshot ToLegacy(FenceSnapshot snapshot)
        => new(new(Px4FenceDocument.CurrentSchemaVersion, snapshot.Document.FenceId, snapshot.Document.DisplayName, snapshot.Document.Kind == FenceKind.Inclusion ? Px4FenceKind.Inclusion : Px4FenceKind.Exclusion, snapshot.Document.Coordinates, snapshot.Document.SourceGeometryId, snapshot.Document.SourceGeometryName, snapshot.Document.SourceGeometryHash, snapshot.Document.MinimumAltitudeMetres, snapshot.Document.MaximumAltitudeMetres, snapshot.Document.CreatedAt, snapshot.Document.UpdatedAt, snapshot.Document.ContentSha256), snapshot.Findings);
}
