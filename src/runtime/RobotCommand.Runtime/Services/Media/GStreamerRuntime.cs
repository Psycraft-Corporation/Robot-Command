using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Media;

public sealed class GStreamerRuntime : IGStreamerRuntime, IDisposable
{
    private static readonly string[] CorePlugins =
    [
        "videotestsrc", "filesrc", "decodebin", "videoconvert", "videoscale",
        "videorate", "queue", "tcpclientsink"
    ];

    private static readonly IReadOnlyDictionary<VideoProtocolPreference, string[]> ProtocolPlugins =
        new Dictionary<VideoProtocolPreference, string[]>
        {
            [VideoProtocolPreference.Rtsp] = ["rtspsrc"],
            [VideoProtocolPreference.Hls] = ["souphttpsrc", "hlsdemux"],
            [VideoProtocolPreference.WebRtc] = ["whepsrc"]
        };

    private static readonly string[] AdditionalPlugins =
    [
        "srtsrc", "tsdemux", "rtph264depay", "rtph265depay",
        "rtpvp8depay", "rtpvp9depay", "rtpav1depay"
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppConfiguration _configuration;
    private readonly ILogger<GStreamerRuntime> _logger;

    public GStreamerRuntime(
        AppConfiguration configuration,
        ILogger<GStreamerRuntime> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public GStreamerRuntimeDiagnostics Diagnostics { get; private set; } =
        GStreamerRuntimeDiagnostics.Unknown;

    public async Task<GStreamerRuntimeDiagnostics> InspectAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && Diagnostics.State is not GStreamerRuntimeState.Unknown)
            {
                return Diagnostics;
            }

            Diagnostics = Diagnostics with
            {
                State = GStreamerRuntimeState.Inspecting,
                Summary = "Inspecting GStreamer",
                Detail = "Locating GStreamer and inspecting native video protocol plugins."
            };

            var executables = GStreamerExecutableLocator.Locate(_configuration.GStreamerBinPath);
            if (executables is null)
            {
                Diagnostics = new GStreamerRuntimeDiagnostics(
                    GStreamerRuntimeState.Missing,
                    "GStreamer runtime not found",
                    "Install GStreamer 1.x or set video.gstreamerBinPath to the directory containing gst-launch-1.0 and gst-inspect-1.0.");
                return Diagnostics;
            }

            var environment = BuildEnvironment();
            var versionResult = await RunAsync(executables.Launch, ["--version"], environment, cancellationToken);
            if (versionResult.ExitCode != 0)
            {
                Diagnostics = new GStreamerRuntimeDiagnostics(
                    GStreamerRuntimeState.Faulted,
                    "GStreamer could not start",
                    FirstNonEmpty(versionResult.StandardError, versionResult.StandardOutput, "gst-launch-1.0 returned an error."),
                    executables.Launch,
                    executables.Inspect);
                return Diagnostics;
            }

            var pluginsToInspect = CorePlugins
                .Concat(ProtocolPlugins.Values.SelectMany(item => item))
                .Concat(AdditionalPlugins)
                .Concat(GStreamerRecordingArguments.RequiredPlugins)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in pluginsToInspect)
            {
                var result = await RunAsync(executables.Inspect, [plugin], environment, cancellationToken);
                if (result.ExitCode == 0)
                {
                    available.Add(plugin);
                }
            }

            var missingCore = CorePlugins.Where(plugin => !available.Contains(plugin)).ToArray();
            var capabilities = BuildProtocolCapabilities(available);
            var version = versionResult.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.Contains("GStreamer", StringComparison.OrdinalIgnoreCase))
                ?? versionResult.StandardOutput.Trim();

            if (missingCore.Length > 0)
            {
                Diagnostics = new GStreamerRuntimeDiagnostics(
                    GStreamerRuntimeState.Faulted,
                    "GStreamer core plugins are missing",
                    $"The runtime was found, but these required frame-pipeline plugins are unavailable: {string.Join(", ", missingCore)}.",
                    executables.Launch,
                    executables.Inspect,
                    version,
                    missingCore,
                    available.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                    capabilities);
                return Diagnostics;
            }

            var supported = capabilities
                .Where(item => item.Available)
                .Select(item => DisplayName(item.Protocol))
                .ToList();
            if (available.Contains("srtsrc") && available.Contains("tsdemux"))
            {
                supported.Add("SRT");
            }
            Diagnostics = new GStreamerRuntimeDiagnostics(
                GStreamerRuntimeState.Available,
                "GStreamer runtime available",
                supported.Count == 0
                    ? $"{version}. Core frame plugins are installed, but no network playback protocol is currently available."
                    : $"{version}. Native playback available for: {string.Join(", ", supported)}.",
                executables.Launch,
                executables.Inspect,
                version,
                [],
                available.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
                capabilities);
            return Diagnostics;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GStreamer runtime inspection failed");
            Diagnostics = new GStreamerRuntimeDiagnostics(
                GStreamerRuntimeState.Faulted,
                "GStreamer inspection failed",
                ex.Message);
            return Diagnostics;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<GStreamerProtocolCapability> BuildProtocolCapabilities(
        IReadOnlySet<string> available)
    {
        var result = new List<GStreamerProtocolCapability>();
        foreach (var pair in ProtocolPlugins)
        {
            var missing = pair.Value.Where(plugin => !available.Contains(plugin)).ToArray();
            result.Add(new GStreamerProtocolCapability(
                pair.Key,
                missing.Length == 0,
                missing,
                missing.Length == 0
                    ? $"{DisplayName(pair.Key)} playback plugins are installed."
                    : $"Missing: {string.Join(", ", missing)}."));
        }
        return result;
    }

    private Dictionary<string, string?> BuildEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pluginPath = GStreamerExecutableLocator.ResolvePluginPath(_configuration.GStreamerPluginPath);
        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            environment["GST_PLUGIN_PATH"] = pluginPath;
        }
        return environment;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        foreach (var pair in environment)
        {
            if (pair.Value is null)
            {
                process.StartInfo.Environment.Remove(pair.Key);
            }
            else
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{executable}'.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(-1, string.Empty, $"'{Path.GetFileName(executable)}' did not finish within 10 seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static string DisplayName(VideoProtocolPreference protocol)
        => protocol switch
        {
            VideoProtocolPreference.WebRtc => "WebRTC/WHEP",
            VideoProtocolPreference.Hls => "HLS",
            VideoProtocolPreference.Rtsp => "RTSP",
            _ => protocol.ToString()
        };

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? "Unknown GStreamer runtime error.";

    public void Dispose() => _gate.Dispose();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
