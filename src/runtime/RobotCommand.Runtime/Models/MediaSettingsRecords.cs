using RobotCommand.Services;

namespace RobotCommand.Models;

public sealed record MediaSettingsSnapshot(
    VideoProtocolPreference DefaultProtocol,
    RtspTransportMode RtspTransport,
    int RtspLatencyMilliseconds,
    int RtspReconnectAttempts,
    int RtspReconnectDelayMilliseconds,
    bool LocalRecordingEnabled,
    int RollingBufferMinutes,
    int SegmentSeconds,
    int MaximumStorageGigabytes,
    int EncodingBitrateKbps,
    int RemoteLookbackHours,
    int RemoteRequestTimeoutSeconds,
    int RemoteCacheMaximumGigabytes,
    int RemoteMaximumDownloadGigabytes)
{
    public static MediaSettingsSnapshot FromConfiguration(AppConfiguration configuration)
        => Normalize(new(
            configuration.DefaultVideoProtocol,
            configuration.GStreamerRtspTransport,
            configuration.GStreamerRtspLatencyMilliseconds,
            configuration.GStreamerRtspReconnectAttempts,
            configuration.GStreamerRtspReconnectDelayMilliseconds,
            configuration.LocalVideoRecordingEnabled,
            configuration.LocalVideoRollingBufferMinutes,
            configuration.LocalVideoSegmentSeconds,
            configuration.LocalVideoMaximumStorageGigabytes,
            configuration.LocalVideoEncodingBitrateKbps,
            configuration.RemoteVideoLookbackHours,
            configuration.RemoteVideoRequestTimeoutSeconds,
            configuration.RemoteVideoCacheMaximumGigabytes,
            configuration.RemoteVideoMaximumDownloadGigabytes));

    public static MediaSettingsSnapshot Normalize(MediaSettingsSnapshot value)
        => value with
        {
            DefaultProtocol = Enum.IsDefined(value.DefaultProtocol)
                ? value.DefaultProtocol
                : VideoProtocolPreference.Automatic,
            RtspTransport = Enum.IsDefined(value.RtspTransport)
                ? value.RtspTransport
                : RtspTransportMode.Tcp,
            RtspLatencyMilliseconds = Math.Clamp(value.RtspLatencyMilliseconds, 0, 60_000),
            RtspReconnectAttempts = Math.Clamp(value.RtspReconnectAttempts, 0, 20),
            RtspReconnectDelayMilliseconds = Math.Clamp(value.RtspReconnectDelayMilliseconds, 100, 30_000),
            RollingBufferMinutes = Math.Clamp(value.RollingBufferMinutes, 1, 24 * 60),
            SegmentSeconds = Math.Clamp(value.SegmentSeconds, 2, 300),
            MaximumStorageGigabytes = Math.Clamp(value.MaximumStorageGigabytes, 1, 2048),
            EncodingBitrateKbps = Math.Clamp(value.EncodingBitrateKbps, 128, 100_000),
            RemoteLookbackHours = Math.Clamp(value.RemoteLookbackHours, 1, 24 * 90),
            RemoteRequestTimeoutSeconds = Math.Clamp(value.RemoteRequestTimeoutSeconds, 2, 300),
            RemoteCacheMaximumGigabytes = Math.Clamp(value.RemoteCacheMaximumGigabytes, 1, 4096),
            RemoteMaximumDownloadGigabytes = Math.Clamp(value.RemoteMaximumDownloadGigabytes, 1, 1024)
        };
}

