using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobotCommand.Core;

namespace RobotCommand.Models;

public sealed class Px4FenceLibraryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private IReadOnlyList<Px4FenceDocument> _fences = [];

    public Px4FenceLibraryStore(string baseDirectory)
    {
        _root = Path.Combine(baseDirectory, "data", "px4-fences");
        Directory.CreateDirectory(_root);
        Refresh();
    }

    public IReadOnlyList<Px4FenceDocument> Fences => _fences;
    public event EventHandler? Changed;
    public bool TryGet(string id, out Px4FenceDocument? document) { document = _fences.FirstOrDefault(item => item.FenceId == id); return document is not null; }
    public async Task<Px4FenceDocument> SaveAsync(Px4FenceDocument document, bool replace = true, CancellationToken token = default)
    {
        Validate(document); await _gate.WaitAsync(token);
        try
        {
            var path = PathFor(document.FenceId); if (!replace && File.Exists(path)) throw new InvalidOperationException($"Fence '{document.FenceId}' already exists.");
            var canonical = document with { SchemaVersion = Px4FenceDocument.CurrentSchemaVersion, ContentSha256 = string.Empty };
            var prepared = canonical with { ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, Json)))) };
            var temporary = Path.Combine(_root, $".{prepared.FenceId}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(prepared, Json), token); File.Move(temporary, path, true); Refresh(); Changed?.Invoke(this, EventArgs.Empty); return prepared;
        }
        finally { _gate.Release(); }
    }
    public async Task<Px4FenceDocument> ImportAsync(string path, bool replace, CancellationToken token)
    {
        var full = Path.GetFullPath(path); var document = JsonSerializer.Deserialize<Px4FenceDocument>(await File.ReadAllTextAsync(full, token), Json) ?? throw new InvalidDataException("Fence file is invalid.");
        if (document.SchemaVersion != Px4FenceDocument.CurrentSchemaVersion) throw new InvalidDataException($"Unsupported fence schema '{document.SchemaVersion}'.");
        return await SaveAsync(document, replace, token);
    }
    public async Task ExportAsync(string id, string path, CancellationToken token)
    {
        if (!TryGet(id, out var document) || document is null) throw new KeyNotFoundException("Fence was not found.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fence exports must be outside the managed fence library.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = $"{full}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), token);
        File.Move(temporary, full, true);
    }
    public async Task RemoveAsync(string id, CancellationToken token) { await _gate.WaitAsync(token); try { var path = PathFor(id); if (File.Exists(path)) File.Delete(path); Refresh(); Changed?.Invoke(this, EventArgs.Empty); } finally { _gate.Release(); } }
    public static void Validate(Px4FenceDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.FenceId) || document.FenceId.Any(value => !char.IsLetterOrDigit(value) && value is not '-' and not '_')) throw new InvalidDataException("Fence ID is invalid.");
        if (string.IsNullOrWhiteSpace(document.DisplayName)) throw new InvalidDataException("Fence name is required.");
        if (document.Coordinates.Count < 3) throw new InvalidDataException("A polygon fence needs at least three coordinates.");
        if (document.Coordinates.Any(point => !double.IsFinite(point.LatitudeDegrees) || !double.IsFinite(point.LongitudeDegrees) || point.LatitudeDegrees is < -90 or > 90 || point.LongitudeDegrees is < -180 or > 180)) throw new InvalidDataException("Fence coordinates must be valid WGS84 values.");
        var minimumAltitude = document.MinimumAltitudeMetres;
        var maximumAltitude = document.MaximumAltitudeMetres;
        if (minimumAltitude.HasValue && (!double.IsFinite(minimumAltitude.Value) || minimumAltitude.Value < 0)) throw new InvalidDataException("Fence minimum altitude is invalid.");
        if (maximumAltitude.HasValue && (!double.IsFinite(maximumAltitude.Value) || maximumAltitude.Value < 0 || (minimumAltitude.HasValue && maximumAltitude.Value <= minimumAltitude.Value))) throw new InvalidDataException("Fence maximum altitude is invalid.");
        if (Math.Abs(SignedArea(document.Coordinates)) < 0.0000000001d) throw new InvalidDataException("Fence polygon has no usable area.");
        for (var first = 0; first < document.Coordinates.Count; first++)
        {
            var next = (first + 1) % document.Coordinates.Count;
            for (var second = first + 1; second < document.Coordinates.Count; second++)
            {
                var secondNext = (second + 1) % document.Coordinates.Count;
                if (first == second || next == second || first == secondNext) continue;
                if (SegmentsIntersect(document.Coordinates[first], document.Coordinates[next], document.Coordinates[second], document.Coordinates[secondNext]))
                    throw new InvalidDataException("Fence polygon must not self-intersect.");
            }
        }
    }
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
    private void Refresh() => _fences = Directory.EnumerateFiles(_root, "*.json").Select(path => { try { return JsonSerializer.Deserialize<Px4FenceDocument>(File.ReadAllText(path), Json); } catch { return null; } }).Where(item => item?.SchemaVersion == Px4FenceDocument.CurrentSchemaVersion).Cast<Px4FenceDocument>().OrderBy(item => item.DisplayName).ToArray();
    private string PathFor(string id) => Path.Combine(_root, $"{id}.json");
}
