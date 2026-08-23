using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Media;

public sealed class MediaMtxRemoteVideoRecordingCatalog : IRemoteVideoRecordingCatalog
{
    public const string CacheSchema = "logos.remote-video-cache.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly AppConfiguration _configuration;
    private readonly ILogger<MediaMtxRemoteVideoRecordingCatalog> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private RemoteVideoRecordingStatus _status = RemoteVideoRecordingStatus.NotConfigured;
    private IReadOnlyList<RemoteVideoRecordingSpan> _spans = [];
    private CameraStreamRecord? _stream;
    private ConnectionDefinition? _connection;
    private RemoteVideoRecordingProfile? _profile;
    private string? _mediaPath;
    private string? _playbackProtectedSpanId;
    private int _disposed;

    public MediaMtxRemoteVideoRecordingCatalog(
        AppConfiguration configuration,
        ILogger<MediaMtxRemoteVideoRecordingCatalog> logger)
        : this(configuration, logger, null)
    {
    }

    public MediaMtxRemoteVideoRecordingCatalog(
        AppConfiguration configuration,
        ILogger<MediaMtxRemoteVideoRecordingCatalog> logger,
        HttpClient? httpClient)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(configuration.RemoteVideoRequestTimeoutSeconds);
    }

    public event EventHandler? Changed;

    public RemoteVideoRecordingStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    public IReadOnlyList<RemoteVideoRecordingSpan> Spans
    {
        get
        {
            lock (_stateGate)
            {
                return _spans;
            }
        }
    }

    public async Task BeginSessionAsync(
        CameraStreamRecord stream,
        ConnectionDefinition? connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stream = stream;
            _connection = connection;
            _profile = _configuration.RemoteVideoRecordingProfiles.FirstOrDefault(item => item.Matches(connection));
            try
            {
                _mediaPath = _profile is null ? null : ExpandPath(_profile.PathTemplate, stream);
                await CleanupCacheAsync(_playbackProtectedSpanId ?? string.Empty, cancellationToken);
                await RefreshCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _mediaPath = null;
                SetSpans([]);
                SetStatus(new RemoteVideoRecordingStatus(
                    RemoteVideoRecordingState.Faulted,
                    "Vehicle recording configuration invalid",
                    GStreamerPipelineArguments.RedactText(ex.Message),
                    DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stream = null;
            _connection = null;
            _profile = null;
            _mediaPath = null;
            _playbackProtectedSpanId = null;
            SetSpans([]);
            SetStatus(RemoteVideoRecordingStatus.NotConfigured with { UpdatedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CleanupCacheAsync(_playbackProtectedSpanId ?? string.Empty, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }


    public void ProtectPlayback(string? spanId)
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            _playbackProtectedSpanId = string.IsNullOrWhiteSpace(spanId) ? null : spanId;
        }
    }

    public async Task<RemoteVideoRecordingSpan> EnsureCachedAsync(
        string spanId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var span = _spans.FirstOrDefault(item => item.Id == spanId)
                ?? throw new InvalidOperationException("The selected vehicle recording is no longer present in the current timeline.");
            if (span.Cached)
            {
                return span;
            }
            if (_profile is null)
            {
                throw new InvalidOperationException("No MediaMTX playback profile is active for this camera connection.");
            }

            SetStatus(new RemoteVideoRecordingStatus(
                RemoteVideoRecordingState.Downloading,
                "Downloading vehicle recording",
                $"Caching {span.StartedAt.ToLocalTime():g} from the vehicle playback server.",
                DateTimeOffset.UtcNow,
                _spans.Count,
                _spans.Count(item => item.Cached)));

            Directory.CreateDirectory(_configuration.RemoteVideoCachePath);
            var target = Path.Combine(_configuration.RemoteVideoCachePath, span.Id + ".mp4");
            var temporary = target + ".download";
            TryDelete(temporary);
            try
            {
                using var request = CreateRequest(HttpMethod.Get, BuildGetUri(_profile, span.MediaPath, span.StartedAt, span.DurationSeconds));
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var maximumBytes = (long)_configuration.RemoteVideoMaximumDownloadGigabytes * 1024L * 1024L * 1024L;
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > maximumBytes)
                {
                    throw new InvalidOperationException("The selected vehicle recording exceeds the configured download-size limit.");
                }

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 128];
                    long total = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }
                        total += read;
                        if (total > maximumBytes)
                        {
                            throw new InvalidOperationException("The selected vehicle recording exceeded the configured download-size limit.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }

                var file = new FileInfo(temporary);
                if (!file.Exists || file.Length == 0 || !HasMp4Header(temporary))
                {
                    throw new InvalidDataException("The MediaMTX playback server did not return a valid MP4 recording.");
                }
                File.Move(temporary, target, overwrite: true);
                await WriteCacheManifestAsync(span, target, cancellationToken);
                await CleanupCacheAsync(span.Id, cancellationToken);
                await RefreshCoreAsync(cancellationToken, queryRemote: false);
                return _spans.First(item => item.Id == span.Id);
            }
            catch (Exception ex)
            {
                TryDelete(temporary);
                SetStatus(new RemoteVideoRecordingStatus(
                    RemoteVideoRecordingState.Faulted,
                    "Vehicle recording download failed",
                    GStreamerPipelineArguments.RedactText(ex.Message),
                    DateTimeOffset.UtcNow,
                    _spans.Count(item => !item.Cached),
                    _spans.Count(item => item.Cached)));
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RemoteVideoRecordingSpan> RetainAsync(
        string spanId,
        CancellationToken cancellationToken = default)
    {
        var cached = await EnsureCachedAsync(spanId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(cached.CachedPath))
            {
                throw new InvalidOperationException("The vehicle recording could not be cached before retention.");
            }
            await File.WriteAllTextAsync(cached.CachedPath + ".retained", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            await RefreshCoreAsync(cancellationToken, queryRemote: false);
            return _spans.First(item => item.Id == spanId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }
        _gate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool queryRemote = true)
    {
        var cached = await LoadCachedAsync(cancellationToken);
        if (_profile is null || _stream is null || _connection is null || string.IsNullOrWhiteSpace(_mediaPath))
        {
            SetSpans(cached);
            SetStatus(RemoteVideoRecordingStatus.NotConfigured with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                CachedSpanCount = cached.Count
            });
            return;
        }

        if (!queryRemote)
        {
            var mergedCached = Merge(_spans, cached);
            SetSpans(mergedCached);
            SetStatus(new RemoteVideoRecordingStatus(
                RemoteVideoRecordingState.Available,
                "Vehicle recording cache ready",
                cached.Count == 0
                    ? "No vehicle recordings have been cached locally for this camera."
                    : $"{cached.Count} vehicle recording span(s) are cached for offline playback.",
                DateTimeOffset.UtcNow,
                mergedCached.Count(item => !item.Cached),
                mergedCached.Count(item => item.Cached)));
            return;
        }

        SetStatus(new RemoteVideoRecordingStatus(
            RemoteVideoRecordingState.Discovering,
            "Discovering vehicle recordings",
            "Querying the configured MediaMTX playback server.",
            DateTimeOffset.UtcNow,
            0,
            cached.Count));
        try
        {
            var end = DateTimeOffset.UtcNow;
            var start = end - TimeSpan.FromHours(_configuration.RemoteVideoLookbackHours);
            using var request = CreateRequest(HttpMethod.Get, BuildListUri(_profile, _mediaPath, start, end));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<List<MediaMtxTimespanDto>>(
                stream,
                Json,
                cancellationToken: cancellationToken) ?? [];
            var remote = payload
                .Where(item =>
                    item.Start > DateTimeOffset.UnixEpoch &&
                    item.Start <= end.AddDays(1) &&
                    item.Duration > 0 &&
                    item.Duration <= TimeSpan.FromDays(30).TotalSeconds)
                .Select(item => CreateSpan(item.Start, item.Duration))
                .Where(item => item is not null)
                .Cast<RemoteVideoRecordingSpan>()
                .ToArray();
            var merged = Merge(remote, cached);
            SetSpans(merged);
            SetStatus(new RemoteVideoRecordingStatus(
                RemoteVideoRecordingState.Available,
                "Vehicle recordings available",
                remote.Length == 0
                    ? "The playback server is reachable but reported no recording spans in the configured lookback window."
                    : $"{remote.Length} vehicle-side span(s) discovered; {merged.Count(item => item.Cached)} cached locally.",
                DateTimeOffset.UtcNow,
                remote.Length,
                merged.Count(item => item.Cached)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = GStreamerPipelineArguments.RedactText(ex.Message);
            _logger.LogInformation("Vehicle recording discovery unavailable: {Detail}", detail);
            var offline = Merge(_spans, cached);
            SetSpans(offline);
            SetStatus(new RemoteVideoRecordingStatus(
                RemoteVideoRecordingState.Offline,
                "Vehicle recording server unreachable",
                cached.Count == 0
                    ? detail
                    : $"{detail} {cached.Count} cached span(s) remain available offline.",
                DateTimeOffset.UtcNow,
                offline.Count(item => !item.Cached),
                offline.Count(item => item.Cached)));
        }
    }

    private RemoteVideoRecordingSpan? CreateSpan(DateTimeOffset start, double duration)
    {
        if (_profile is null || _stream is null || _connection is null || string.IsNullOrWhiteSpace(_mediaPath))
        {
            return null;
        }
        var id = RemoteVideoRecordingSpan.CreateId(_connection.Id, _mediaPath, start, duration);
        return new RemoteVideoRecordingSpan(
            id,
            _profile.Name,
            _connection.Id,
            _stream.CameraSourceId,
            _stream.StreamId,
            _mediaPath,
            start,
            start.AddSeconds(duration),
            duration,
            string.IsNullOrWhiteSpace(_stream.Protocol) ? "Recorded" : _stream.Protocol,
            _stream.Width == 0 ? _configuration.GStreamerTestWidth : (int)_stream.Width,
            _stream.Height == 0 ? _configuration.GStreamerTestHeight : (int)_stream.Height,
            (int)Math.Round(_stream.FrameRateHz <= 0 ? _configuration.GStreamerTestFrameRate : _stream.FrameRateHz),
            null,
            0,
            false);
    }

    private async Task<IReadOnlyList<RemoteVideoRecordingSpan>> LoadCachedAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_configuration.RemoteVideoCachePath) || _stream is null || _connection is null)
        {
            return [];
        }

        var result = new List<RemoteVideoRecordingSpan>();
        foreach (var manifestPath in Directory.EnumerateFiles(_configuration.RemoteVideoCachePath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var input = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<RemoteVideoCacheManifest>(
                    input,
                    Json,
                    cancellationToken: cancellationToken);
                if (manifest is null || manifest.Schema != CacheSchema ||
                    manifest.ConnectionId != _connection.Id ||
                    manifest.CameraSourceId != _stream.CameraSourceId ||
                    manifest.DurationSeconds <= 0)
                {
                    continue;
                }
                var filePath = EnsureCacheFile(Path.Combine(_configuration.RemoteVideoCachePath, manifest.FileName));
                var file = new FileInfo(filePath);
                if (!file.Exists || file.Length == 0)
                {
                    continue;
                }
                result.Add(new RemoteVideoRecordingSpan(
                    manifest.Id,
                    manifest.ProfileName,
                    manifest.ConnectionId,
                    manifest.CameraSourceId,
                    manifest.StreamId,
                    manifest.MediaPath,
                    manifest.StartedAt,
                    manifest.EndedAt,
                    manifest.DurationSeconds,
                    manifest.Protocol,
                    manifest.Width,
                    manifest.Height,
                    manifest.FrameRate,
                    filePath,
                    file.Length,
                    File.Exists(filePath + ".retained")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Ignoring invalid vehicle recording cache manifest {Manifest}", manifestPath);
            }
        }
        return result.OrderBy(item => item.StartedAt).ToArray();
    }

    private static IReadOnlyList<RemoteVideoRecordingSpan> Merge(
        IEnumerable<RemoteVideoRecordingSpan> remote,
        IEnumerable<RemoteVideoRecordingSpan> cached)
    {
        var byId = remote
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var item in cached)
        {
            if (byId.TryGetValue(item.Id, out var existing))
            {
                byId[item.Id] = existing with
                {
                    CachedPath = item.CachedPath,
                    CachedSizeBytes = item.CachedSizeBytes,
                    Retained = item.Retained
                };
            }
            else
            {
                byId[item.Id] = item;
            }
        }
        return byId.Values.OrderBy(item => item.StartedAt).ToArray();
    }

    private async Task WriteCacheManifestAsync(
        RemoteVideoRecordingSpan span,
        string cachedPath,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(cachedPath);
        var manifest = new RemoteVideoCacheManifest(
            CacheSchema,
            span.Id,
            span.ProfileName,
            span.ConnectionId,
            span.CameraSourceId,
            span.StreamId,
            span.MediaPath,
            span.StartedAt,
            span.EndedAt,
            span.DurationSeconds,
            span.Protocol,
            span.Width,
            span.Height,
            span.FrameRate,
            file.Name,
            file.Length,
            DateTimeOffset.UtcNow);
        var path = Path.Combine(_configuration.RemoteVideoCachePath, span.Id + ".json");
        var temporary = path + ".tmp";
        await using (var output = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                output,
                manifest,
                Json,
                cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private async Task CleanupCacheAsync(string protectedSpanId, CancellationToken cancellationToken)
    {
        var entries = await LoadAllCacheEntriesAsync(cancellationToken);
        var maximumBytes = (long)_configuration.RemoteVideoCacheMaximumGigabytes * 1024L * 1024L * 1024L;
        var total = entries.Sum(item => item.SizeBytes);
        foreach (var entry in entries.Where(item => !item.Retained && item.Id != protectedSpanId && item.Id != _playbackProtectedSpanId).OrderBy(item => item.StartedAt))
        {
            if (total <= maximumBytes)
            {
                break;
            }
            TryDelete(entry.FilePath);
            TryDelete(entry.FilePath + ".retained");
            TryDelete(entry.ManifestPath);
            total -= entry.SizeBytes;
        }
    }

    private async Task<IReadOnlyList<CacheEntry>> LoadAllCacheEntriesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_configuration.RemoteVideoCachePath))
        {
            return [];
        }

        var result = new List<CacheEntry>();
        foreach (var manifestPath in Directory.EnumerateFiles(_configuration.RemoteVideoCachePath, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var input = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<RemoteVideoCacheManifest>(
                    input,
                    Json,
                    cancellationToken: cancellationToken);
                if (manifest is null || manifest.Schema != CacheSchema)
                {
                    continue;
                }
                var filePath = EnsureCacheFile(Path.Combine(_configuration.RemoteVideoCachePath, manifest.FileName));
                var file = new FileInfo(filePath);
                if (!file.Exists)
                {
                    continue;
                }
                result.Add(new CacheEntry(
                    manifest.Id,
                    manifest.StartedAt,
                    filePath,
                    manifestPath,
                    file.Length,
                    File.Exists(filePath + ".retained")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Ignoring invalid vehicle cache entry {Manifest}", manifestPath);
            }
        }
        return result;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_profile is not null && !string.IsNullOrWhiteSpace(_profile.BearerTokenEnvironmentVariable))
        {
            var token = Environment.GetEnvironmentVariable(_profile.BearerTokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            }
        }
        request.Headers.UserAgent.ParseAdd("Robot-Command/1.0");
        return request;
    }

    private static Uri BuildListUri(
        RemoteVideoRecordingProfile profile,
        string path,
        DateTimeOffset start,
        DateTimeOffset end)
        => BuildUri(profile.PlaybackBaseUrl, "list",
        [
            new("path", path),
            new("start", start.ToString("O")),
            new("end", end.ToString("O"))
        ]);

    private static Uri BuildGetUri(
        RemoteVideoRecordingProfile profile,
        string path,
        DateTimeOffset start,
        double duration)
        => BuildUri(profile.PlaybackBaseUrl, "get",
        [
            new("path", path),
            new("start", start.ToString("O")),
            new("duration", duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            new("format", "mp4")
        ]);

    private static Uri BuildUri(
        string baseUrl,
        string endpoint,
        IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(root.UserInfo) ||
            !string.IsNullOrEmpty(root.Query) ||
            !string.IsNullOrEmpty(root.Fragment))
        {
            throw new InvalidOperationException("The MediaMTX playback base URL must be an absolute HTTP or HTTPS URL.");
        }
        var builder = new UriBuilder(new Uri(root, endpoint))
        {
            Query = string.Join("&", parameters.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))
        };
        return builder.Uri;
    }

    private static string ExpandPath(string template, CameraStreamRecord stream)
    {
        var path = template
            .Replace("{cameraSourceId}", stream.CameraSourceId, StringComparison.Ordinal)
            .Replace("{streamId}", stream.StreamId, StringComparison.Ordinal)
            .Replace("{connectionId}", stream.ConnectionId, StringComparison.Ordinal)
            .Replace("{logosInstanceId}", stream.LogosInstanceId ?? string.Empty, StringComparison.Ordinal)
            .Trim()
            .Trim('/');
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 512 ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('?') ||
            path.Contains('#') ||
            path.Any(char.IsControl))
        {
            throw new InvalidOperationException("The configured MediaMTX path template produced an invalid path.");
        }
        return path;
    }

    private string EnsureCacheFile(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_configuration.RemoteVideoCachePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison))
        {
            throw new InvalidOperationException("The vehicle recording cache path is outside the configured cache directory.");
        }
        return full;
    }

    private static bool HasMp4Header(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Read(header) == header.Length &&
                   header[4] == (byte)'f' && header[5] == (byte)'t' &&
                   header[6] == (byte)'y' && header[7] == (byte)'p';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetStatus(RemoteVideoRecordingStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetSpans(IReadOnlyList<RemoteVideoRecordingSpan> spans)
    {
        lock (_stateGate)
        {
            _spans = spans;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed record CacheEntry(
        string Id,
        DateTimeOffset StartedAt,
        string FilePath,
        string ManifestPath,
        long SizeBytes,
        bool Retained);

    private sealed record MediaMtxTimespanDto(
        [property: JsonPropertyName("start")] DateTimeOffset Start,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("url")] string? Url);
}
