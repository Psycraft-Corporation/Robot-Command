using System.Net;
using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class VideoProtocolPolicy : IVideoProtocolPolicy
{
    public IReadOnlyList<VideoProtocolPreference> BuildPlan(
        ConnectionDefinition? connection,
        VideoProtocolPreference requested,
        GStreamerRuntimeDiagnostics diagnostics)
    {
        if (requested != VideoProtocolPreference.Automatic)
        {
            return [requested];
        }

        var candidates = connection?.Mode == ConnectionMode.FieldLink
            ? new[] { VideoProtocolPreference.Automatic, VideoProtocolPreference.Hls, VideoProtocolPreference.WebRtc, VideoProtocolPreference.Rtsp }
            : IsLocalOrPrivate(connection?.Target)
                ? new[] { VideoProtocolPreference.Automatic, VideoProtocolPreference.Rtsp, VideoProtocolPreference.Hls, VideoProtocolPreference.WebRtc }
                : new[] { VideoProtocolPreference.WebRtc, VideoProtocolPreference.Hls, VideoProtocolPreference.Automatic, VideoProtocolPreference.Rtsp };

        return candidates
            .Where(protocol => protocol == VideoProtocolPreference.Automatic ||
                               diagnostics.State is GStreamerRuntimeState.Unknown or GStreamerRuntimeState.Inspecting ||
                               diagnostics.Supports(protocol))
            .Distinct()
            .ToArray();
    }

    private static bool IsLocalOrPrivate(string? target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return true;
        }
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254));
    }
}
