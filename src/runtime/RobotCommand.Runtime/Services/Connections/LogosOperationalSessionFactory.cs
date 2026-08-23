using System.Net.Http.Headers;
using Grpc.Net.Client;
using Logos.Api.V1;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public sealed class LogosOperationalSessionFactory : ILogosOperationalSessionFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public LogosOperationalSessionFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public ILogosOperationalSession Create(
        ConnectionDefinition definition,
        ConnectionCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!Uri.TryCreate(definition.Target, UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"The Logos target for '{definition.Name}' must be an absolute HTTP or HTTPS URI.");
        }

        var normalized = credentials.Normalize();
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };
        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Robot-Command/1.0");

        if (!string.IsNullOrWhiteSpace(normalized.BearerToken))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", normalized.BearerToken);
        }

        if (!string.IsNullOrWhiteSpace(normalized.ApiKey))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", normalized.ApiKey);
        }

        var channel = GrpcChannel.ForAddress(target, new GrpcChannelOptions
        {
            HttpClient = httpClient
        });
        var clients = new LogosOperationalClients(
            new SystemService.SystemServiceClient(channel),
            new MissionService.MissionServiceClient(channel),
            new TaskService.TaskServiceClient(channel),
            new AutonomyService.AutonomyServiceClient(channel),
            new GeometryService.GeometryServiceClient(channel),
            new PolicyService.PolicyServiceClient(channel),
            new SensorsService.SensorsServiceClient(channel))
        {
            VehicleOperations = new VehicleOperationsService.VehicleOperationsServiceClient(channel)
        };

        return new LogosOperationalSession(
            definition,
            channel,
            httpClient,
            clients,
            _loggerFactory.CreateLogger<LogosOperationalSession>());
    }
}
