using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Media;
using Xunit;

namespace RobotCommand.Tests.Media;

public sealed class RtspConnectionProviderTests
{
    [Fact]
    public async Task MediaConnectionProbesAndPublishesCameraWithoutVehicleObservation()
    {
        var provider = new FakeRtspProvider();
        var managed = Assert.IsAssignableFrom<IManagedConnection>(new RtspConnectionProvider([provider]).Create(
            new ConnectionDefinition("media:hm30", "HM30 Video", "rtsp://camera.test/live", ConnectionMode.Media)));

        var observation = await managed.ConnectAsync(ConnectionCredentials.Empty, false);
        Assert.Equal(AvailabilityState.Online, managed.State);
        Assert.Null(observation.Vehicle);
        Assert.Equal("HM30 Video", Assert.Single(managed.LiveSnapshot.CameraSources).Name);
        var stream = await managed.OpenCameraStreamAsync(new CameraStreamOpenRequest("hm30"));
        Assert.Equal("media:hm30", stream.ConnectionId);
        Assert.True(managed.HasActiveStreams);
        await managed.CloseCameraStreamAsync(stream.Id);
        Assert.False(managed.HasActiveStreams);
    }

    [Fact]
    public void RtspConnectionRejectsCredentialBearingAndNonRtspTargets()
    {
        var provider = new RtspConnectionProvider([new FakeRtspProvider()]);
        Assert.Throws<ArgumentException>(() => provider.Create(new ConnectionDefinition(
            "bad", "Bad", "rtsp://user:secret@camera.test/live", ConnectionMode.Media)));
        Assert.Throws<ArgumentException>(() => provider.Create(new ConnectionDefinition(
            "bad", "Bad", "https://camera.test/live", ConnectionMode.Media)));
    }

    private sealed class FakeRtspProvider : ICameraMediaSourceProvider
    {
        public string Key => "rtsp";
        public string DisplayName => "RTSP";
        public void Validate(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp" || uri.UserInfo.Length > 0)
                throw new ArgumentException("Invalid RTSP endpoint.", nameof(endpoint));
        }
        public Task<CameraMediaSourceProbeResult> ProbeAsync(string endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(new CameraMediaSourceProbeResult(true, "Reachable", DateTimeOffset.UtcNow));
        public CameraSourceRecord CreateSource(string id, string name, string endpoint, bool available, string message)
            => new($"media:{id}", id, $"media:{id}", null, name, Key, available ? AvailabilityState.Online : AvailabilityState.Offline,
                message, message, available, available, false, 0, 0, 0, 0, "", "RTSP", message, DateTimeOffset.UtcNow,
                SupportsPhoto: false, SupportsVideo: false, SupportsGimbal: false);
        public Task<CameraStreamRecord> OpenAsync(string id, string name, string endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(new CameraStreamRecord($"stream:{id}", id, id, $"media:{id}", null, "RTSP", "Live", endpoint, "", "", 0, 0, 0, 0,
                DateTimeOffset.UtcNow, null, "OPEN", "Opening", DateTimeOffset.UtcNow));
        public Task CloseAsync(CameraStreamRecord stream, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
