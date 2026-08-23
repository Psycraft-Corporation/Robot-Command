using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RobotCommand.Simulation;

namespace RobotCommand.Services.Simulation;

public interface IGhostSimulationWorkerSupervisor : IAsyncDisposable
{
    bool IsRunning { get; }
    bool IsHealthy { get; }
    DateTimeOffset? LastHeartbeat { get; }
    SimulationSnapshot? LatestSnapshot { get; }
    bool RefreshSnapshot();
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<SimulationCommandResult> SendAsync(SimulationCommand command, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns one portable simulator process for a Runtime session.  This boundary
/// is deliberately independent of the existing store-backed Ghost adapter so
/// the worker can be enabled for production only after parity checks complete.
/// </summary>
public sealed class GhostSimulationWorkerSupervisor : IGhostSimulationWorkerSupervisor
{
    private readonly ILogger<GhostSimulationWorkerSupervisor>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private Process? _process;
    private LocalControlClient? _client;
    private SharedSnapshotChannel? _snapshots;
    private string? _sessionDirectory;
    private SimulationSnapshot? _latestSnapshot;
    private int _disposed;

    public GhostSimulationWorkerSupervisor(ILogger<GhostSimulationWorkerSupervisor>? logger = null)
        => _logger = logger;

    public bool IsRunning => _process is { HasExited: false } && _client is not null;
    public DateTimeOffset? LastHeartbeat => _latestSnapshot?.HeartbeatAt;
    public bool IsHealthy => IsRunning && _latestSnapshot is { } snapshot &&
        DateTimeOffset.UtcNow - snapshot.HeartbeatAt <= TimeSpan.FromMilliseconds(250);
    public SimulationSnapshot? LatestSnapshot => _latestSnapshot;

    public bool RefreshSnapshot()
    {
        if (_process is { HasExited: true }) return false;
        if (_snapshots is null) return false;
        var updated = _snapshots.TryRead(out var snapshot);
        if (updated && snapshot is not null) _latestSnapshot = snapshot;
        return updated;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            if (_process is not null) throw new InvalidOperationException("The Ghost simulation worker is unavailable.");

            _sessionDirectory = Path.Combine(Path.GetTempPath(), $"robotcommand-simulation-{_sessionId}");
            Directory.CreateDirectory(_sessionDirectory);
            var snapshotPath = Path.Combine(_sessionDirectory, "snapshots.bin");
            var endpoint = OperatingSystem.IsWindows()
                ? $"robotcommand-sim-{_sessionId}"
                : Path.Combine(_sessionDirectory, "control.sock");
            var startInfo = CreateStartInfo(snapshotPath, endpoint);
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("The Ghost simulation worker could not be started.");
            _client = new LocalControlClient(endpoint);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            Exception? lastError = null;
            var connected = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    if (_snapshots is null && File.Exists(snapshotPath))
                        _snapshots = SharedSnapshotChannel.OpenReader(snapshotPath);
                    if (!connected)
                    {
                        await _client.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                        connected = true;
                    }
                    if (_snapshots is not null && _snapshots.TryRead(out _latestSnapshot)) return;
                    await Task.Delay(25, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException or TimeoutException)
                {
                    lastError = exception;
                    if (connected)
                    {
                        try { await _client.DisposeAsync(); } catch { }
                        _client = new LocalControlClient(endpoint);
                        connected = false;
                    }
                    await Task.Delay(50, cancellationToken);
                }
            }
            throw new InvalidOperationException("The Ghost simulation worker did not complete its handshake.", lastError);
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<SimulationCommandResult> SendAsync(SimulationCommand command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsRunning || _client is null) return new(command.RequestId, false, "WORKER_UNAVAILABLE", "The Ghost simulation worker is unavailable.", 0);
            var result = await _client.SendAsync(command, cancellationToken);
            _snapshots?.TryRead(out _latestSnapshot);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            try { await _client.SendAsync(new($"shutdown-{Guid.NewGuid():N}", SimulationCommandKind.Shutdown), cancellationToken).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (Exception exception) { _logger?.LogDebug(exception, "Ghost simulation worker did not acknowledge shutdown."); }
        }
        if (_process is not null)
        {
            try { await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }
            _process.Dispose();
        }
        if (_client is not null) await _client.DisposeAsync();
        _snapshots?.Dispose();
        _process = null;
        _client = null;
        _snapshots = null;
        if (_sessionDirectory is not null)
        {
            for (var attempt = 0; attempt < 10 && Directory.Exists(_sessionDirectory); attempt++)
            {
                try
                {
                    Directory.Delete(_sessionDirectory, recursive: true);
                }
                catch (IOException)
                {
                    try { File.Delete(Path.Combine(_sessionDirectory, "snapshots.bin")); } catch { }
                    try { File.Delete(Path.Combine(_sessionDirectory, "control.sock")); } catch { }
                    await Task.Delay(25, cancellationToken);
                }
                catch (UnauthorizedAccessException) { break; }
            }
        }
        _sessionDirectory = null;
    }

    private static ProcessStartInfo CreateStartInfo(string snapshotPath, string endpoint)
    {
        var configured = Environment.GetEnvironmentVariable("ROBOT_COMMAND_SIMULATOR_PATH");
        var executable = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RobotCommand.Simulator.exe" : "RobotCommand.Simulator");
        var arguments = $"--snapshot \"{snapshotPath}\" --endpoint \"{endpoint}\"";
        if (File.Exists(executable) && !executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new() { FileName = executable, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true };

        var dll = !string.IsNullOrWhiteSpace(configured) && configured.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "RobotCommand.Simulator.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("The bundled Ghost simulation worker was not found.", executable);
        return new() { FileName = "dotnet", Arguments = $"\"{dll}\" {arguments}", UseShellExecute = false, CreateNoWindow = true };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
