using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public static class NativeVideoProtocolResolver
{
    public static NativeVideoProtocol Resolve(CameraStreamRecord stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (Uri.TryCreate(stream.StreamUrl, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "rtsp" or "rtsps") return NativeVideoProtocol.Rtsp;
            if (uri.Scheme == "srt") return NativeVideoProtocol.Srt;
            if (uri.Scheme is "http" or "https")
            {
                if (stream.Protocol.Contains("Hls", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.EndsWith("/index.m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    return NativeVideoProtocol.Hls;
                }
                if (stream.Protocol.Contains("Webrtc", StringComparison.OrdinalIgnoreCase) ||
                    stream.Protocol.Contains("WebRtc", StringComparison.OrdinalIgnoreCase) ||
                    stream.Protocol.Contains("Whep", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.EndsWith("/whep", StringComparison.OrdinalIgnoreCase))
                {
                    return NativeVideoProtocol.Whep;
                }
            }
        }

        if (stream.Protocol.Contains("Rtsp", StringComparison.OrdinalIgnoreCase)) return NativeVideoProtocol.Rtsp;
        if (stream.Protocol.Contains("Hls", StringComparison.OrdinalIgnoreCase)) return NativeVideoProtocol.Hls;
        if (stream.Protocol.Contains("Webrtc", StringComparison.OrdinalIgnoreCase) ||
            stream.Protocol.Contains("WebRtc", StringComparison.OrdinalIgnoreCase) ||
            stream.Protocol.Contains("Whep", StringComparison.OrdinalIgnoreCase)) return NativeVideoProtocol.Whep;
        if (stream.Protocol.Contains("Srt", StringComparison.OrdinalIgnoreCase)) return NativeVideoProtocol.Srt;
        return NativeVideoProtocol.Unknown;
    }

    public static string DisplayName(NativeVideoProtocol protocol)
        => protocol switch
        {
            NativeVideoProtocol.Rtsp => "RTSP",
            NativeVideoProtocol.Srt => "SRT",
            NativeVideoProtocol.Hls => "HLS",
            NativeVideoProtocol.Whep => "WebRTC/WHEP",
            _ => "Unknown"
        };
}
