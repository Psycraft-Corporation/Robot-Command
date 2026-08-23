using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryDocumentStore : IGeometryDocumentStore
{
    private const long MaximumDocumentBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromDays(1);

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly GeometryDocumentCodec _codec;
    private readonly GeometryDocumentValidator _validator;
    private readonly ILogger<GeometryDocumentStore> _logger;
    private GeometryDocument[] _documents = [];
    private GeometryLibraryIssue[] _issues = [];

    public GeometryDocumentStore(
        GeometryDocumentCodec codec,
        GeometryDocumentValidator validator,
        ILogger<GeometryDocumentStore> logger)
        : this(GeometryLibraryPaths.DefaultRootPath(), codec, validator, logger)
    {
    }

    public GeometryDocumentStore(
        string rootPath,
        GeometryDocumentCodec? codec = null,
        GeometryDocumentValidator? validator = null,
        ILogger<GeometryDocumentStore>? logger = null)
    {
        RootPath = GeometryLibraryPaths.NormalizeRoot(rootPath);
        DocumentsPath = GeometryLibraryPaths.DocumentsPath(RootPath);
        StagingPath = GeometryLibraryPaths.StagingPath(RootPath);
        _codec = codec ?? new GeometryDocumentCodec();
        _validator = validator ?? new GeometryDocumentValidator(_codec);
        _logger = logger ?? NullLogger<GeometryDocumentStore>.Instance;

        Directory.CreateDirectory(RootPath);
        GeometryLibraryPaths.EnsureNotSymbolicLink(RootPath);
        Directory.CreateDirectory(DocumentsPath);
        Directory.CreateDirectory(StagingPath);
        GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(RootPath, DocumentsPath);
        GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(RootPath, StagingPath);
        RefreshCore();
    }

    public event EventHandler? Changed;

    public string RootPath { get; }

    public string DocumentsPath { get; }

    public string StagingPath { get; }

    public IReadOnlyList<GeometryDocument> Documents
    {
        get
        {
            lock (_stateGate)
            {
                return _documents.ToArray();
            }
        }
    }

    public IReadOnlyList<GeometryLibraryIssue> Issues
    {
        get
        {
            lock (_stateGate)
            {
                return _issues.ToArray();
            }
        }
    }

    public bool TryGet(string geometryId, out GeometryDocument? document)
    {
        if (string.IsNullOrWhiteSpace(geometryId))
        {
            document = null;
            return false;
        }

        lock (_stateGate)
        {
            document = _documents.FirstOrDefault(item =>
                string.Equals(item.GeometryId, geometryId.Trim(), StringComparison.Ordinal));
            return document is not null;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCore(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }

        RaiseChanged();
    }

    public async Task<GeometryDocument> UpsertAsync(
        GeometryDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        GeometryDocument saved;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            saved = await UpsertCoreAsync(document, allowReplace: true, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }

        RaiseChanged();
        return saved;
    }

    public async Task<GeometryDocument> ImportAsync(
        string path,
        bool allowReplace = false,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ValidateReadableExternalPath(path);
        var json = await ReadDocumentTextAsync(sourcePath, cancellationToken);
        var imported = _codec.Deserialize(json) with
        {
            Origin = GeometryDocumentOrigin.Imported,
            IsDirty = true
        };

        GeometryDocument saved;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            saved = await UpsertCoreAsync(imported, allowReplace, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }

        RaiseChanged();
        return saved;
    }

    public async Task ExportAsync(
        string geometryId,
        string path,
        CancellationToken cancellationToken = default)
    {
        GeometryLibraryPaths.ValidateGeometryId(geometryId);
        GeometryDocument document;
        lock (_stateGate)
        {
            document = _documents.FirstOrDefault(item =>
                string.Equals(item.GeometryId, geometryId.Trim(), StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Geometry document '{geometryId}' was not found.");
        }

        var destinationPath = ValidateWritableExternalPath(path);
        if (GeometryLibraryPaths.IsContained(RootPath, destinationPath))
        {
            throw new InvalidOperationException(
                "Geometry exports must be written outside the managed local geometry library.");
        }
        var content = _codec.Serialize(document);
        await WriteAtomicallyAsync(destinationPath, content, useLibraryStaging: false, cancellationToken);
    }

    public async Task RemoveAsync(
        string geometryId,
        CancellationToken cancellationToken = default)
    {
        GeometryLibraryPaths.ValidateGeometryId(geometryId);
        await _operationGate.WaitAsync(cancellationToken);
        var changed = false;
        try
        {
            var normalizedId = geometryId.Trim();
            var path = GeometryLibraryPaths.DocumentPath(DocumentsPath, normalizedId);
            GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(DocumentsPath, path);
            if (File.Exists(path))
            {
                File.Delete(path);
                changed = true;
            }

            lock (_stateGate)
            {
                var replacement = _documents
                    .Where(item => !string.Equals(item.GeometryId, normalizedId, StringComparison.Ordinal))
                    .ToArray();
                changed |= replacement.Length != _documents.Length;
                _documents = replacement;
                _issues = _issues
                    .Where(item => !string.Equals(item.GeometryId, normalizedId, StringComparison.Ordinal) &&
                                   !string.Equals(item.Path, path, StringComparison.Ordinal))
                    .ToArray();
            }
        }
        finally
        {
            _operationGate.Release();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private async Task<GeometryDocument> UpsertCoreAsync(
        GeometryDocument document,
        bool allowReplace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = _codec.Normalize(document);
        ValidatePersistableIdentity(normalized);
        var targetPath = GeometryLibraryPaths.DocumentPath(DocumentsPath, normalized.GeometryId);
        GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(DocumentsPath, targetPath);
        if (!allowReplace && File.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"Geometry document '{normalized.GeometryId}' already exists in the local library.");
        }

        var prepared = _codec.PrepareForSave(normalized);
        var content = _codec.Serialize(prepared);
        await WriteAtomicallyAsync(targetPath, content, useLibraryStaging: true, cancellationToken);
        UpdateAfterWrite(prepared, targetPath);
        return prepared;
    }

    private void RefreshCore(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DocumentsPath);
        Directory.CreateDirectory(StagingPath);
        GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(RootPath, DocumentsPath);
        GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(RootPath, StagingPath);
        CleanupAbandonedStagingFiles();

        var documents = new Dictionary<string, GeometryDocument>(StringComparer.Ordinal);
        var issues = new List<GeometryLibraryIssue>();
        var abandonedLegacyDrafts = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     DocumentsPath,
                     $"*{GeometryLibraryPaths.DocumentFileExtension}",
                     SearchOption.TopDirectoryOnly)
                 .OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Prior to native Robot Command geometry authoring, opening a
                // create tool wrote an empty Logos-format LocalDraft straight
                // into the library. Those files cannot represent a PoI,
                // route, or zone and have no geometry data to preserve. Do
                // not upgrade them into visible library records.
                if (IsAbandonedLegacyDraft(path))
                {
                    abandonedLegacyDrafts.Add(path);
                    continue;
                }

                LoadOne(path, documents, issues);
            }
            catch (Exception ex) when (ex is
                       IOException or
                       UnauthorizedAccessException or
                       InvalidDataException or
                       InvalidOperationException or
                       ArgumentException)
            {
                _logger.LogWarning(ex, "Could not load local geometry document {Path}", path);
                issues.Add(new GeometryLibraryIssue(
                    "GEOMETRY_LIBRARY_LOAD_FAILED",
                    GeometryValidationSeverity.Error,
                    ex.Message,
                    path));
            }
        }

        foreach (var path in abandonedLegacyDrafts)
        {
            try
            {
                GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(DocumentsPath, path);
                File.Delete(path);
                _logger.LogInformation("Removed abandoned legacy geometry draft {Path}", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The document remains hidden even if it cannot be removed
                // immediately; a later refresh can retry the cleanup.
                _logger.LogWarning(ex, "Could not remove abandoned legacy geometry draft {Path}", path);
            }
        }

        lock (_stateGate)
        {
            _documents = Order(documents.Values);
            _issues = OrderIssues(issues);
        }
    }

    private void LoadOne(
        string path,
        IDictionary<string, GeometryDocument> documents,
        ICollection<GeometryLibraryIssue> issues)
    {
        GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(DocumentsPath, path);
        if (!GeometryLibraryPaths.TryGetGeometryId(path, out var fileGeometryId))
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_LIBRARY_FILENAME_INVALID",
                GeometryValidationSeverity.Error,
                "The file name does not use the canonical geometry library naming convention.",
                path));
            return;
        }

        var json = ReadDocumentText(path);
        var document = _codec.Deserialize(json);
        if (!string.Equals(document.SchemaVersion, GeometryDocument.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_SCHEMA_UNSUPPORTED",
                GeometryValidationSeverity.Error,
                $"schemaVersion must be '{GeometryDocument.CurrentSchemaVersion}'.",
                path,
                document.GeometryId,
                "schemaVersion"));
            return;
        }

        if (!GeometryLibraryPaths.IsValidGeometryId(document.GeometryId))
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_ID_INVALID",
                GeometryValidationSeverity.Error,
                "The geometry document contains an invalid geometryId.",
                path,
                document.GeometryId,
                "geometryId"));
            return;
        }

        if (!string.Equals(fileGeometryId, document.GeometryId, StringComparison.Ordinal))
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_LIBRARY_ID_FILENAME_MISMATCH",
                GeometryValidationSeverity.Error,
                $"File name identifies '{fileGeometryId}' but the document identifies '{document.GeometryId}'.",
                path,
                document.GeometryId,
                "geometryId"));
            return;
        }

        if (!Enum.IsDefined(document.Kind) || document.Kind == GeometryDocumentKind.Unknown)
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_KIND_INVALID",
                GeometryValidationSeverity.Error,
                "The geometry document contains an unsupported kind.",
                path,
                document.GeometryId,
                "kind"));
            return;
        }

        if (!Enum.IsDefined(document.Frame) || document.Frame == GeometryCoordinateFrame.Unknown)
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_FRAME_INVALID",
                GeometryValidationSeverity.Error,
                "The geometry document contains an unsupported coordinate frame.",
                path,
                document.GeometryId,
                "frame"));
            return;
        }

        if (documents.ContainsKey(document.GeometryId))
        {
            issues.Add(new GeometryLibraryIssue(
                "GEOMETRY_LIBRARY_DUPLICATE_ID",
                GeometryValidationSeverity.Error,
                $"More than one local document uses geometryId '{document.GeometryId}'.",
                path,
                document.GeometryId,
                "geometryId"));
            return;
        }

        var validation = _validator.Validate(document, requireAuthorableFrame: false);
        var dirty = validation.Issues.Any(item => item.Code is
            "GEOMETRY_HASH_MISSING" or "GEOMETRY_HASH_INVALID" or "GEOMETRY_HASH_MISMATCH");
        document = document with { IsDirty = dirty };
        documents.Add(document.GeometryId, document);
        AddValidationIssues(path, document.GeometryId, validation, issues);
    }

    private static bool IsAbandonedLegacyDraft(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(ReadDocumentText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                !string.Equals(
                    schema.GetString(),
                    GeometryDocument.LegacyLogosSchemaVersion,
                    StringComparison.Ordinal) ||
                !json.RootElement.TryGetProperty("origin", out var origin) ||
                !string.Equals(origin.GetString(), "LocalDraft", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsEmptyArray(json.RootElement, "points") &&
                   IsEmptyArray(json.RootElement, "rings");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEmptyArray(JsonElement root, string propertyName)
        => !root.TryGetProperty(propertyName, out var value) ||
           value.ValueKind == JsonValueKind.Null ||
           (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private void UpdateAfterWrite(GeometryDocument document, string path)
    {
        var validation = _validator.Validate(document, requireAuthorableFrame: false);
        lock (_stateGate)
        {
            var documents = _documents.ToDictionary(item => item.GeometryId, StringComparer.Ordinal);
            documents[document.GeometryId] = document;
            _documents = Order(documents.Values);

            var issues = _issues
                .Where(item => !string.Equals(item.GeometryId, document.GeometryId, StringComparison.Ordinal) &&
                               !string.Equals(item.Path, path, StringComparison.Ordinal))
                .ToList();
            AddValidationIssues(path, document.GeometryId, validation, issues);
            _issues = OrderIssues(issues);
        }
    }

    private static void AddValidationIssues(
        string path,
        string geometryId,
        GeometryValidationResult validation,
        ICollection<GeometryLibraryIssue> destination)
    {
        foreach (var issue in validation.Issues)
        {
            destination.Add(new GeometryLibraryIssue(
                issue.Code,
                issue.Severity,
                issue.Message,
                path,
                geometryId,
                issue.FieldPath));
        }
    }

    private async Task WriteAtomicallyAsync(
        string targetPath,
        string content,
        bool useLibraryStaging,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath)
                              ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(targetDirectory);
        GeometryLibraryPaths.EnsureNotSymbolicLink(targetDirectory);
        ValidateAtomicWriteTarget(targetPath, useLibraryStaging);

        var temporaryDirectory = useLibraryStaging ? StagingPath : targetDirectory;
        Directory.CreateDirectory(temporaryDirectory);
        GeometryLibraryPaths.EnsureNotSymbolicLink(temporaryDirectory);
        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    16 * 1024,
                    leaveOpen: true);
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            ValidateAtomicWriteTarget(targetPath, useLibraryStaging);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void ValidateAtomicWriteTarget(string targetPath, bool libraryTarget)
    {
        if (libraryTarget)
        {
            GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(DocumentsPath, targetPath);
        }
        else
        {
            GeometryLibraryPaths.EnsureNotSymbolicLink(targetPath);
        }
    }

    private static string ValidateReadableExternalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A geometry document path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The geometry document was not found.", fullPath);
        }

        GeometryLibraryPaths.EnsureNotSymbolicLink(fullPath);
        return fullPath;
    }

    private static string ValidateWritableExternalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A geometry export path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
                     ?? throw new InvalidOperationException("The geometry export path has no parent directory.");
        Directory.CreateDirectory(parent);
        GeometryLibraryPaths.EnsureNotSymbolicLink(parent);
        GeometryLibraryPaths.EnsureNotSymbolicLink(fullPath);
        return fullPath;
    }

    private static void ValidatePersistableIdentity(GeometryDocument document)
    {
        if (!string.Equals(document.SchemaVersion, GeometryDocument.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"schemaVersion must be '{GeometryDocument.CurrentSchemaVersion}'.");
        }

        GeometryLibraryPaths.ValidateGeometryId(document.GeometryId);
        if (string.IsNullOrWhiteSpace(document.DisplayName))
        {
            throw new InvalidDataException("displayName is required before a geometry draft can be saved.");
        }

        if (!Enum.IsDefined(document.Kind) || document.Kind == GeometryDocumentKind.Unknown)
        {
            throw new InvalidDataException("kind must identify a supported geometry kind.");
        }

        if (!Enum.IsDefined(document.Frame) || document.Frame == GeometryCoordinateFrame.Unknown)
        {
            throw new InvalidDataException("frame must identify a supported coordinate frame.");
        }
    }

    private static string ReadDocumentText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Geometry document exceeds the {MaximumDocumentBytes / (1024 * 1024)} MiB local limit.");
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static async Task<string> ReadDocumentTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"Geometry document exceeds the {MaximumDocumentBytes / (1024 * 1024)} MiB local limit.");
        }

        return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
    }

    private void CleanupAbandonedStagingFiles()
    {
        var threshold = DateTimeOffset.UtcNow - AbandonedStagingAge;
        foreach (var path in Directory.EnumerateFiles(StagingPath, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            try
            {
                GeometryLibraryPaths.EnsureNoSymbolicLinksUnder(StagingPath, path);
                if (File.GetLastWriteTimeUtc(path) < threshold.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Could not remove abandoned geometry staging file {Path}", path);
            }
        }
    }

    private static GeometryDocument[] Order(IEnumerable<GeometryDocument> documents)
        => documents
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
            .ToArray();

    private static GeometryLibraryIssue[] OrderIssues(IEnumerable<GeometryLibraryIssue> issues)
        => issues
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void RaiseChanged()
        => Changed?.Invoke(this, EventArgs.Empty);
}
