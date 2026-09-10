using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobotCommand.Core;

namespace RobotCommand.Models;

public sealed class FlightMissionLibraryStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private readonly string _documents;
    private IReadOnlyList<FlightMissionDocument> _missions = [];

    public FlightMissionLibraryStore(string baseDirectory)
    {
        _root = Path.Combine(baseDirectory, "data", "flight-missions");
        _documents = Path.Combine(_root, "documents");
        Directory.CreateDirectory(_documents);
        Refresh();
    }

    public IReadOnlyList<FlightMissionDocument> Missions => _missions;

    public bool TryGet(string id, out FlightMissionDocument? mission)
    {
        mission = _missions.FirstOrDefault(item => item.MissionId.Equals(id, StringComparison.Ordinal));
        return mission is not null;
    }

    public async Task<FlightMissionDocument> SaveAsync(FlightMissionDocument document, bool replace = true, CancellationToken cancellationToken = default)
    {
        ValidateId(document.MissionId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(document.MissionId);
            if (!replace && File.Exists(path)) throw new InvalidOperationException($"Mission '{document.MissionId}' already exists.");
            var prepared = Prepare(document);
            var temporary = Path.Combine(_root, $".{prepared.MissionId}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(prepared, Json), cancellationToken);
            File.Move(temporary, path, true);
            Refresh();
            return prepared;
        }
        finally { _gate.Release(); }
    }

    public async Task<FlightMissionDocument> ImportAsync(string path, bool replace, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Mission file was not found.", fullPath);
        var document = JsonSerializer.Deserialize<FlightMissionDocument>(await File.ReadAllTextAsync(fullPath, cancellationToken), Json)
            ?? throw new InvalidDataException("The mission file is empty or invalid.");
        if (!string.Equals(document.SchemaVersion, FlightMissionDocument.CurrentSchemaVersion, StringComparison.Ordinal) &&
            !string.Equals(document.SchemaVersion, FlightMissionDocument.LegacySchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported mission schema '{document.SchemaVersion}'.");
        return await SaveAsync(document with { SourceSummary = Path.GetFileName(fullPath) }, replace, cancellationToken);
    }

    public async Task ExportAsync(string id, string path, CancellationToken cancellationToken)
    {
        if (!TryGet(id, out var document) || document is null) throw new KeyNotFoundException($"Mission '{id}' was not found.");
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mission exports must be outside the managed mission library.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), cancellationToken);
        File.Move(temporary, fullPath, true);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        await _gate.WaitAsync(cancellationToken);
        try { var path = PathFor(id); if (File.Exists(path)) File.Delete(path); Refresh(); }
        finally { _gate.Release(); }
    }

    private void Refresh()
    {
        _missions = Directory.EnumerateFiles(_documents, "*.json")
            .Select(path => TryRead(path))
            .Where(item => item is not null)
            .Cast<FlightMissionDocument>()
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private FlightMissionDocument? TryRead(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<FlightMissionDocument>(File.ReadAllText(path), Json);
            return document is not null && (document.SchemaVersion == FlightMissionDocument.CurrentSchemaVersion || document.SchemaVersion == FlightMissionDocument.LegacySchemaVersion) ? Prepare(document) : null;
        }
        catch (Exception) { return null; }
    }

    private static FlightMissionDocument Prepare(FlightMissionDocument document)
    {
        var migrated = document.SchemaVersion == FlightMissionDocument.LegacySchemaVersion
            ? document with { SchemaVersion = FlightMissionDocument.CurrentSchemaVersion, CruiseSpeedMetresPerSecond = 5, CameraIntent = null, TargetAssignment = null }
            : document;
        Validate(migrated);
        var canonical = migrated with { ContentSha256 = string.Empty };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, Json))));
        return migrated with { ContentSha256 = hash, UpdatedAt = migrated.UpdatedAt == default ? DateTimeOffset.UtcNow : migrated.UpdatedAt };
    }

    public static void Validate(FlightMissionDocument document)
    {
        ValidateId(document.MissionId);
        if (string.IsNullOrWhiteSpace(document.DisplayName)) throw new InvalidDataException("Mission name is required.");
        if (!double.IsFinite(document.RelativeAltitudeMetres) || document.RelativeAltitudeMetres <= 0 || document.RelativeAltitudeMetres > 5000)
            throw new InvalidDataException("Mission altitude must be between 0 and 5000 metres.");
        if (!double.IsFinite(document.CruiseSpeedMetresPerSecond) || document.CruiseSpeedMetresPerSecond is <= 0 or > 30)
            throw new InvalidDataException("Mission cruise speed must be between 0 and 30 metres per second.");
        ValidateCameraActions(document.CameraIntent, "Mission");
        for (var index = 0; index < document.Steps.Count; index++)
        {
            var step = document.Steps[index];
            if (string.IsNullOrWhiteSpace(step.Id)) throw new InvalidDataException("Each mission step needs an ID.");
            if (step.Kind == FlightMissionStepKind.Takeoff && index != 0)
                throw new InvalidDataException("Takeoff must be the first mission step.");
            if (step.Kind == FlightMissionStepKind.ReturnToLaunch &&
                index != document.Steps.Count - 1 &&
                (index != document.Steps.Count - 2 || document.Steps[index + 1].Kind != FlightMissionStepKind.Land))
                throw new InvalidDataException("RTL must be the final step or immediately precede Land.");
            if (step.Kind == FlightMissionStepKind.Land && index != document.Steps.Count - 1)
                throw new InvalidDataException("Land must be the final mission step.");
            // Geometry-backed steps may be authored before a reusable geometry
            // is chosen. They remain incomplete until the selected-step binding
            // is set, and the compiler reports that state before upload.
            var missingGeometry = string.IsNullOrWhiteSpace(step.SourceGeometryId) && step.FrozenCoordinates.Count == 0;
            if (step.Kind is FlightMissionStepKind.PointOfInterest && !missingGeometry && step.FrozenCoordinates.Count != 1)
                throw new InvalidDataException("A point-of-interest step must contain one coordinate.");
            if (step.Kind is FlightMissionStepKind.WaypointSequence && !missingGeometry && step.FrozenCoordinates.Count < 2)
                throw new InvalidDataException("A waypoint-sequence step must contain at least two coordinates.");
            if (step.Kind is FlightMissionStepKind.SurveyZone && !missingGeometry && step.FrozenCoordinates.Count < 3)
                throw new InvalidDataException("A survey-zone step must contain at least three zone coordinates.");
            if (step.Kind is FlightMissionStepKind.CorridorScan && !missingGeometry && step.FrozenCoordinates.Count < 2)
                throw new InvalidDataException("A corridor scan needs a waypoint sequence with at least two points.");
            if (step.Kind is FlightMissionStepKind.TimedLoiter && ((!missingGeometry && step.FrozenCoordinates.Count != 1) || step.LoiterDurationSeconds is not > 0 or > 3600))
                throw new InvalidDataException("A timed loiter needs one point and a duration between 0 and 3600 seconds.");
            if (step.RelativeAltitudeMetres is { } altitude && (!double.IsFinite(altitude) || altitude is <= 0 or > 5000))
                throw new InvalidDataException("Step altitude must be between 0 and 5000 metres.");
            if (step.CruiseSpeedMetresPerSecond is { } speed && (!double.IsFinite(speed) || speed is <= 0 or > 30))
                throw new InvalidDataException("Step speed must be between 0 and 30 metres per second.");
            if (step.Kind is FlightMissionStepKind.SurveyZone && step.Survey is { } survey &&
                (!double.IsFinite(survey.LineSpacingMetres) || survey.LineSpacingMetres <= 0 || !double.IsFinite(survey.BearingDegrees) || !double.IsFinite(survey.TurnaroundDistanceMetres) || survey.TurnaroundDistanceMetres < 0))
                throw new InvalidDataException("Survey spacing, bearing, and turnaround values are invalid.");
            if (step.Kind is FlightMissionStepKind.CorridorScan && step.Corridor is { } corridor &&
                (!double.IsFinite(corridor.CorridorWidthMetres) || corridor.CorridorWidthMetres <= 0 || corridor.CorridorWidthMetres > 10_000 ||
                 !double.IsFinite(corridor.LineSpacingMetres) || corridor.LineSpacingMetres <= 0 || corridor.LineSpacingMetres > 10_000 ||
                 !double.IsFinite(corridor.TurnaroundDistanceMetres) || corridor.TurnaroundDistanceMetres < 0 || corridor.TurnaroundDistanceMetres > 10_000 ||
                 !double.IsFinite(corridor.FrontLapPercent) || corridor.FrontLapPercent is < 0 or >= 100 ||
                 !double.IsFinite(corridor.SideLapPercent) || corridor.SideLapPercent is < 0 or >= 100))
                throw new InvalidDataException("Corridor width, spacing, turnaround, and overlap values are invalid.");
            ValidateCameraActions(step.CameraIntent, $"Step '{step.Id}'");
            ValidateCameraActions(step.Survey?.CameraIntent, $"Step '{step.Id}' survey");
            ValidateCameraActions(step.Corridor?.CameraIntent, $"Step '{step.Id}' corridor");
            foreach (var point in step.FrozenCoordinates)
                if (!double.IsFinite(point.LatitudeDegrees) || !double.IsFinite(point.LongitudeDegrees) || point.LatitudeDegrees is < -90 or > 90 || point.LongitudeDegrees is < -180 or > 180)
                    throw new InvalidDataException("A mission geometry snapshot contains invalid WGS84 coordinates.");
        }

        if (document.Steps.Count(step => step.Kind == FlightMissionStepKind.ReturnToLaunch) > 1)
            throw new InvalidDataException("A mission can contain only one RTL step.");
        if (document.Steps.Count(step => step.Kind == FlightMissionStepKind.Land) > 1)
            throw new InvalidDataException("A mission can contain only one Land step.");

        if (!Enum.IsDefined(document.EndAction))
            throw new InvalidDataException("Mission end action is invalid.");

    }

    private static void ValidateCameraActions(FlightMissionCameraIntent? intent, string context)
    {
        if (intent?.Actions is null) return;
        foreach (var action in intent.Actions)
        {
            var errors = action.ValidationErrors;
            if (errors.Count > 0)
                throw new InvalidDataException($"{context} camera action is invalid: {string.Join(" ", errors)}");
        }
    }

    public void Dispose() => _gate.Dispose();

    private string PathFor(string id) => Path.Combine(_documents, $"{id}.json");
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Mission ID must contain letters, numbers, hyphens, or underscores.", nameof(id));
    }
}
