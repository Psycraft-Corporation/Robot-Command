using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Mavlink;

namespace RobotCommand.Services.Workflows;

public sealed class Px4GeofenceWorkflow : IPx4GeofenceWorkflow
{
    // Fence vertices are exchanged using MISSION_ITEM_INT, so PX4 expects the
    // integer global frame rather than MAV_FRAME_GLOBAL's floating-point form.
    private const byte GlobalFrame = 5;
    private const ushort FencePolygonInclusion = 5001;
    private const ushort FencePolygonExclusion = 5002;
    private readonly Px4FenceLibraryStore _store; private readonly IGeometryDocumentStore _geometry; private readonly IUnitObservationWorkflow _units; private readonly IMavlinkConnectionRegistry _connections; private readonly ReviewedOperationWorkflow _reviewed;
    public Px4GeofenceWorkflow(Px4FenceLibraryStore store, IGeometryDocumentStore geometry, IUnitObservationWorkflow units, IMavlinkConnectionRegistry connections, ReviewedOperationWorkflow reviewed)
    { _store = store; _geometry = geometry; _units = units; _connections = connections; _reviewed = reviewed; _geometry.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty); _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty); }
    public event EventHandler? Changed;
    public IReadOnlyList<Px4FenceSnapshot> Fences => _store.Fences.Select(item => new Px4FenceSnapshot(item, ValidateDocument(item, null))).ToArray();
    public async Task<Px4FenceSnapshot> CreateFromZoneAsync(string geometryId, string? name = null, Px4FenceKind kind = Px4FenceKind.Inclusion, CancellationToken cancellationToken = default)
    {
        if (!_geometry.TryGet(geometryId, out var geometry) || geometry is null || geometry.Kind != GeometryDocumentKind.Zone) throw new InvalidOperationException("Select a saved zone before creating a PX4 fence.");
        var points = (geometry.Rings.FirstOrDefault()?.Points ?? geometry.Points).Select(point => new FlightMissionCoordinate(point.LatitudeDegrees, point.LongitudeDegrees)).ToArray(); var now = DateTimeOffset.UtcNow;
        var document = await _store.SaveAsync(new(Px4FenceDocument.CurrentSchemaVersion, $"fence-{Guid.NewGuid():N}", name?.Trim() ?? geometry.DisplayName, kind, points, geometry.GeometryId, geometry.DisplayName, geometry.ContentSha256, null, null, now, now), false, cancellationToken);
        return new(document, ValidateDocument(document, null));
    }
    public async Task DeleteAsync(string fenceId, CancellationToken cancellationToken = default) { await _store.RemoveAsync(fenceId, cancellationToken); }
    public async Task<Px4FenceSnapshot> ImportAsync(string path, bool replace = false, CancellationToken cancellationToken = default) { var document = await _store.ImportAsync(path, replace, cancellationToken); return new(document, ValidateDocument(document, null)); }
    public Task ExportAsync(string fenceId, string path, CancellationToken cancellationToken = default) => _store.ExportAsync(fenceId, path, cancellationToken);
    public Task<IReadOnlyList<WorkflowFinding>> ValidateAsync(string fenceId, string? vehicleId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkflowFinding>>(ValidateDocument(Require(fenceId), Target(vehicleId)));
    public Task<ReviewedOperationSnapshot> PlanUploadAsync(string fenceId, string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var resolvedConnection = ResolveMavlinkConnectionId(connectionId, vehicleId);
        var findings = ValidateDocument(Require(fenceId), Target(vehicleId));
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.Px4FenceUpload, "Upload PX4 fence", findings, [resolvedConnection, vehicleId], "Upload PX4 fence", [resolvedConnection, vehicleId], async token =>
        {
            var fence = Require(fenceId);
            var currentFindings = ValidateDocument(fence, Target(vehicleId));
            if (currentFindings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking))
                return new("", ReviewedOperationState.Failed, false, "PX4 fence upload is no longer available.", currentFindings.Select(item => item.Message).ToArray());
            var result = await Client(resolvedConnection).UploadFenceAsync(CommandVehicle(vehicleId), Compile(fence), token);
            return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, result.Summary, []);
        }));
    }
    public Task<ReviewedOperationSnapshot> PlanDownloadAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var resolvedConnection = ResolveMavlinkConnectionId(connectionId, vehicleId);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.Px4FenceDownload, "Download PX4 fences", TargetFindings(vehicleId), [resolvedConnection, vehicleId], "Download PX4 fences", [resolvedConnection, vehicleId], async token =>
    {
        var currentFindings = TargetFindings(vehicleId);
        if (currentFindings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking))
            return new("", ReviewedOperationState.Failed, false, "PX4 fence download is no longer available.", currentFindings.Select(item => item.Message).ToArray());
        var result = await Client(resolvedConnection).DownloadFenceAsync(CommandVehicle(vehicleId), token);
        if (!result.Succeeded) return new("", ReviewedOperationState.Failed, false, result.Summary, []);
        var imported = await ImportDownloadedAsync(result.Items, token);
        return new("", ReviewedOperationState.Succeeded, true, result.Summary + " Saved " + imported.Count + " local fence asset(s).", imported.Select(item => item.FenceId).ToArray());
    }));
    }
    public Task<ReviewedOperationSnapshot> PlanClearAsync(string connectionId, string vehicleId, CancellationToken cancellationToken = default)
    {
        var resolvedConnection = ResolveMavlinkConnectionId(connectionId, vehicleId);
        return Task.FromResult(_reviewed.Plan(ReviewedOperationKind.Px4FenceClear, "Clear PX4 fences", TargetFindings(vehicleId), [resolvedConnection, vehicleId], "Clear PX4 fences", [resolvedConnection, vehicleId], async token =>
        {
            var currentFindings = TargetFindings(vehicleId);
            if (currentFindings.Any(item => item.Severity == WorkflowFindingSeverity.Blocking))
                return new("", ReviewedOperationState.Failed, false, "PX4 fence clear is no longer available.", currentFindings.Select(item => item.Message).ToArray());
            var result = await Client(resolvedConnection).ClearFenceAsync(CommandVehicle(vehicleId), token);
            return new("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, result.Summary, []);
        }));
    }
    private IReadOnlyList<WorkflowFinding> ValidateDocument(Px4FenceDocument document, UnitObservationSnapshot? target)
    {
        var findings = new List<WorkflowFinding>(); try { Px4FenceLibraryStore.Validate(document); } catch (Exception ex) { findings.Add(new("PX4_FENCE_INVALID", WorkflowFindingSeverity.Blocking, ex.Message)); }
        if (target is not null)
        {
            findings.AddRange(TargetFindings(target.Id));
            if (target.Telemetry?.LatitudeDegrees is { } lat && target.Telemetry.LongitudeDegrees is { } lon)
            {
                var inside = Contains(document.Coordinates, lat, lon);
                if (document.Kind == Px4FenceKind.Inclusion && !inside)
                    findings.Add(new("PX4_FENCE_HOME_OUTSIDE", WorkflowFindingSeverity.Blocking, "The inclusion fence must contain the current PX4 position."));
                if (document.Kind == Px4FenceKind.Exclusion && inside)
                    findings.Add(new("PX4_FENCE_CURRENTLY_BREACHED", WorkflowFindingSeverity.Blocking, "The current PX4 position is inside this exclusion fence."));
            }
            if (document.MinimumAltitudeMetres.HasValue || document.MaximumAltitudeMetres.HasValue)
                findings.Add(new("PX4_FENCE_ALTITUDE_METADATA", WorkflowFindingSeverity.Warning, "Altitude bounds are retained in this asset but are not sent by the current PX4 polygon-fence transfer."));
        }
        return findings;
    }
    private static bool Contains(IReadOnlyList<FlightMissionCoordinate> polygon, double latitude, double longitude) { var inside = false; for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++) { var a = polygon[i]; var b = polygon[j]; if ((a.LatitudeDegrees > latitude) != (b.LatitudeDegrees > latitude) && longitude < (b.LongitudeDegrees - a.LongitudeDegrees) * (latitude - a.LatitudeDegrees) / (b.LatitudeDegrees - a.LatitudeDegrees) + a.LongitudeDegrees) inside = !inside; } return inside; }
    private static IReadOnlyList<MavlinkMissionItem> Compile(Px4FenceDocument fence) => fence.Coordinates.Select((point, index) => new MavlinkMissionItem((ushort)index, fence.Kind == Px4FenceKind.Inclusion ? FencePolygonInclusion : FencePolygonExclusion, GlobalFrame, (int)Math.Round(point.LatitudeDegrees * 1e7), (int)Math.Round(point.LongitudeDegrees * 1e7), 0, Param1: fence.Coordinates.Count, MissionType: 1)).ToArray();
    private Px4FenceDocument Require(string id) => _store.TryGet(id, out var fence) && fence is not null ? fence : throw new KeyNotFoundException("Fence was not found.");
    private UnitObservationSnapshot? Target(string? id) { _units.TryGet(id ?? string.Empty, out var target); return target; }
    private IReadOnlyList<WorkflowFinding> TargetFindings(string vehicleId)
    {
        var target = Target(vehicleId);
        return target is not null && target.ProfileKey.Contains("px4", StringComparison.OrdinalIgnoreCase) && target.VehicleClass.Contains("multicopter", StringComparison.OrdinalIgnoreCase) && target.Telemetry is { IsStale: false, LatitudeDegrees: not null, LongitudeDegrees: not null }
            ? []
            : [new("PX4_FENCE_TARGET_UNAVAILABLE", WorkflowFindingSeverity.Blocking, "Fresh PX4 multicopter telemetry with a global position is required for fence operations.")];
    }
    private async Task<IReadOnlyList<Px4FenceDocument>> ImportDownloadedAsync(IReadOnlyList<MavlinkMissionItem> items, CancellationToken token)
    {
        var supported = items.Where(item => item.Command is FencePolygonInclusion or FencePolygonExclusion).OrderBy(item => item.Sequence).ToArray();
        if (supported.Length == 0) return [];
        var now = DateTimeOffset.UtcNow;
        var imported = new List<Px4FenceDocument>();
        for (var index = 0; index < supported.Length;)
        {
            var first = supported[index];
            var vertexCount = (int)Math.Round(first.Param1);
            if (vertexCount < 3 || index + vertexCount > supported.Length)
                throw new InvalidDataException("PX4 returned an incomplete polygon fence.");
            var vertices = supported.Skip(index).Take(vertexCount).ToArray();
            if (vertices.Any(item => item.Command != first.Command || (int)Math.Round(item.Param1) != vertexCount))
                throw new InvalidDataException("PX4 returned an inconsistent polygon fence.");
            var coordinates = vertices.Select(item => new FlightMissionCoordinate(item.LatitudeE7 / 10_000_000d, item.LongitudeE7 / 10_000_000d)).ToArray();
            var kind = first.Command == FencePolygonInclusion ? Px4FenceKind.Inclusion : Px4FenceKind.Exclusion;
            imported.Add(await _store.SaveAsync(new(Px4FenceDocument.CurrentSchemaVersion, $"fence-{Guid.NewGuid():N}", $"Downloaded {kind} fence", kind, coordinates, null, null, null, null, null, now, now), false, token));
            index += vertexCount;
        }
        return imported;
    }
    private IMavlinkMissionClient Client(string id) => _connections.TryGet(id, out var connection) && connection is IMavlinkMissionClient client ? client : throw new InvalidOperationException("The selected MAVLink connection is unavailable.");
    private string ResolveMavlinkConnectionId(string requestedConnectionId, string unitId)
    {
        if (_connections.TryGet(requestedConnectionId, out var requested) && requested is IMavlinkMissionClient)
            return requestedConnectionId;
        var target = Target(unitId);
        return target?.ConnectionIds.FirstOrDefault(connectionId =>
                   _connections.TryGet(connectionId, out var connection) && connection is IMavlinkMissionClient)
               ?? requestedConnectionId;
    }
    private string CommandVehicle(string unitId) => Target(unitId)?.CommandAuthorityVehicleId ?? unitId;
}
