using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services;

namespace RobotCommand.Services.Media;

public sealed class GStreamerVideoPipeline : IGStreamerVideoPipeline
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly IGStreamerRuntime _runtime;
    private readonly AppConfiguration _configuration;
    private readonly ILogger<GStreamerVideoPipeline> _logger;
    private readonly VideoFrameBuffer _frames = new();
    private Process? _process;
    private CancellationTokenSource? _pipelineCancellation;
    private TcpListener? _listener;
    private Task? _readerTask;
    private Task? _stderrTask;
    private NativeVideoPipelineStatus _status = NativeVideoPipelineStatus.Stopped;
    private int _generation;
    private int _disposed;

    public GStreamerVideoPipeline(
        IGStreamerRuntime runtime,
        AppConfiguration configuration,
        ILogger<GStreamerVideoPipeline> logger)
    {
        _runtime = runtime;
        _configuration = configuration;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public NativeVideoPipelineStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    public IVideoFrameSource Frames => _frames;

    public Task StartTestPatternAsync(
        GStreamerTestSourceOptions options,
        CancellationToken cancellationToken = default)
        => StartAsync(
            options.Output,
            port => GStreamerPipelineArguments.BuildTestPattern(options, port),
            "GStreamer test pattern",
            [],
            cancellationToken);

    public Task StartFileAsync(
        string path,
        GStreamerRawVideoOptions output,
        CancellationToken cancellationToken = default)
        => StartAsync(
            output,
            port => GStreamerPipelineArguments.BuildFile(path, output, port),
            $"Local media file: {Path.GetFileName(path)}",
            [],
            cancellationToken);

    public Task StartRtspAsync(
        RtspPlaybackOptions options,
        CancellationToken cancellationToken = default)
        => StartAsync(
            options.Output,
            port => GStreamerPipelineArguments.BuildRtsp(options, port),
            $"Logos RTSP stream {options.StreamId} from {options.CameraSourceId} at {GStreamerPipelineArguments.RedactEndpoint(options.Endpoint)}",
            ["rtspsrc"],
            cancellationToken);

    public Task StartSrtAsync(
        SrtPlaybackOptions options,
        CancellationToken cancellationToken = default)
        => StartAsync(
            options.Output,
            port => GStreamerPipelineArguments.BuildSrt(options, port),
            $"Logos SRT stream {options.StreamId} from {options.CameraSourceId} at {GStreamerPipelineArguments.RedactEndpoint(options.Endpoint)}",
            ["srtsrc", "tsdemux"],
            cancellationToken);

    public Task StartHlsAsync(
        HlsPlaybackOptions options,
        CancellationToken cancellationToken = default)
        => StartAsync(
            options.Output,
            port => GStreamerPipelineArguments.BuildHls(options, port),
            $"Logos HLS stream {options.StreamId} from {options.CameraSourceId} at {GStreamerPipelineArguments.RedactEndpoint(options.Endpoint)}",
            ["souphttpsrc", "hlsdemux"],
            cancellationToken);

    public Task StartWhepAsync(
        WhepPlaybackOptions options,
        CancellationToken cancellationToken = default)
        => StartAsync(
            options.Output,
            port => GStreamerPipelineArguments.BuildWhep(options, port),
            $"Logos WHEP stream {options.StreamId} from {options.CameraSourceId} at {GStreamerPipelineArguments.RedactEndpoint(options.Endpoint)}",
            ["whepsrc", GStreamerPipelineArguments.WhepDepayloader(options.Codec)],
            cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken, publishStopped: true);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _operationGate.WaitAsync();
        try
        {
            await StopCoreAsync(CancellationToken.None, publishStopped: false);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task StartAsync(
        GStreamerRawVideoOptions options,
        Func<int, IReadOnlyList<string>> argumentsFactory,
        string sourceDescription,
        IReadOnlyList<string> requiredPlugins,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken, publishStopped: false);
            UpdateStatus(new NativeVideoPipelineStatus(
                NativeVideoPipelineState.InspectingRuntime,
                "Inspecting GStreamer",
                "Checking the native runtime and required plugins before starting video.",
                DateTimeOffset.UtcNow));

            var diagnostics = await _runtime.InspectAsync(cancellationToken: cancellationToken);
            if (!diagnostics.Available || string.IsNullOrWhiteSpace(diagnostics.LaunchExecutable))
            {
                var error = diagnostics.Detail;
                UpdateStatus(new NativeVideoPipelineStatus(
                    NativeVideoPipelineState.Faulted,
                    "Native video unavailable",
                    error,
                    DateTimeOffset.UtcNow,
                    LastError: error));
                throw new InvalidOperationException(error);
            }

            var missingPlugins = requiredPlugins
                .Where(plugin => !diagnostics.HasPlugin(plugin))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingPlugins.Length > 0)
            {
                var error = $"The installed GStreamer runtime is missing required playback plugins: {string.Join(", ", missingPlugins)}.";
                UpdateStatus(new NativeVideoPipelineStatus(
                    NativeVideoPipelineState.Faulted,
                    "Native protocol unavailable",
                    error,
                    DateTimeOffset.UtcNow,
                    LastError: error));
                throw new InvalidOperationException(error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var loopbackPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var arguments = argumentsFactory(loopbackPort);
            var generation = ++_generation;
            var pipelineCancellation = new CancellationTokenSource();
            var process = CreateProcess(diagnostics.LaunchExecutable, arguments);
            lock (_stateGate)
            {
                _pipelineCancellation = pipelineCancellation;
                _listener = listener;
                _process = process;
            }

            var description = GStreamerPipelineArguments.Describe(arguments);
            UpdateStatus(new NativeVideoPipelineStatus(
                NativeVideoPipelineState.Starting,
                "Starting native video",
                sourceDescription,
                DateTimeOffset.UtcNow,
                description));

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("gst-launch-1.0 did not start.");
                }
            }
            catch
            {
                listener.Stop();
                pipelineCancellation.Dispose();
                lock (_stateGate)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                        _pipelineCancellation = null;
                        _listener = null;
                    }
                }

                process.Dispose();
                throw;
            }

            _stderrTask = ReadStandardErrorAsync(process, generation, pipelineCancellation.Token);
            _readerTask = ReadFramesAsync(
                process,
                listener,
                options,
                sourceDescription,
                generation,
                pipelineCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start the GStreamer native video pipeline");
            if (Status.State != NativeVideoPipelineState.Faulted)
            {
                UpdateStatus(new NativeVideoPipelineStatus(
                    NativeVideoPipelineState.Faulted,
                    "Native video failed",
                    ex.Message,
                    DateTimeOffset.UtcNow,
                    LastError: ex.Message));
            }

            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private Process CreateProcess(string executable, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var pluginPath = GStreamerExecutableLocator.ResolvePluginPath(_configuration.GStreamerPluginPath);
        if (!string.IsNullOrWhiteSpace(pluginPath))
        {
            process.StartInfo.Environment["GST_PLUGIN_PATH"] = pluginPath;
        }

        return process;
    }

    private async Task ReadFramesAsync(
        Process process,
        TcpListener listener,
        GStreamerRawVideoOptions options,
        string sourceDescription,
        int generation,
        CancellationToken cancellationToken)
    {
        var stride = checked(options.Width * 4);
        var frameSize = checked(stride * options.Height);
        var frame = new byte[frameSize];
        var firstFrame = true;

        try
        {
            using var client = await AcceptClientAsync(
                process,
                listener,
                cancellationToken);
            await using var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested && IsCurrent(generation, process))
            {
                var completed = await ReadExactlyAsync(
                    stream,
                    frame,
                    cancellationToken);
                if (!completed)
                {
                    break;
                }

                _frames.Publish(
                    frame,
                    options.Width,
                    options.Height,
                    stride,
                    DateTimeOffset.UtcNow);

                if (firstFrame)
                {
                    firstFrame = false;
                    UpdateStatus(new NativeVideoPipelineStatus(
                        NativeVideoPipelineState.Playing,
                        "Native video playing",
                        $"{sourceDescription} · {options.Width}×{options.Height} · {options.FrameRate} fps · BGRA",
                        DateTimeOffset.UtcNow,
                        Status.PipelineDescription));
                }
            }

            if (!cancellationToken.IsCancellationRequested && IsCurrent(generation, process))
            {
                await process.WaitForExitAsync(CancellationToken.None);
                var detail = process.ExitCode == 0
                    ? "The GStreamer source ended."
                    : $"gst-launch-1.0 exited with code {process.ExitCode}.";
                UpdateStatus(new NativeVideoPipelineStatus(
                    process.ExitCode == 0
                        ? NativeVideoPipelineState.Stopped
                        : NativeVideoPipelineState.Faulted,
                    process.ExitCode == 0 ? "Native video ended" : "Native video process failed",
                    detail,
                    DateTimeOffset.UtcNow,
                    Status.PipelineDescription,
                    process.ExitCode == 0 ? null : detail));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation, process))
            {
                _logger.LogWarning(ex, "The GStreamer frame reader failed");
                UpdateStatus(new NativeVideoPipelineStatus(
                    NativeVideoPipelineState.Faulted,
                    "Native frame reader failed",
                    ex.Message,
                    DateTimeOffset.UtcNow,
                    Status.PipelineDescription,
                    ex.Message));
            }
        }
    }

    private async Task ReadStandardErrorAsync(
        Process process,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsCurrent(generation, process))
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogDebug("GStreamer: {Line}", GStreamerPipelineArguments.RedactText(line));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopCoreAsync(
        CancellationToken cancellationToken,
        bool publishStopped)
    {
        Process? process;
        CancellationTokenSource? pipelineCancellation;
        TcpListener? listener;
        Task? readerTask;
        Task? stderrTask;
        lock (_stateGate)
        {
            process = _process;
            pipelineCancellation = _pipelineCancellation;
            listener = _listener;
            readerTask = _readerTask;
            stderrTask = _stderrTask;
            _process = null;
            _pipelineCancellation = null;
            _listener = null;
            _readerTask = null;
            _stderrTask = null;
            _generation++;
        }

        if (process is null && pipelineCancellation is null && listener is null)
        {
            _frames.Clear();
            if (publishStopped)
            {
                UpdateStatus(NativeVideoPipelineStatus.Stopped with { UpdatedAt = DateTimeOffset.UtcNow });
            }

            return;
        }

        UpdateStatus(new NativeVideoPipelineStatus(
            NativeVideoPipelineState.Stopping,
            "Stopping native video",
            "Closing the GStreamer process and local frame stream.",
            DateTimeOffset.UtcNow,
            Status.PipelineDescription));

        pipelineCancellation?.Cancel();
        listener?.Stop();
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is
                InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
            {
                _logger.LogDebug(ex, "The GStreamer process had already stopped");
            }
        }

        var tasks = new[] { readerTask, stderrTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        var callerCancelled = false;
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Timed out waiting for GStreamer reader tasks to stop");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A GStreamer reader task stopped with an error");
            }
        }

        process?.Dispose();
        pipelineCancellation?.Dispose();
        _frames.Clear();
        if (publishStopped)
        {
            UpdateStatus(NativeVideoPipelineStatus.Stopped with { UpdatedAt = DateTimeOffset.UtcNow });
        }

        if (callerCancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private bool IsCurrent(int generation, Process process)
    {
        lock (_stateGate)
        {
            return generation == _generation && ReferenceEquals(_process, process);
        }
    }

    private void UpdateStatus(NativeVideoPipelineStatus status)
    {
        lock (_stateGate)
        {
            _status = status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }


    private static async Task<TcpClient> AcceptClientAsync(
        Process process,
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(acceptTask, exitTask);
            if (completed == exitTask)
            {
                await exitTask;
                throw new IOException(
                    $"gst-launch-1.0 exited with code {process.ExitCode} before opening the local frame stream.");
            }

            return await acceptTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
