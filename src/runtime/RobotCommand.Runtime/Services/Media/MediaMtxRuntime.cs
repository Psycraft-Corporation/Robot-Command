using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public sealed class MediaMtxRuntime(
    AppConfiguration configuration,
    ILogger<MediaMtxRuntime> logger) : IMediaMtxRuntime, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MediaMtxRuntimeDiagnostics Diagnostics { get; private set; } = MediaMtxRuntimeDiagnostics.Unknown;

    public async Task<MediaMtxRuntimeDiagnostics> InspectAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && Diagnostics.State is not MediaMtxRuntimeState.Unknown)
            {
                return Diagnostics;
            }

            var executable = MediaMtxExecutableLocator.Locate(configuration.MediaMtxBinPath);
            if (executable is null)
            {
                return Diagnostics = new(
                    MediaMtxRuntimeState.Missing,
                    "Playback server unavailable",
                    "No packaged or external playback server was found.");
            }

            var result = await RunAsync(executable.Path, cancellationToken);
            return Diagnostics = result.ExitCode == 0
                ? new(MediaMtxRuntimeState.Available, "Playback server available", "Ready", executable.Path, FirstLine(result.Output))
                : new(MediaMtxRuntimeState.Faulted, "Playback server unavailable", "The playback server could not start.", executable.Path, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Playback server inspection failed");
            return Diagnostics = new(MediaMtxRuntimeState.Faulted, "Playback server unavailable", "The runtime check failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string path, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        if (!process.Start())
        {
            return (-1, string.Empty);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, $"{await outputTask}\n{await errorTask}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return (-1, string.Empty);
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string? FirstLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
