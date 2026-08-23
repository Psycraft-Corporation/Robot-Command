using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Services;

namespace RobotCommand.Services.Connections;

public sealed class ConnectionSupervisorService : BackgroundService
{
    private readonly ILogosConnectionManager _connectionManager;
    private readonly AppConfiguration _configuration;
    private readonly ILogger<ConnectionSupervisorService> _logger;

    public ConnectionSupervisorService(
        ILogosConnectionManager connectionManager,
        AppConfiguration configuration,
        ILogger<ConnectionSupervisorService> logger)
    {
        _connectionManager = connectionManager;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _connectionManager.StartAutoConnectionsAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_configuration.RefreshSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _connectionManager.SuperviseAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The Logos connection supervisor iteration failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
