using Grpc.Core;
using Grpc.Net.Client;
using Logos.Api.V1;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public sealed class LogosOperationalSession : ILogosOperationalSession
{
    private static readonly TimeSpan CachedInspectionAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _inspectionGate = new(1, 1);
    private readonly GrpcChannel _channel;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LogosOperationalSession> _logger;
    private OperationalApiSnapshot _status;
    private int _disposed;

    public LogosOperationalSession(
        ConnectionDefinition definition,
        GrpcChannel channel,
        HttpClient httpClient,
        LogosOperationalClients clients,
        ILogger<LogosOperationalSession> logger)
    {
        Definition = definition;
        _channel = channel;
        _httpClient = httpClient;
        Clients = clients;
        _logger = logger;
        _status = OperationalApiSnapshot.Unknown(definition.Id);
    }

    public ConnectionDefinition Definition { get; }

    public LogosOperationalClients Clients { get; }

    public OperationalApiSnapshot Status => Volatile.Read(ref _status);

    public async Task<OperationalApiSnapshot> InspectAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _inspectionGate.WaitAsync(cancellationToken);
        try
        {
            var current = Status;
            if (!force &&
                current.InspectedAt != DateTimeOffset.MinValue &&
                DateTimeOffset.UtcNow - current.InspectedAt <= CachedInspectionAge)
            {
                return current;
            }

            var capabilitiesTask = InspectCapabilitiesAsync(cancellationToken);
            var missionTask = ProbeMissionAsync(cancellationToken);
            var taskTask = ProbeTaskAsync(cancellationToken);
            var autonomyTask = ProbeAutonomyAsync(cancellationToken);
            var geometryTask = ProbeGeometryAsync(cancellationToken);
            var policyTask = ProbePolicyAsync(cancellationToken);
            var sensorsTask = ProbeSensorsAsync(cancellationToken);
            var vehicleOperationsTask = ProbeVehicleOperationsAsync(cancellationToken);

            var capabilities = await capabilitiesTask;
            var domains = new[]
            {
                capabilities.SystemStatus,
                AttachCapabilities(await missionTask, capabilities, "mission."),
                AttachCapabilities(await taskTask, capabilities, "task."),
                AttachCapabilities(await autonomyTask, capabilities, "autonomy."),
                AttachCapabilities(await geometryTask, capabilities, "geometry."),
                AttachCapabilities(await policyTask, capabilities, "policy."),
                AttachCapabilities(await sensorsTask, capabilities, "sensors."),
                AttachCapabilities(await vehicleOperationsTask, capabilities, "vehicle.")
            };
            var snapshot = new OperationalApiSnapshot(
                Definition.Id,
                DateTimeOffset.UtcNow,
                domains,
                capabilities.AvailableKeys,
                capabilities.UnavailableKeys);
            Volatile.Write(ref _status, snapshot);

            _logger.LogInformation(
                "Operational API inspection for {ConnectionId}: {Summary}",
                Definition.Id,
                snapshot.Summary);
            return snapshot;
        }
        finally
        {
            _inspectionGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _channel.Dispose();
        _httpClient.Dispose();
        _inspectionGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<CapabilityInspection> InspectCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.System.GetCapabilitiesAsync(
                new GetCapabilitiesRequest { IncludeUnavailable = true },
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            var status = FromDomainStatus(
                OperationalApiDomain.System,
                response.Status,
                "System capability API available");
            var available = response.Capabilities
                .Where(item => item.Available && !string.IsNullOrWhiteSpace(item.CapabilityKey))
                .Select(item => item.CapabilityKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var unavailable = response.Capabilities
                .Where(item => !item.Available && !string.IsNullOrWhiteSpace(item.CapabilityKey))
                .Select(item => item.CapabilityKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            return new CapabilityInspection(status, available, unavailable);
        }
        catch (RpcException ex)
        {
            return new CapabilityInspection(
                FromRpcException(OperationalApiDomain.System, ex),
                [],
                []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CapabilityInspection(
                Faulted(OperationalApiDomain.System, ex),
                [],
                []);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbeMissionAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.Missions.ListMissionsAsync(
                new ListMissionsRequest(),
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return FromDomainStatus(
                OperationalApiDomain.Mission,
                response.Status,
                "Mission API available");
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.Mission, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.Mission, ex);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbeTaskAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.Tasks.ListTasksAsync(
                new ListTasksRequest(),
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return FromDomainStatus(
                OperationalApiDomain.Task,
                response.Status,
                "Task API available");
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.Task, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.Task, ex);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbeAutonomyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.Autonomy.GetAutonomyRuntimeStatusAsync(
                new GetAutonomyRuntimeStatusRequest
                {
                    IncludeBehaviour = true,
                    IncludeStatechart = true,
                    IncludeGeometryBindings = true,
                    IncludeTreeNodes = false,
                    IncludeDetails = false
                },
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return FromDomainStatus(
                OperationalApiDomain.Autonomy,
                response.Status,
                "Autonomy API available");
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.Autonomy, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.Autonomy, ex);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbeGeometryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.Geometry.ListGeometryObjectsAsync(
                new ListGeometryObjectsRequest
                {
                    IncludeObjects = false,
                    Refresh = false
                },
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return FromDomainStatus(
                OperationalApiDomain.Geometry,
                response.Status,
                "Geometry API available");
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.Geometry, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.Geometry, ex);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbePolicyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.Policy.ListPolicyProfilesAsync(
                new ListPolicyProfilesRequest
                {
                    IncludeDeprecated = false,
                    IncludeProfileDocument = false
                },
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return FromDomainStatus(
                OperationalApiDomain.Policy,
                response.Status,
                "Policy API available");
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.Policy, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.Policy, ex);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbeSensorsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Clients.Sensors.ListSensorsAsync(
                new ListSensorsRequest { IncludeStatus = false },
                deadline: Deadline(),
                cancellationToken: cancellationToken);
            return FromDomainStatus(
                OperationalApiDomain.Sensors,
                response.Status,
                "Sensors API available");
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.Sensors, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.Sensors, ex);
        }
    }

    private async Task<OperationalApiDomainStatus> ProbeVehicleOperationsAsync(
        CancellationToken cancellationToken)
    {
        var client = Clients.VehicleOperations;
        if (client is null)
        {
            return new OperationalApiDomainStatus(
                OperationalApiDomain.VehicleOperations,
                OperationalApiAvailability.Unimplemented,
                "Vehicle Operations API not present in the SDK",
                "Regenerate Psycraft.Logos.Api.Sdk with vehicle_operations.proto.",
                []);
        }

        try
        {
            var requestId = $"robot-command-probe-{Guid.NewGuid():N}";
            var response = await client.GetOperationalReadinessAsync(
                new GetOperationalReadinessRequest
                {
                    Command = new CommandRequestMetadata
                    {
                        RequestId = new RequestId { RequestId_ = requestId },
                        CorrelationId = new CorrelationId { CorrelationId_ = requestId },
                        ClientName = "Robot Command"
                    },
                    OperationKind = VehicleOperationKind.Hold
                },
                deadline: Deadline(),
                cancellationToken: cancellationToken);

            // A domain-level validation response still proves the service is
            // reachable; a real target is intentionally not invented for a probe.
            return new OperationalApiDomainStatus(
                OperationalApiDomain.VehicleOperations,
                OperationalApiAvailability.Available,
                "Vehicle Operations API available",
                string.IsNullOrWhiteSpace(response.Status?.Message)
                    ? "The generated client reached VehicleOperationsService."
                    : response.Status.Message,
                []);
        }
        catch (RpcException ex) when (ex.StatusCode is
                   StatusCode.InvalidArgument or
                   StatusCode.FailedPrecondition or
                   StatusCode.PermissionDenied or
                   StatusCode.Unauthenticated)
        {
            return new OperationalApiDomainStatus(
                OperationalApiDomain.VehicleOperations,
                OperationalApiAvailability.Available,
                "Vehicle Operations API available",
                string.IsNullOrWhiteSpace(ex.Status.Detail)
                    ? "The service is reachable and requires a valid vehicle target."
                    : ex.Status.Detail,
                []);
        }
        catch (RpcException ex)
        {
            return FromRpcException(OperationalApiDomain.VehicleOperations, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(OperationalApiDomain.VehicleOperations, ex);
        }
    }

    private static OperationalApiDomainStatus FromDomainStatus(
        OperationalApiDomain domain,
        DomainStatus? status,
        string availableSummary)
    {
        if (status is null)
        {
            return new OperationalApiDomainStatus(
                domain,
                OperationalApiAvailability.Faulted,
                $"{domain} API returned no domain status",
                "The RPC completed but Logos did not return DomainStatus.",
                []);
        }

        if (status.Ok)
        {
            return new OperationalApiDomainStatus(
                domain,
                OperationalApiAvailability.Available,
                availableSummary,
                string.IsNullOrWhiteSpace(status.Message)
                    ? "The generated client completed a read-only probe successfully."
                    : status.Message,
                []);
        }

        var code = status.Code.ToString();
        var unavailable = code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
                          code.Contains("NotReady", StringComparison.OrdinalIgnoreCase);
        return new OperationalApiDomainStatus(
            domain,
            unavailable
                ? OperationalApiAvailability.Unavailable
                : OperationalApiAvailability.Faulted,
            $"{domain} API rejected the probe",
            string.IsNullOrWhiteSpace(status.Message) ? code : $"{code}: {status.Message}",
            []);
    }

    private static OperationalApiDomainStatus FromRpcException(
        OperationalApiDomain domain,
        RpcException exception)
    {
        var availability = exception.StatusCode switch
        {
            StatusCode.Unimplemented => OperationalApiAvailability.Unimplemented,
            StatusCode.Unavailable or StatusCode.DeadlineExceeded => OperationalApiAvailability.Unreachable,
            _ => OperationalApiAvailability.Faulted
        };
        var summary = availability switch
        {
            OperationalApiAvailability.Unimplemented => $"{domain} API not implemented",
            OperationalApiAvailability.Unreachable => $"{domain} API unreachable",
            _ => $"{domain} API probe failed"
        };
        return new OperationalApiDomainStatus(
            domain,
            availability,
            summary,
            string.IsNullOrWhiteSpace(exception.Status.Detail)
                ? exception.StatusCode.ToString()
                : exception.Status.Detail,
            []);
    }

    private static OperationalApiDomainStatus Faulted(
        OperationalApiDomain domain,
        Exception exception)
        => new(
            domain,
            OperationalApiAvailability.Faulted,
            $"{domain} API inspection failed",
            exception.Message,
            []);

    private static OperationalApiDomainStatus AttachCapabilities(
        OperationalApiDomainStatus status,
        CapabilityInspection capabilities,
        string prefix)
    {
        var keys = capabilities.AvailableKeys
            .Concat(capabilities.UnavailableKeys)
            .Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return status with { CapabilityKeys = keys };
    }

    private static DateTime Deadline() => DateTime.UtcNow.Add(RpcTimeout);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private sealed record CapabilityInspection(
        OperationalApiDomainStatus SystemStatus,
        IReadOnlyList<string> AvailableKeys,
        IReadOnlyList<string> UnavailableKeys);
}
