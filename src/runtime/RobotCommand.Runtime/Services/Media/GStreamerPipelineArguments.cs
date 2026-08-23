using System.Text.RegularExpressions;
using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public static class GStreamerPipelineArguments
{
    private static readonly Regex EndpointPattern = new(
        @"(?:rtsps?|srt|https?|turns?|stuns?)://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SecretPropertyPattern = new(
        @"(?<name>auth-token|passphrase|user-pw|proxy-pw)=(?<value>[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "smpte", "snow", "black", "white", "red", "green", "blue",
        "checkers-1", "checkers-2", "checkers-4", "checkers-8", "circular",
        "blink", "smpte75", "zone-plate", "gamut", "chroma-zone-plate",
        "solid-color", "ball", "smpte100", "bar"
    };

    public static IReadOnlyList<string> BuildTestPattern(
        GStreamerTestSourceOptions options,
        int loopbackPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.Output, loopbackPort);
        var pattern = AllowedPatterns.Contains(options.Pattern)
            ? options.Pattern.ToLowerInvariant()
            : "smpte";

        var arguments = new List<string>
        {
            "-q", "videotestsrc", "is-live=true", $"pattern={pattern}",
            "!", "videoconvert", "!", "videoscale", "!", "videorate"
        };
        AppendRawOutput(arguments, options.Output, loopbackPort);
        return arguments;
    }

    public static IReadOnlyList<string> BuildFile(
        string path,
        GStreamerRawVideoOptions output,
        int loopbackPort)
    {
        Validate(output, loopbackPort);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A local media file path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The local media test file does not exist.", fullPath);
        }

        var arguments = new List<string>
        {
            "-q", "filesrc", $"location={fullPath}", "!", "decodebin",
            "!", "videoconvert", "!", "videoscale", "!", "videorate"
        };
        AppendRawOutput(arguments, output, loopbackPort);
        return arguments;
    }

    public static IReadOnlyList<string> BuildRtsp(
        RtspPlaybackOptions options,
        int loopbackPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.Output, loopbackPort);
        var endpoint = ValidateEndpoint(options.Endpoint, "rtsp", "rtsps");
        if (options.LatencyMilliseconds is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RTSP latency must be between 0 and 60000 milliseconds.");
        }

        var arguments = new List<string>
        {
            "-q", "rtspsrc", $"location={endpoint}", $"latency={options.LatencyMilliseconds}"
        };
        var transport = ToGStreamerTransport(options.Transport);
        if (transport is not null)
        {
            arguments.Add($"protocols={transport}");
        }

        arguments.AddRange(["!", "decodebin", "!", "videoconvert", "!", "videoscale", "!", "videorate"]);
        AppendRawOutput(arguments, options.Output, loopbackPort);
        return arguments;
    }

    public static IReadOnlyList<string> BuildSrt(
        SrtPlaybackOptions options,
        int loopbackPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.Output, loopbackPort);
        var endpoint = ValidateEndpoint(options.Endpoint, "srt");
        if (options.LatencyMilliseconds is < 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SRT latency must be between 0 and 60000 milliseconds.");
        }

        var arguments = new List<string>
        {
            "-q", "srtsrc", $"uri={endpoint}", $"latency={options.LatencyMilliseconds}",
            "auto-reconnect=true", "!", "tsdemux", "!", "decodebin",
            "!", "videoconvert", "!", "videoscale", "!", "videorate"
        };
        AppendRawOutput(arguments, options.Output, loopbackPort);
        return arguments;
    }

    public static IReadOnlyList<string> BuildHls(
        HlsPlaybackOptions options,
        int loopbackPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.Output, loopbackPort);
        var endpoint = ValidateEndpoint(options.Endpoint, "http", "https");
        if (options.TimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "HLS timeout must be between 1 and 300 seconds.");
        }

        var arguments = new List<string>
        {
            "-q", "souphttpsrc", $"location={endpoint}", "is-live=true",
            $"timeout={options.TimeoutSeconds}", "retries=3", "!", "hlsdemux",
            "!", "decodebin", "!", "videoconvert", "!", "videoscale", "!", "videorate"
        };
        AppendRawOutput(arguments, options.Output, loopbackPort);
        return arguments;
    }

    public static IReadOnlyList<string> BuildWhep(
        WhepPlaybackOptions options,
        int loopbackPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options.Output, loopbackPort);
        var endpoint = ValidateEndpoint(options.Endpoint, "http", "https");
        if (options.TimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WHEP timeout must be between 1 and 300 seconds.");
        }

        var codec = ResolveWhepCodec(options.Codec, options.PayloadType);
        var arguments = new List<string>
        {
            "-q", "whepsrc", $"whep-endpoint={endpoint}",
            $"use-link-headers={options.UseLinkHeaders.ToString().ToLowerInvariant()}",
            $"timeout={options.TimeoutSeconds}", $"video-caps={codec.Caps}",
            "audio-caps=application/x-rtp,media=audio,encoding-name=PCMU,payload=0,clock-rate=8000"
        };

        if (!string.IsNullOrWhiteSpace(options.AuthToken))
        {
            arguments.Add($"auth-token={options.AuthToken.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(options.StunServer))
        {
            arguments.Add($"stun-server={ValidateEndpoint(options.StunServer, "stun", "stuns")}");
        }
        if (!string.IsNullOrWhiteSpace(options.TurnServer))
        {
            arguments.Add($"turn-server={ValidateEndpoint(options.TurnServer, "turn", "turns")}");
        }

        arguments.AddRange(["!", codec.Depayloader, "!", "decodebin", "!", "videoconvert", "!", "videoscale", "!", "videorate"]);
        AppendRawOutput(arguments, options.Output, loopbackPort);
        return arguments;
    }

    public static string Describe(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(SanitizeForDisplay).Select(QuoteForDisplay));

    public static string RedactText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var endpointsRedacted = EndpointPattern.Replace(text, match => RedactEndpoint(match.Value));
        return SecretPropertyPattern.Replace(endpointsRedacted, match => $"{match.Groups["name"].Value}=<redacted>");
    }

    public static string RedactEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "<invalid endpoint>";
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "redacted",
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    public static string WhepDepayloader(string codec)
        => ResolveWhepCodec(codec, 0).Depayloader;

    private static void AppendRawOutput(
        ICollection<string> arguments,
        GStreamerRawVideoOptions options,
        int loopbackPort)
    {
        arguments.Add("!");
        arguments.Add(Caps(options));
        arguments.Add("!");
        arguments.Add("queue");
        arguments.Add("max-size-buffers=2");
        arguments.Add("leaky=downstream");
        arguments.Add("!");
        arguments.Add("tcpclientsink");
        arguments.Add("host=127.0.0.1");
        arguments.Add($"port={loopbackPort}");
        arguments.Add("sync=true");
    }

    private static string Caps(GStreamerRawVideoOptions options)
        => $"video/x-raw,format=BGRA,width={options.Width},height={options.Height},framerate={options.FrameRate}/1,pixel-aspect-ratio=1/1";

    private static void Validate(GStreamerRawVideoOptions options, int loopbackPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Width is < 16 or > 7680)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Width must be between 16 and 7680 pixels.");
        }
        if (options.Height is < 16 or > 4320)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Height must be between 16 and 4320 pixels.");
        }
        if (options.FrameRate is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Frame rate must be between 1 and 120 fps.");
        }
        if (loopbackPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(loopbackPort), "The loopback port must be between 1 and 65535.");
        }
    }

    private static string ValidateEndpoint(string endpoint, params string[] schemes)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"A valid {string.Join(" or ", schemes.Select(item => item + "://"))} endpoint is required.", nameof(endpoint));
        }
        return endpoint.Trim();
    }

    private static WhepCodec ResolveWhepCodec(string codec, int requestedPayloadType)
    {
        var normalized = (codec ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var result = normalized switch
        {
            "H265" or "HEVC" => new WhepCodec("H265", 104, "rtph265depay"),
            "VP8" => new WhepCodec("VP8", 101, "rtpvp8depay"),
            "VP9" => new WhepCodec("VP9", 102, "rtpvp9depay"),
            "AV1" or "AV01" => new WhepCodec("AV1", 105, "rtpav1depay"),
            "H264" or "AVC" or "AVC1" or "" => new WhepCodec("H264", 103, "rtph264depay"),
            _ => throw new NotSupportedException($"WHEP codec '{codec}' is not supported by the native player.")
        };
        var payloadType = requestedPayloadType is >= 96 and <= 127 ? requestedPayloadType : result.PayloadType;
        return result with
        {
            PayloadType = payloadType,
            Caps = $"application/x-rtp,media=video,encoding-name={result.EncodingName},payload={payloadType},clock-rate=90000"
        };
    }

    private static string? ToGStreamerTransport(RtspTransportMode transport)
        => transport switch
        {
            RtspTransportMode.Automatic => null,
            RtspTransportMode.Tcp => "tcp",
            RtspTransportMode.Udp => "udp",
            RtspTransportMode.UdpMulticast => "udp-mcast",
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };

    private static string SanitizeForDisplay(string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0)
        {
            return RedactText(value);
        }

        var name = value[..separator];
        var propertyValue = value[(separator + 1)..];
        if (name.Equals("auth-token", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("passphrase", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("user-pw", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("proxy-pw", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}=<redacted>";
        }

        if (name.Equals("location", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("uri", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("whep-endpoint", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("stun-server", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("turn-server", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}={RedactEndpoint(propertyValue)}";
        }

        return RedactText(value);
    }

    private static string QuoteForDisplay(string value)
    {
        if (!value.Any(char.IsWhiteSpace))
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private sealed record WhepCodec(
        string EncodingName,
        int PayloadType,
        string Depayloader,
        string Caps = "");
}
