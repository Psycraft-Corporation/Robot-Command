using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests;

public sealed class MediaMtxRemoteVideoRecordingCatalogTests
{
    [Fact]
    public async Task RefreshAndDownload_UsesConfiguredPlaybackServerAndCreatesOfflineCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "logos-robot-command-remote-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new RecordingHandler();
            using var client = new HttpClient(handler);
            var configuration = Configuration(root);
            await using var catalog = new MediaMtxRemoteVideoRecordingCatalog(
                configuration,
                NullLogger<MediaMtxRemoteVideoRecordingCatalog>.Instance,
                client);

            await catalog.BeginSessionAsync(Stream(), Definition());

            var discovered = Assert.Single(catalog.Spans);
            Assert.Equal("camera/front", discovered.MediaPath);
            Assert.False(discovered.Cached);
            Assert.Contains("/list?", handler.Requests[0].PathAndQuery);
            Assert.Contains("path=camera%2Ffront", handler.Requests[0].PathAndQuery);

            var cached = await catalog.EnsureCachedAsync(discovered.Id);

            Assert.True(cached.Cached);
            Assert.True(File.Exists(cached.CachedPath));
            Assert.Contains("/get?", handler.Requests[1].PathAndQuery);
            Assert.Contains("format=mp4", handler.Requests[1].PathAndQuery);
            Assert.DoesNotContain("token", await File.ReadAllTextAsync(Path.Combine(root, discovered.Id + ".json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }



    [Fact]
    public async Task OfflineDiscovery_KeepsPreviouslyCachedVehicleRecordingPlayable()
    {
        var root = Path.Combine(Path.GetTempPath(), "logos-robot-command-remote-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = Configuration(root);
            using var onlineClient = new HttpClient(new RecordingHandler());
            await using (var online = new MediaMtxRemoteVideoRecordingCatalog(
                configuration,
                NullLogger<MediaMtxRemoteVideoRecordingCatalog>.Instance,
                onlineClient))
            {
                await online.BeginSessionAsync(Stream(), Definition());
                await online.EnsureCachedAsync(Assert.Single(online.Spans).Id);
            }

            using var offlineClient = new HttpClient(new RecordingHandler(failList: true));
            await using var offline = new MediaMtxRemoteVideoRecordingCatalog(
                configuration,
                NullLogger<MediaMtxRemoteVideoRecordingCatalog>.Instance,
                offlineClient);

            await offline.BeginSessionAsync(Stream(), Definition());

            var cached = Assert.Single(offline.Spans);
            Assert.True(cached.Cached);
            Assert.Equal(RemoteVideoRecordingState.Offline, offline.Status.State);
            Assert.Contains("cached span", offline.Status.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProtectedPlayback_IsNotRemovedByCacheCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "logos-robot-command-remote-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new RecordingHandler(twoSpans: true);
            using var client = new HttpClient(handler);
            var configuration = new AppConfiguration
            {
                RemoteVideoCachePath = root,
                RemoteVideoCacheMaximumGigabytes = 0,
                RemoteVideoRecordingProfiles =
                [
                    new RemoteVideoRecordingProfile(
                        "test",
                        "http://media.local:9996",
                        "camera/{cameraSourceId}",
                        ConnectionId: "connection-1")
                ]
            };
            await using var catalog = new MediaMtxRemoteVideoRecordingCatalog(
                configuration,
                NullLogger<MediaMtxRemoteVideoRecordingCatalog>.Instance,
                client);

            await catalog.BeginSessionAsync(Stream(), Definition());
            var spans = catalog.Spans.OrderBy(item => item.StartedAt).ToArray();
            Assert.Equal(2, spans.Length);

            var first = await catalog.EnsureCachedAsync(spans[0].Id);
            catalog.ProtectPlayback(first.Id);
            var second = await catalog.EnsureCachedAsync(spans[1].Id);

            Assert.True(File.Exists(first.CachedPath));
            Assert.True(File.Exists(second.CachedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AppConfiguration Configuration(string root)
        => new()
        {
            RemoteVideoCachePath = root,
            RemoteVideoRecordingProfiles =
            [
                new RemoteVideoRecordingProfile(
                    "test",
                    "http://media.local:9996",
                    "camera/{cameraSourceId}",
                    ConnectionId: "connection-1")
            ]
        };

    private static ConnectionDefinition Definition()
        => new("connection-1", "Test", "http://logos.local:50051");

    private static CameraStreamRecord Stream()
        => new(
            "record-1",
            "stream-1",
            "front",
            "connection-1",
            "logos-1",
            "RTSP",
            "Open",
            "rtsp://media.local/front",
            "",
            "H264",
            1280,
            720,
            30,
            2000,
            DateTimeOffset.UtcNow,
            null,
            "",
            "",
            DateTimeOffset.UtcNow);

    private sealed class RecordingHandler(bool twoSpans = false, bool failList = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal))
            {
                if (failList)
                {
                    throw new HttpRequestException("Playback server unavailable");
                }
                var json = twoSpans
                    ? """
                      [
                        {
                          "start": "2026-07-25T12:00:00Z",
                          "duration": 60.0,
                          "url": "http://media.local:9996/get?ignored=true"
                        },
                        {
                          "start": "2026-07-25T12:02:00Z",
                          "duration": 60.0,
                          "url": "http://media.local:9996/get?ignored=true"
                        }
                      ]
                      """
                    : """
                      [
                        {
                          "start": "2026-07-25T12:00:00Z",
                          "duration": 60.0,
                          "url": "http://media.local:9996/get?ignored=true"
                        }
                      ]
                      """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109])
            });
        }
    }
}
