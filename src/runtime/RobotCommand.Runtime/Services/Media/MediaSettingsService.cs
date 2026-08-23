using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class MediaSettingsService : IMediaSettingsService, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaSettingsSnapshot _current;

    public MediaSettingsService(AppConfiguration configuration, string baseDirectory)
    {
        _path = Path.Combine(baseDirectory, "appsettings.local.json");
        _current = MediaSettingsSnapshot.FromConfiguration(configuration);
    }

    public MediaSettingsSnapshot Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public async Task SetAsync(MediaSettingsSnapshot settings, CancellationToken cancellationToken = default)
    {
        var normalized = MediaSettingsSnapshot.Normalize(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = await LoadRootAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            options.Converters.Add(new JsonStringEnumConverter());
            var video = root["video"] as JsonObject ?? [];
            root["video"] = video;
            video["defaultProtocol"] = JsonSerializer.SerializeToNode(normalized.DefaultProtocol, options);
            video["rtspTransport"] = JsonSerializer.SerializeToNode(normalized.RtspTransport, options);
            video["rtspLatencyMilliseconds"] = normalized.RtspLatencyMilliseconds;
            video["rtspReconnectAttempts"] = normalized.RtspReconnectAttempts;
            video["rtspReconnectDelayMilliseconds"] = normalized.RtspReconnectDelayMilliseconds;
            video["localRecordingEnabled"] = normalized.LocalRecordingEnabled;
            video["rollingBufferMinutes"] = normalized.RollingBufferMinutes;
            video["segmentSeconds"] = normalized.SegmentSeconds;
            video["maximumStorageGigabytes"] = normalized.MaximumStorageGigabytes;
            video["encodingBitrateKbps"] = normalized.EncodingBitrateKbps;
            video["remoteRecordingLookbackHours"] = normalized.RemoteLookbackHours;
            video["remoteRecordingRequestTimeoutSeconds"] = normalized.RemoteRequestTimeoutSeconds;
            video["remoteRecordingCacheMaximumGigabytes"] = normalized.RemoteCacheMaximumGigabytes;
            video["remoteRecordingMaximumDownloadGigabytes"] = normalized.RemoteMaximumDownloadGigabytes;

            var temporaryPath = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, null);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }

            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<JsonObject> LoadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = File.OpenRead(_path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject ?? [];
    }
}
