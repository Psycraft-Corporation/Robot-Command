using System.Text.Json;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Media;

public sealed class LocalVideoRecordingCatalog : ILocalVideoRecordingCatalog, IDisposable
{
    public const string SessionSchema = "logos.local-video-session.v1";
    private const string ManifestFileName = "session.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppConfiguration _configuration;
    private readonly ILogger<LocalVideoRecordingCatalog> _logger;

    public LocalVideoRecordingCatalog(
        AppConfiguration configuration,
        ILogger<LocalVideoRecordingCatalog> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string RootPath => _configuration.LocalVideoRecordingPath;

    public async Task WriteSessionAsync(
        string sessionDirectory,
        LocalVideoSessionManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var directory = EnsureContainedDirectory(sessionDirectory);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ManifestFileName);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    Json,
                    cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalVideoSegment>> LoadAsync(
        LocalVideoCatalogContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(context, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalVideoSegment>> CleanupAndLoadAsync(
        LocalVideoCatalogContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var segments = await LoadCoreAsync(context, cancellationToken);
            CleanupCore(segments, context);
            return await LoadCoreAsync(context, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RetainAsync(
        LocalVideoSegment segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!segment.Finalized)
        {
            throw new InvalidOperationException("Only finalized local video segments can be retained.");
        }

        var path = EnsureContainedFile(segment.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local video segment no longer exists.", path);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(
                RetainedMarker(path),
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<IReadOnlyList<LocalVideoSegment>> LoadCoreAsync(
        LocalVideoCatalogContext context,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootPath))
        {
            return [];
        }

        var candidates = new List<LocalVideoSegment>();
        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(RootPath, ManifestFileName, SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate the local video recording directory {Directory}", RootPath);
            return [];
        }

        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalVideoSessionManifest? manifest;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<LocalVideoSessionManifest>(
                    stream,
                    Json,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogDebug(ex, "Ignoring invalid local video session manifest {Manifest}", manifestPath);
                continue;
            }

            if (manifest is null || manifest.Schema != SessionSchema ||
                manifest.Width <= 0 || manifest.Height <= 0 || manifest.FrameRate <= 0 || manifest.SegmentSeconds <= 0)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(manifestPath)!;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(directory, "segment-*.mkv", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not enumerate local video session {Directory}", directory);
                continue;
            }

            for (var i = 0; i < files.Length; i++)
            {
                var path = files[i];
                var index = ParseSegmentIndex(path, i);
                var nominalStart = manifest.StartedAt + TimeSpan.FromSeconds((long)index * manifest.SegmentSeconds);
                var file = new FileInfo(path);
                var fileEnd = file.Exists
                    ? new DateTimeOffset(file.LastWriteTimeUtc)
                    : nominalStart.AddSeconds(manifest.SegmentSeconds);
                var endedAt = fileEnd > nominalStart
                    ? fileEnd
                    : nominalStart.AddSeconds(manifest.SegmentSeconds);
                var startedAt = endedAt - TimeSpan.FromSeconds(manifest.SegmentSeconds);
                var isActiveSession = string.Equals(
                    manifest.SessionId,
                    context.ActiveSessionId,
                    StringComparison.Ordinal);
                var stable = file.Exists &&
                    DateTimeOffset.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(2);
                var finalized = HasMatroskaHeader(path) && stable &&
                    (i < files.Length - 1 || !isActiveSession ||
                     context.RecorderState is not LocalVideoRecorderState.Recording);
                candidates.Add(new LocalVideoSegment(
                    $"{manifest.SessionId}:{index:D5}",
                    manifest.SessionId,
                    path,
                    manifest.CameraSourceId,
                    manifest.StreamId,
                    manifest.Protocol,
                    startedAt,
                    endedAt,
                    manifest.Width,
                    manifest.Height,
                    manifest.FrameRate,
                    file.Exists ? file.Length : 0,
                    File.Exists(RetainedMarker(path)),
                    finalized,
                    false));
            }
        }

        var ordered = candidates.OrderBy(item => item.StartedAt).ToArray();
        var result = new LocalVideoSegment[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var gap = i > 0 && ordered[i].StartedAt - ordered[i - 1].EndedAt > TimeSpan.FromSeconds(2);
            result[i] = ordered[i] with { GapBefore = gap };
        }
        return result;
    }

    private void CleanupCore(
        IReadOnlyList<LocalVideoSegment> segments,
        LocalVideoCatalogContext context)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(_configuration.LocalVideoRollingBufferMinutes);
        foreach (var segment in segments.Where(item =>
                     item.Finalized && !item.Retained &&
                     item.Id != context.ProtectedSegmentId && item.EndedAt < cutoff))
        {
            TryDeleteSegment(segment);
        }

        var remaining = segments.Where(item => File.Exists(item.Path)).ToArray();
        var maximumBytes = (long)_configuration.LocalVideoMaximumStorageGigabytes * 1024L * 1024L * 1024L;
        var currentBytes = remaining.Sum(item => item.SizeBytes);
        foreach (var segment in remaining.Where(item =>
                     item.Finalized && !item.Retained && item.Id != context.ProtectedSegmentId)
                 .OrderBy(item => item.StartedAt))
        {
            if (currentBytes <= maximumBytes)
            {
                break;
            }
            if (TryDeleteSegment(segment))
            {
                currentBytes -= segment.SizeBytes;
            }
        }

        DeleteEmptySessionDirectories(context.ActiveSessionDirectory);
    }

    private bool TryDeleteSegment(LocalVideoSegment segment)
    {
        try
        {
            File.Delete(segment.Path);
            var marker = RetainedMarker(segment.Path);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove expired local video segment {Segment}", segment.Path);
            return false;
        }
    }

    private void DeleteEmptySessionDirectories(string? activeSessionDirectory)
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            if (string.Equals(directory, activeSessionDirectory, StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFiles(directory, "segment-*.mkv").Any())
            {
                continue;
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not remove empty local video session directory {Directory}", directory);
            }
        }
    }

    private string EnsureContainedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureContained(fullPath);
        return fullPath;
    }

    private string EnsureContainedFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureContained(fullPath);
        return fullPath;
    }

    private void EnsureContained(string fullPath)
    {
        var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root, comparison))
        {
            throw new InvalidOperationException("The local video path is outside the configured recording directory.");
        }
    }

    private static bool HasMatroskaHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.Read(header) == header.Length &&
                   header[0] == 0x1A && header[1] == 0x45 &&
                   header[2] == 0xDF && header[3] == 0xA3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ParseSegmentIndex(string path, int fallback)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var suffix = name.Split('-').LastOrDefault();
        return int.TryParse(suffix, out var parsed) ? parsed : fallback;
    }

    private static string RetainedMarker(string path) => path + ".retained";
}
