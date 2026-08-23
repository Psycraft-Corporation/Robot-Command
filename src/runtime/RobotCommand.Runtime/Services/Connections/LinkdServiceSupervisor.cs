using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RobotCommand.Services.Connections;

public enum LinkdServiceStartupState
{
    NotInstalled,
    AlreadyRunning,
    Started,
    PermissionDenied,
    Failed,
    UnsupportedPlatform
}

public sealed record LinkdServiceStartupResult(
    LinkdServiceStartupState State,
    string Message);

/// <summary>
/// Starts an already-installed LinkD service. Installation, configuration, and
/// service removal remain responsibilities of the LinkD MSI.
/// </summary>
public interface ILinkdServiceController
{
    Task<LinkdServiceStartupResult> EnsureRunningAsync(CancellationToken cancellationToken = default);
}

public sealed class WindowsLinkdServiceController : ILinkdServiceController
{
    public const string ServiceName = "LogosLinkd";

    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _pollInterval;

    public WindowsLinkdServiceController(
        TimeSpan? startupTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(10);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public async Task<LinkdServiceStartupResult> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(LinkdServiceStartupState.UnsupportedPlatform,
                "LinkD Windows service activation is unavailable on this platform.");
        }

        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();

            if (service.Status == ServiceControllerStatus.Running)
            {
                return new(LinkdServiceStartupState.AlreadyRunning,
                    $"The {ServiceName} service is already running.");
            }

            if (service.Status is ServiceControllerStatus.StopPending or ServiceControllerStatus.StartPending)
            {
                await WaitForRunningAsync(service, cancellationToken);
                return new(LinkdServiceStartupState.Started,
                    $"The {ServiceName} service is now running.");
            }

            service.Start();
            await WaitForRunningAsync(service, cancellationToken);
            return new(LinkdServiceStartupState.Started,
                $"The {ServiceName} service was started.");
        }
        catch (InvalidOperationException ex) when (IsMissingService(ex))
        {
            return new(LinkdServiceStartupState.NotInstalled,
                $"The {ServiceName} Windows service is not installed.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1060)
        {
            return new(LinkdServiceStartupState.NotInstalled,
                $"The {ServiceName} Windows service is not installed.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 1300)
        {
            return new(LinkdServiceStartupState.PermissionDenied,
                $"Windows denied permission to start the {ServiceName} service. Install or grant service-start permission, then retry.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(LinkdServiceStartupState.Failed,
                $"Could not start the {ServiceName} service: {ex.Message}");
        }
    }

    private async Task WaitForRunningAsync(
        ServiceController service,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _startupTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Refresh();

            if (service.Status == ServiceControllerStatus.Running)
            {
                return;
            }

            if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                throw new InvalidOperationException(
                    $"The {ServiceName} service stopped before reaching the running state.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new System.TimeoutException(
                    $"The {ServiceName} service did not reach the running state within {_startupTimeout.TotalSeconds:N0} seconds.");
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private static bool IsMissingService(InvalidOperationException exception)
        => exception.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("cannot be found", StringComparison.OrdinalIgnoreCase)
           || exception.InnerException is InvalidOperationException inner && IsMissingService(inner);
}

/// <summary>
/// Ensures LinkD is available before connection auto-connect begins. It never
/// stops LinkD during application shutdown because the daemon may be shared by
/// other clients.
/// </summary>
public sealed class LinkdServiceStartupService : IHostedService
{
    private readonly ILinkdServiceController _controller;
    private readonly ILogger<LinkdServiceStartupService> _logger;

    public LinkdServiceStartupService(
        ILinkdServiceController controller,
        ILogger<LinkdServiceStartupService> logger)
    {
        _controller = controller;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await _controller.EnsureRunningAsync(cancellationToken);

        if (result.State is LinkdServiceStartupState.AlreadyRunning or LinkdServiceStartupState.Started)
        {
            _logger.LogInformation("{Message}", result.Message);
        }
        else
        {
            _logger.LogWarning("{Message}", result.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
