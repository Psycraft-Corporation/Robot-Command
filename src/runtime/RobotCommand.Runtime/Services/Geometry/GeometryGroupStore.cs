using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryGroupStore : IGeometryGroupStore, IDisposable
{
    private const string ManifestFileName = "groups.geometry.json";
    private readonly IGeometryDocumentStore _documents;
    private readonly GeometryDocumentCodec _documentCodec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private Dictionary<string, string[]> _groups = new(StringComparer.OrdinalIgnoreCase);

    public GeometryGroupStore(IGeometryDocumentStore documents, GeometryDocumentCodec documentCodec)
    {
        _documents = documents;
        _documentCodec = documentCodec;
        _documents.Changed += OnDocumentsChanged;
        RefreshCore();
    }

    public event EventHandler? Changed;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups
    {
        get
        {
            lock (_stateGate)
            {
                return _groups.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyList<string> GetGroups(string geometryId)
    {
        lock (_stateGate)
        {
            return _groups.Where(pair => pair.Value.Contains(geometryId, StringComparer.Ordinal))
                .Select(pair => pair.Key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RefreshCore(); }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task CreateAsync(string name, CancellationToken cancellationToken = default)
        => MutateAsync(groups =>
        {
            var key = NormalizeName(name);
            if (groups.ContainsKey(key)) throw new InvalidOperationException($"Geometry group '{key}' already exists.");
            groups[key] = [];
        }, cancellationToken);

    public Task RenameAsync(string name, string replacement, CancellationToken cancellationToken = default)
        => MutateAsync(groups =>
        {
            var key = NormalizeName(name); var replacementKey = NormalizeName(replacement);
            if (!groups.Remove(key, out var members)) throw new KeyNotFoundException($"Geometry group '{key}' was not found.");
            if (groups.ContainsKey(replacementKey)) throw new InvalidOperationException($"Geometry group '{replacementKey}' already exists.");
            groups[replacementKey] = members;
        }, cancellationToken);

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        => MutateAsync(groups =>
        {
            var key = NormalizeName(name);
            if (!groups.TryGetValue(key, out var members)) throw new KeyNotFoundException($"Geometry group '{key}' was not found.");
            if (members.Length > 0)
                throw new InvalidOperationException($"Geometry group '{key}' is not empty. Remove its geometry objects before deleting it.");
            groups.Remove(key);
        }, cancellationToken);

    public Task AssignAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default)
        => MutateAsync(groups =>
        {
            var key = NormalizeName(name);
            if (!groups.TryGetValue(key, out var current)) throw new KeyNotFoundException($"Geometry group '{key}' was not found.");
            groups[key] = current.Concat(ValidateIds(geometryIds)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }, cancellationToken);

    public Task RemoveAsync(string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default)
        => MutateAsync(groups =>
        {
            var key = NormalizeName(name);
            if (!groups.TryGetValue(key, out var current)) throw new KeyNotFoundException($"Geometry group '{key}' was not found.");
            var removed = ValidateIds(geometryIds).ToHashSet(StringComparer.Ordinal);
            groups[key] = current.Where(id => !removed.Contains(id)).ToArray();
        }, cancellationToken);

    public async Task ExportSetAsync(string path, string name, IReadOnlyList<string> geometryIds, CancellationToken cancellationToken = default)
    {
        var requested = ValidateIds(geometryIds).ToHashSet(StringComparer.Ordinal);
        var geometry = _documents.Documents.Where(document => requested.Contains(document.GeometryId)).ToArray();
        if (geometry.Length != requested.Count) throw new InvalidOperationException("One or more selected geometry objects no longer exist.");
        var groups = Groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Where(requested.Contains).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var collection = new GeometryCollection { Name = NormalizeName(name), Geometry = geometry, Groups = groups };
        var destination = ValidateWritableExternalPath(path);
        if (GeometryLibraryPaths.IsContained(_documents.RootPath, destination))
            throw new InvalidOperationException("Geometry sets must be exported outside the managed local geometry library.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await WriteAtomicAsync(destination, JsonSerializer.Serialize(collection, JsonOptions), cancellationToken);
    }

    public async Task ImportSetAsync(string path, bool replace, CancellationToken cancellationToken = default)
    {
        var source = ValidateReadableExternalPath(path);
        var json = await File.ReadAllTextAsync(source, cancellationToken);
        var collection = JsonSerializer.Deserialize<GeometryCollection>(json, JsonOptions)
            ?? throw new InvalidDataException("Geometry set was empty.");
        if (!string.Equals(collection.SchemaVersion, GeometryCollection.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported geometry set schema '{collection.SchemaVersion}'.");
        foreach (var document in collection.Geometry)
        {
            if (_documents.TryGet(document.GeometryId, out _) && !replace)
                throw new InvalidOperationException($"Geometry document '{document.GeometryId}' already exists in the local library.");
            await _documents.UpsertAsync(document with { Origin = GeometryDocumentOrigin.Imported }, cancellationToken);
        }
        await MutateAsync(groups =>
        {
            foreach (var pair in collection.Groups)
            {
                var group = NormalizeName(pair.Key);
                var members = ValidateIds(pair.Value).ToArray();
                if (groups.TryGetValue(group, out var current) && !replace)
                    groups[group] = current.Concat(members).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                else groups[group] = members;
            }
        }, cancellationToken);
    }

    private async Task MutateAsync(Action<Dictionary<string, string[]>> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, string[]> copy;
            lock (_stateGate) copy = _groups.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            mutation(copy);
            await SaveAsync(copy, cancellationToken);
            lock (_stateGate) _groups = copy;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCore()
    {
        var path = ManifestPath;
        if (!File.Exists(path)) return;
        var manifest = JsonSerializer.Deserialize<GeometryGroupManifest>(File.ReadAllText(path), JsonOptions);
        if (manifest is null || !string.Equals(manifest.SchemaVersion, GeometryGroupManifest.CurrentSchemaVersion, StringComparison.Ordinal)) return;
        var validIds = _documents.Documents.Select(item => item.GeometryId).ToHashSet(StringComparer.Ordinal);
        var groups = manifest.Groups.ToDictionary(
            pair => NormalizeName(pair.Key),
            pair => ValidateIds(pair.Value).Where(validIds.Contains).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        lock (_stateGate) _groups = groups;
    }

    private async Task SaveAsync(Dictionary<string, string[]> groups, CancellationToken cancellationToken)
    {
        var manifest = new GeometryGroupManifest { Groups = groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase) };
        await WriteAtomicAsync(ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
    }

    private string ManifestPath => Path.Combine(_documents.RootPath, ManifestFileName);

    private static string ValidateReadableExternalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A geometry-set file path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Geometry-set file was not found.", fullPath);
        if (Directory.Exists(fullPath)) throw new InvalidOperationException("A geometry-set file, not a directory, is required.");
        return fullPath;
    }

    private static string ValidateWritableExternalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A geometry-set export path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath)) throw new InvalidOperationException("A geometry-set file path, not a directory, is required.");
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("A writable geometry-set export path is required.");
        return fullPath;
    }
    public void Dispose() => _gate.Dispose();

    private static string NormalizeName(string? name)
    {
        var result = name?.Trim() ?? string.Empty;
        if (result.Length is < 1 or > 96) throw new ArgumentException("A geometry group name must contain 1 to 96 characters.", nameof(name));
        return result;
    }
    private static IEnumerable<string> ValidateIds(IEnumerable<string>? ids) => (ids ?? [])
        .Where(GeometryLibraryPaths.IsValidGeometryId).Select(id => id.Trim()).Distinct(StringComparer.Ordinal);
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }
    private void OnDocumentsChanged(object? sender, EventArgs e) => _ = RefreshAsync();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}
