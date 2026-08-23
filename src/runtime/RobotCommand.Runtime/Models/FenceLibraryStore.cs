using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobotCommand.Core;

namespace RobotCommand.Models;

/// <summary>Persistent target-neutral fence library with legacy PX4 migration.</summary>
public sealed class FenceLibraryStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private IReadOnlyList<FenceDocument> _fences = [];

    public FenceLibraryStore(string baseDirectory)
    {
        _root = Path.Combine(baseDirectory, "data", "fences");
        Directory.CreateDirectory(_root);
        MigrateLegacy(baseDirectory);
        Refresh();
    }

    public IReadOnlyList<FenceDocument> Fences => _fences;
    public event EventHandler? Changed;
    public bool TryGet(string id, out FenceDocument? document)
    {
        document = _fences.FirstOrDefault(item => item.FenceId == id);
        return document is not null;
    }

    public async Task<FenceDocument> SaveAsync(FenceDocument document, bool replace = true, CancellationToken token = default)
    {
        Validate(document);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = PathFor(document.FenceId);
            if (!replace && File.Exists(path))
                throw new InvalidOperationException($"Fence '{document.FenceId}' already exists.");
            var canonical = document with { SchemaVersion = FenceDocument.CurrentSchemaVersion, ContentSha256 = string.Empty };
            var prepared = canonical with
            {
                ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, Json))))
            };
            var temporary = Path.Combine(_root, $".{prepared.FenceId}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(prepared, Json), token).ConfigureAwait(false);
            File.Move(temporary, path, true);
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
            return prepared;
        }
        finally { _gate.Release(); }
    }

    public async Task<FenceDocument> ImportAsync(string path, bool replace, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        var document = JsonSerializer.Deserialize<FenceDocument>(await File.ReadAllTextAsync(full, token).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Fence file is invalid.");
        if (document.SchemaVersion != FenceDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported fence schema '{document.SchemaVersion}'.");
        return await SaveAsync(document, replace, token).ConfigureAwait(false);
    }

    public async Task ExportAsync(string id, string path, CancellationToken token)
    {
        if (!TryGet(id, out var document) || document is null)
            throw new KeyNotFoundException("Fence was not found.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fence exports must be outside the managed fence library.");
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = $"{full}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), token).ConfigureAwait(false);
        File.Move(temporary, full, true);
    }

    public async Task RemoveAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public static void Validate(FenceDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.FenceId) || document.FenceId.Any(value => !char.IsLetterOrDigit(value) && value is not '-' and not '_'))
            throw new InvalidDataException("Fence ID is invalid.");
        if (string.IsNullOrWhiteSpace(document.DisplayName)) throw new InvalidDataException("Fence name is required.");
        if (document.Coordinates.Count < 3) throw new InvalidDataException("A polygon fence needs at least three coordinates.");
        if (document.Coordinates.Any(point => !double.IsFinite(point.LatitudeDegrees) || !double.IsFinite(point.LongitudeDegrees) || point.LatitudeDegrees is < -90 or > 90 || point.LongitudeDegrees is < -180 or > 180))
            throw new InvalidDataException("Fence coordinates must be valid WGS84 values.");
        for (var first = 0; first < document.Coordinates.Count; first++)
        {
            for (var second = first + 1; second < document.Coordinates.Count; second++)
            {
                if (document.Coordinates[first] == document.Coordinates[second])
                    throw new InvalidDataException("Fence polygon must not contain duplicate vertices.");
            }
        }
        if (document.MinimumAltitudeMetres is { } minimum && (!double.IsFinite(minimum) || minimum < 0))
            throw new InvalidDataException("Fence minimum altitude is invalid.");
        if (document.MaximumAltitudeMetres is { } maximum && (!double.IsFinite(maximum) || maximum < 0 || (document.MinimumAltitudeMetres is { } minimumForMaximum && maximum <= minimumForMaximum)))
            throw new InvalidDataException("Fence maximum altitude is invalid.");
        if (Math.Abs(SignedArea(document.Coordinates)) < 0.0000000001d)
            throw new InvalidDataException("Fence polygon has no usable area.");
        for (var first = 0; first < document.Coordinates.Count; first++)
        {
            var next = (first + 1) % document.Coordinates.Count;
            for (var second = first + 1; second < document.Coordinates.Count; second++)
            {
                var secondNext = (second + 1) % document.Coordinates.Count;
                if (next == second || first == secondNext) continue;
                if (SegmentsIntersect(document.Coordinates[first], document.Coordinates[next], document.Coordinates[second], document.Coordinates[secondNext]))
                    throw new InvalidDataException("Fence polygon must not self-intersect.");
            }
        }
    }

    private void MigrateLegacy(string baseDirectory)
    {
        var legacyRoot = Path.Combine(baseDirectory, "data", "px4-fences");
        if (!Directory.Exists(legacyRoot)) return;
        foreach (var path in Directory.EnumerateFiles(legacyRoot, "*.json"))
        {
            try
            {
                var old = JsonSerializer.Deserialize<Px4FenceDocument>(File.ReadAllText(path), Json);
                if (old is null || old.SchemaVersion != Px4FenceDocument.CurrentSchemaVersion) continue;
                var migrated = new FenceDocument(
                    FenceDocument.CurrentSchemaVersion, old.FenceId, old.DisplayName,
                    old.Kind == Px4FenceKind.Inclusion ? FenceKind.Inclusion : FenceKind.Exclusion,
                    old.Coordinates, old.SourceGeometryId, old.SourceGeometryName, old.SourceGeometryHash,
                    old.MinimumAltitudeMetres, old.MaximumAltitudeMetres, old.CreatedAt, old.UpdatedAt, string.Empty);
                Validate(migrated);
                var canonical = migrated with { ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(migrated, Json)))) };
                var destination = PathFor(canonical.FenceId);
                if (!File.Exists(destination)) File.WriteAllText(destination, JsonSerializer.Serialize(canonical, Json));
            }
            catch { /* Keep startup resilient; the legacy file remains available for repair. */ }
        }
    }

    private void Refresh()
        => _fences = Directory.EnumerateFiles(_root, "*.json")
            .Select(path => { try { return JsonSerializer.Deserialize<FenceDocument>(File.ReadAllText(path), Json); } catch { return null; } })
            .Where(item => item?.SchemaVersion == FenceDocument.CurrentSchemaVersion)
            .Cast<FenceDocument>()
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Dispose() => _gate.Dispose();

    private string PathFor(string id) => Path.Combine(_root, $"{id}.json");

    private static double SignedArea(IReadOnlyList<FlightMissionCoordinate> points)
    {
        double value = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            value += points[index].LongitudeDegrees * next.LatitudeDegrees - next.LongitudeDegrees * points[index].LatitudeDegrees;
        }
        return value / 2d;
    }

    private static bool SegmentsIntersect(FlightMissionCoordinate a, FlightMissionCoordinate b, FlightMissionCoordinate c, FlightMissionCoordinate d)
    {
        static double Cross(FlightMissionCoordinate p, FlightMissionCoordinate q, FlightMissionCoordinate r)
            => (q.LongitudeDegrees - p.LongitudeDegrees) * (r.LatitudeDegrees - p.LatitudeDegrees) - (q.LatitudeDegrees - p.LatitudeDegrees) * (r.LongitudeDegrees - p.LongitudeDegrees);
        var abC = Cross(a, b, c); var abD = Cross(a, b, d); var cdA = Cross(c, d, a); var cdB = Cross(c, d, b);
        return ((abC > 0 && abD < 0) || (abC < 0 && abD > 0)) && ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0));
    }
}
