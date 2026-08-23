using System.Security.Cryptography;
using System.Text.Json;
using RobotCommand.Models;

namespace RobotCommand.Services.Evidence;

public sealed class EvidenceLibrary : IEvidenceLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootPath;

    public EvidenceLibrary(AppConfiguration configuration)
    {
        _rootPath = Path.GetFullPath(configuration.EvidenceLibraryPath);
        Snapshot = EvidenceLibrarySnapshot.Empty(_rootPath);
    }

    public event EventHandler? Changed;

    public EvidenceLibrarySnapshot Snapshot { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_rootPath);
            var records = new List<EvidenceRecord>();
            foreach (var metadataPath in Directory.EnumerateFiles(
                         _rootPath,
                         "*.evidence.json",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await ReadRecordAsync(metadataPath, cancellationToken);
                if (record is not null)
                {
                    records.Add(record);
                }
            }

            Snapshot = new EvidenceLibrarySnapshot(
                records.OrderByDescending(item => item.CreatedAt).ToArray(),
                records.Sum(item => item.SizeBytes),
                _rootPath,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<EvidenceRecord> CaptureDisplayedFrameAsync(
        DisplayedFrameCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var png = PngFrameEncoder.EncodeBgra(
            request.BgraPixels,
            request.Frame,
            request.Overlay,
            request.IncludeOverlays);
        var createdAt = DateTimeOffset.UtcNow;
        var id = EvidenceManifest.CreateId(EvidenceKind.DisplayedFrame, createdAt, request.Context);
        var stem = BuildStem(createdAt, "frame", request.Context, id);

        return await WriteEvidenceAsync(
            id,
            EvidenceKind.DisplayedFrame,
            BuildTitle("Displayed frame", request.Context),
            stem + ".png",
            "image/png",
            png,
            createdAt,
            request.Frame.Timestamp,
            request.Frame.Timestamp,
            request.IncludeOverlays,
            request.Context with { MediaTimestamp = request.Frame.Timestamp },
            cancellationToken);
    }

    public async Task<EvidenceRecord> ExportClipAsync(
        VideoClipExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourcePath = Path.GetFullPath(request.SourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected recording is not available locally.", sourcePath);
        }
        var extension = NormalizeExtension(
            request.PreferredExtension ?? Path.GetExtension(sourcePath));
        var createdAt = DateTimeOffset.UtcNow;
        var id = EvidenceManifest.CreateId(EvidenceKind.VideoClip, createdAt, request.Context);
        var stem = BuildStem(createdAt, "clip", request.Context, id);
        var fileName = stem + extension;

        byte[] bytes;
        await using (var input = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 128,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (input.Length <= 0)
            {
                throw new InvalidDataException("The selected recording is empty.");
            }
            if (input.Length > int.MaxValue)
            {
                return await CopyLargeClipAsync(
                    id,
                    fileName,
                    sourcePath,
                    createdAt,
                    request,
                    cancellationToken);
            }
            bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
        }

        return await WriteEvidenceAsync(
            id,
            EvidenceKind.VideoClip,
            BuildTitle("Video clip", request.Context),
            fileName,
            MediaTypeFor(extension),
            bytes,
            createdAt,
            request.StartedAt,
            request.EndedAt,
            false,
            request.Context,
            cancellationToken);
    }

    public async Task DeleteAsync(string evidenceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evidenceId)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = Snapshot.Items.FirstOrDefault(item => item.Id == evidenceId);
            if (record is null)
            {
                return;
            }
            DeleteContained(record.FilePath);
            DeleteContained(record.MetadataPath);
        }
        finally
        {
            _gate.Release();
        }
        await RefreshAsync(cancellationToken);
    }

    private async Task<EvidenceRecord> WriteEvidenceAsync(
        string id,
        EvidenceKind kind,
        string title,
        string fileName,
        string mediaType,
        byte[] bytes,
        DateTimeOffset createdAt,
        DateTimeOffset? mediaStartedAt,
        DateTimeOffset? mediaEndedAt,
        bool includesOverlays,
        EvidenceCaptureContext context,
        CancellationToken cancellationToken)
    {
        EvidenceRecord record;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_rootPath);
            var finalPath = ContainedPath(fileName);
            var metadataPath = ContainedPath(Path.GetFileNameWithoutExtension(fileName) + ".evidence.json");
            var tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var tempMetadata = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var mediaInstalled = false;
            var metadataInstalled = false;
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var manifest = new EvidenceManifest(
                    EvidenceManifest.CurrentSchema,
                    id,
                    kind,
                    title,
                    Path.GetFileName(finalPath),
                    mediaType,
                    bytes.LongLength,
                    hash,
                    createdAt,
                    mediaStartedAt,
                    mediaEndedAt,
                    includesOverlays,
                    context);
                await File.WriteAllTextAsync(
                    tempMetadata,
                    JsonSerializer.Serialize(manifest, JsonOptions),
                    cancellationToken);
                File.Move(tempPath, finalPath, overwrite: false);
                mediaInstalled = true;
                File.Move(tempMetadata, metadataPath, overwrite: false);
                metadataInstalled = true;
                record = ToRecord(manifest, finalPath, metadataPath);
                Snapshot = new EvidenceLibrarySnapshot(
                    [record, .. Snapshot.Items.Where(item => item.Id != id)],
                    Snapshot.TotalBytes + record.SizeBytes,
                    _rootPath,
                    DateTimeOffset.UtcNow);
            }
            catch
            {
                if (metadataInstalled) TryDelete(metadataPath);
                if (mediaInstalled) TryDelete(finalPath);
                throw;
            }
            finally
            {
                TryDelete(tempPath);
                TryDelete(tempMetadata);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    private async Task<EvidenceRecord> CopyLargeClipAsync(
        string id,
        string fileName,
        string sourcePath,
        DateTimeOffset createdAt,
        VideoClipExportRequest request,
        CancellationToken cancellationToken)
    {
        EvidenceRecord record;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_rootPath);
            var finalPath = ContainedPath(fileName);
            var metadataPath = ContainedPath(Path.GetFileNameWithoutExtension(fileName) + ".evidence.json");
            var tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var tempMetadata = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var mediaInstalled = false;
            var metadataInstalled = false;
            try
            {
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    hasher.AppendData(buffer, 0, count);
                }
                await output.FlushAsync(cancellationToken);
                var info = new FileInfo(tempPath);
                var manifest = new EvidenceManifest(
                    EvidenceManifest.CurrentSchema,
                    id,
                    EvidenceKind.VideoClip,
                    BuildTitle("Video clip", request.Context),
                    Path.GetFileName(finalPath),
                    MediaTypeFor(Path.GetExtension(finalPath)),
                    info.Length,
                    Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
                    createdAt,
                    request.StartedAt,
                    request.EndedAt,
                    false,
                    request.Context);
                await File.WriteAllTextAsync(tempMetadata, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
                File.Move(tempPath, finalPath, overwrite: false);
                mediaInstalled = true;
                File.Move(tempMetadata, metadataPath, overwrite: false);
                metadataInstalled = true;
                record = ToRecord(manifest, finalPath, metadataPath);
                Snapshot = new EvidenceLibrarySnapshot(
                    [record, .. Snapshot.Items.Where(item => item.Id != id)],
                    Snapshot.TotalBytes + record.SizeBytes,
                    _rootPath,
                    DateTimeOffset.UtcNow);
            }
            catch
            {
                if (metadataInstalled) TryDelete(metadataPath);
                if (mediaInstalled) TryDelete(finalPath);
                throw;
            }
            finally
            {
                TryDelete(tempPath);
                TryDelete(tempMetadata);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    private async Task<EvidenceRecord?> ReadRecordAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(metadataPath);
            var manifest = await JsonSerializer.DeserializeAsync<EvidenceManifest>(stream, JsonOptions, cancellationToken);
            if (manifest is null ||
                manifest.Schema != EvidenceManifest.CurrentSchema ||
                string.IsNullOrWhiteSpace(manifest.Id) ||
                string.IsNullOrWhiteSpace(manifest.FileName) ||
                Path.GetFileName(manifest.FileName) != manifest.FileName ||
                manifest.SizeBytes < 0 ||
                manifest.Context is null ||
                !IsSha256(manifest.Sha256))
            {
                return null;
            }
            var filePath = ContainedPath(manifest.FileName);
            if (!File.Exists(filePath)) return null;
            var info = new FileInfo(filePath);
            if (info.Length != manifest.SizeBytes) return null;
            return ToRecord(manifest, filePath, Path.GetFullPath(metadataPath));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private EvidenceRecord ToRecord(EvidenceManifest manifest, string filePath, string metadataPath)
        => new(
            manifest.Id,
            manifest.Kind,
            manifest.Title,
            filePath,
            metadataPath,
            manifest.FileName,
            manifest.MediaType,
            manifest.SizeBytes,
            manifest.Sha256,
            manifest.CreatedAt,
            manifest.MediaStartedAt,
            manifest.MediaEndedAt,
            manifest.IncludesOverlays,
            manifest.Context);

    private string ContainedPath(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(_rootPath, fileName));
        var prefix = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The evidence path escapes the configured library directory.");
        }
        return path;
    }

    private void DeleteContained(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The evidence path escapes the configured library directory.");
        }
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string BuildStem(
        DateTimeOffset createdAt,
        string kind,
        EvidenceCaptureContext context,
        string id)
    {
        var vehicle = Sanitize(context.VehicleName ?? context.VehicleId ?? "unknown-vehicle");
        var camera = Sanitize(context.CameraSourceId ?? "unknown-camera");
        return $"{createdAt.UtcDateTime:yyyyMMddTHHmmssfffZ}_{kind}_{vehicle}_{camera}_{id[^8..]}";
    }

    private static string BuildTitle(string prefix, EvidenceCaptureContext context)
    {
        var vehicle = context.VehicleName ?? context.VehicleId ?? "unknown vehicle";
        var camera = context.CameraSourceId ?? "unknown camera";
        return $"{prefix} · {vehicle} · {camera}";
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(character =>
            invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized[..Math.Min(normalized.Length, 48)];
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return ".mkv";
        var value = extension.StartsWith('.') ? extension : "." + extension;
        return value.ToLowerInvariant() switch
        {
            ".mp4" => ".mp4",
            ".mkv" => ".mkv",
            ".webm" => ".webm",
            ".ts" => ".ts",
            _ => ".bin"
        };
    }

    private static string MediaTypeFor(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" => "video/mp2t",
            _ => "application/octet-stream"
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}
