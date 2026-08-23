using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobotCommand.Core;

namespace RobotCommand.Models;

public sealed class FormationLibraryStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private IReadOnlyList<FormationDocument> _documents = [];
    private readonly List<string> _issues = [];

    public FormationLibraryStore(string baseDirectory)
    {
        _root = Path.Combine(baseDirectory, "data", "formations");
        Directory.CreateDirectory(_root);
        Refresh();
    }

    public IReadOnlyList<FormationDocument> Documents => _documents;
    public IReadOnlyList<string> Issues => _issues;
    public event EventHandler? Changed;
    public bool TryGet(string id, out FormationDocument? document)
    {
        document = _documents.FirstOrDefault(item => item.FormationId == id);
        return document is not null;
    }

    public async Task<FormationDocument> SaveAsync(FormationDocument document, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        Validate(document, allowEmpty);
        await _gate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            var path = PathFor(document.FormationId);
            var canonical = document with
            {
                SchemaVersion = FormationDocument.CurrentSchemaVersion,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            temporary = Path.Combine(_root, $".{document.FormationId}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(canonical, Json), Encoding.UTF8, cancellationToken);
            File.Move(temporary, path, true);
            temporary = null;
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
            return canonical;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public static void Validate(FormationDocument document, bool allowEmpty = false)
    {
        ValidateId(document.FormationId);
        if (document.SchemaVersion != FormationDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported formation schema '{document.SchemaVersion}'.");
        ValidateName(document.DisplayName);
        if (!allowEmpty && document.Members.Count == 0) throw new InvalidDataException("A formation must contain at least one unit.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in document.Members)
        {
            ValidateId(member.Id);
            if (!ids.Add(member.Id)) throw new InvalidDataException($"Formation member ID '{member.Id}' is duplicated.");
            ValidateName(member.Name, "Unit name");
            if (!names.Add(member.Name)) throw new InvalidDataException($"Formation unit name '{member.Name}' is duplicated.");
            if (!double.IsFinite(member.EastMetres) || !double.IsFinite(member.UpMetres) || !double.IsFinite(member.NorthMetres) ||
                Math.Abs(member.EastMetres) > 100_000 || Math.Abs(member.UpMetres) > 100_000 || Math.Abs(member.NorthMetres) > 100_000)
                throw new InvalidDataException($"Formation member '{member.Name}' has invalid coordinates.");
        }
    }

    private void Refresh()
    {
        _issues.Clear();
        var loaded = new List<FormationDocument>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.formation.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = JsonSerializer.Deserialize<FormationDocument>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Empty document.");
                Validate(document, allowEmpty: true);
                loaded.Add(document);
            }
            catch (Exception exception)
            {
                _issues.Add($"Ignored formation '{Path.GetFileName(path)}': {exception.Message}");
            }
        }
        var duplicates = loaded.GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
        foreach (var duplicate in duplicates.Skip(1)) _issues.Add($"Ignored duplicate formation name '{duplicate.Key}'.");
        _documents = loaded
            .GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose() => _gate.Dispose();

    private string PathFor(string id) => Path.Combine(_root, $"{id}.formation.json");
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(value => !char.IsLetterOrDigit(value) && value is not '-' and not '_'))
            throw new InvalidDataException("Formation IDs must contain only letters, numbers, hyphens, or underscores.");
    }
    private static void ValidateName(string name, string label = "Formation name")
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) throw new InvalidDataException($"{label} is required and must be 120 characters or fewer.");
    }
}
