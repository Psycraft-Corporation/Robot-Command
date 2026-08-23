using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Missions;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Geometry;

public sealed class LogosGeometryGateway : IGeometryGateway
{
    private const int PageSize = 200;
    private const int MaximumPages = 100;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WatchHeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly GeometryProtoMapper _mapper;
    private readonly ILogger<LogosGeometryGateway> _logger;

    public LogosGeometryGateway(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata,
        GeometryProtoMapper mapper,
        ILogger<LogosGeometryGateway> logger)
    {
        _sessions = sessions;
        _metadata = metadata;
        _mapper = mapper;
        _logger = logger;
    }

    public bool IsAvailable => _sessions.ConnectionIds.Any(ConnectionSupportsGeometry);

    public string AvailabilityMessage
    {
        get
        {
            if (_sessions.ConnectionIds.Count == 0)
            {
                return "Connect to a Logos runtime to use geometry registry operations.";
            }

            var compatible = _sessions.ConnectionIds.Count(ConnectionSupportsGeometry);
            return compatible > 0
                ? $"Geometry registry operations are available on {compatible} connected Logos runtime(s)."
                : "Connected Logos runtimes do not currently expose GeometryService.";
        }
    }

    public async Task<IReadOnlyList<RemoteGeometryRecord>> ListAsync(
        string connectionId,
        GeometryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = await RequireSessionAsync(connectionId, cancellationToken);
        var records = new Dictionary<string, RemoteGeometryRecord>(StringComparer.Ordinal);
        var pageToken = string.Empty;

        for (var pageIndex = 0; pageIndex < MaximumPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new V1.ListGeometryObjectsRequest
            {
                RequestId = _metadata.CreateRequestId(),
                CorrelationId = _metadata.CreateCorrelationId(),
                Page = new V1.PageRequest
                {
                    PageSize = PageSize,
                    PageToken = pageToken
                },
                IncludeObjects = query.IncludeObjects,
                Refresh = query.Refresh && pageIndex == 0
            };
            AddFilters(request, query);

            var response = await session.Clients.Geometry.ListGeometryObjectsAsync(
                request,
                deadline: ReadDeadline(),
                cancellationToken: cancellationToken);
            EnsureSuccessful(response.Status, "list geometry objects");
            var revisions = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var geometry in response.Objects)
            {
                if (!string.IsNullOrWhiteSpace(geometry.GeometryId))
                {
                    revisions[geometry.GeometryId] = geometry.Metadata?.Revision;
                }
            }
            foreach (var record in response.Records)
            {
                if (!string.IsNullOrWhiteSpace(record.GeometryId))
                {
                    revisions.TryGetValue(record.GeometryId, out var revision);
                    records[record.GeometryId] = GeometryProtoMapper.ToRecord(record, connectionId, revision);
                }
            }
            foreach (var geometry in response.Objects)
            {
                if (!string.IsNullOrWhiteSpace(geometry.GeometryId) && !records.ContainsKey(geometry.GeometryId))
                {
                    records[geometry.GeometryId] = _mapper.ToRemoteObject(geometry, null, connectionId).Record;
                }
            }

            var next = response.Page?.NextPageToken ?? string.Empty;
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next, pageToken, StringComparison.Ordinal))
            {
                break;
            }
            pageToken = next;
        }

        return records.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<RemoteGeometryObject?> GetAsync(
        string connectionId,
        string geometryId,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        RequireGeometryId(geometryId);
        var session = await RequireSessionAsync(connectionId, cancellationToken);
        var response = await session.Clients.Geometry.GetGeometryObjectAsync(
            new V1.GetGeometryObjectRequest
            {
                RequestId = _metadata.CreateRequestId(),
                CorrelationId = _metadata.CreateCorrelationId(),
                GeometryId = geometryId.Trim(),
                Refresh = refresh
            },
            deadline: ReadDeadline(),
            cancellationToken: cancellationToken);
        if (response.Status?.Code == V1.DomainCode.NotFound)
        {
            return null;
        }
        EnsureSuccessful(response.Status, $"get geometry '{geometryId}'");
        if (response.Object is null)
        {
            throw new InvalidOperationException(
                $"Logos returned no geometry object for '{geometryId}'.");
        }
        return _mapper.ToRemoteObject(response.Object, response.Record, connectionId);
    }

    public async Task<GeometryValidationResult> ValidateAsync(
        string connectionId,
        GeometryDocument document,
        bool checkUpdateCompatibility = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            var session = await RequireSessionAsync(connectionId, cancellationToken);
            var response = await session.Clients.Geometry.ValidateGeometryObjectAsync(
                new V1.ValidateGeometryObjectRequest
                {
                    Command = _metadata.Create(
                        "geometry.validate",
                        document.GeometryId),
                    Object = _mapper.ToProto(document),
                    Options = ValidationOptions(checkUpdateCompatibility)
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return GeometryProtoMapper.ToValidationResult(
                response.Validation,
                response.Status,
                response.Authorization);
        }
        catch (GeometryGatewayUnavailableException ex)
        {
            return GeometryValidationResult.Unavailable(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Geometry validation RPC failed for {GeometryId}", document.GeometryId);
            return ValidationFromRpc(ex);
        }
    }

    public async Task<GeometryCommandResult> CreateAsync(
        GeometryCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
            var response = await session.Clients.Geometry.CreateGeometryObjectAsync(
                new V1.CreateGeometryObjectRequest
                {
                    Command = CommandMetadata("geometry.create", request.Document.GeometryId, request),
                    Object = _mapper.ToProto(request.Document),
                    ValidateOnCreate = request.ValidateOnCreate
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return _mapper.ToCreateResult(response, request.ConnectionId);
        }
        catch (GeometryGatewayUnavailableException ex)
        {
            return Rejected(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Create geometry RPC failed for {GeometryId}", request.Document.GeometryId);
            return CommandFromRpc("Create geometry", ex);
        }
    }

    public async Task<GeometryCommandResult> UpdateAsync(
        GeometryUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
            var response = await session.Clients.Geometry.UpdateGeometryObjectAsync(
                new V1.UpdateGeometryObjectRequest
                {
                    Command = CommandMetadata("geometry.update", request.Document.GeometryId, request),
                    GeometryId = request.Document.GeometryId,
                    ExpectedRevision = request.ExpectedRevision ?? string.Empty,
                    Object = _mapper.ToProto(request.Document),
                    ValidateOnUpdate = request.ValidateOnUpdate
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return _mapper.ToUpdateResult(response, request.ConnectionId);
        }
        catch (GeometryGatewayUnavailableException ex)
        {
            return Rejected(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Update geometry RPC failed for {GeometryId}", request.Document.GeometryId);
            return CommandFromRpc("Update geometry", ex);
        }
    }

    public async Task<GeometryCommandResult> DeleteAsync(
        GeometryDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireGeometryId(request.GeometryId);
        try
        {
            var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
            var response = await session.Clients.Geometry.DeleteGeometryObjectAsync(
                new V1.DeleteGeometryObjectRequest
                {
                    Command = CommandMetadata("geometry.delete", request.GeometryId, request),
                    GeometryId = request.GeometryId.Trim(),
                    AllowDeleteReferenced = request.AllowDeleteReferenced
                },
                deadline: CommandDeadline(),
                cancellationToken: cancellationToken);
            return GeometryProtoMapper.ToDeleteResult(response);
        }
        catch (GeometryGatewayUnavailableException ex)
        {
            return Rejected(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Delete geometry RPC failed for {GeometryId}", request.GeometryId);
            return CommandFromRpc("Delete geometry", ex);
        }
    }

    public async Task<GeometryRegistrySnapshot> GetRegistryStatusAsync(
        string connectionId,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await RequireSessionAsync(connectionId, cancellationToken);
            var response = await session.Clients.Geometry.GetGeometryRegistryStatusAsync(
                new V1.GetGeometryRegistryStatusRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    IncludeDetails = includeDetails
                },
                deadline: ReadDeadline(),
                cancellationToken: cancellationToken);
            EnsureSuccessful(response.Status, "get geometry registry status");
            return GeometryProtoMapper.ToRegistrySnapshot(response.RegistryStatus, connectionId);
        }
        catch (GeometryGatewayUnavailableException ex)
        {
            return GeometryRegistrySnapshot.Unknown(connectionId, ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Geometry registry status RPC failed for {ConnectionId}", connectionId);
            return GeometryRegistrySnapshot.Unknown(
                connectionId,
                RpcMessage("Get geometry registry status", ex));
        }
    }

    public async IAsyncEnumerable<GeometryRegistryEvent> WatchAsync(
        string connectionId,
        GeometryQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = await RequireSessionAsync(connectionId, cancellationToken);
        var request = new V1.WatchGeometryRegistryRequest
        {
            RequestId = _metadata.CreateRequestId(),
            CorrelationId = _metadata.CreateCorrelationId(),
            IncludeHeartbeats = true,
            HeartbeatInterval = Duration.FromTimeSpan(WatchHeartbeatInterval),
            IncludeObjects = query.IncludeObjects,
            IncludeDetails = true
        };
        AddFilters(request, query);

        using var call = session.Clients.Geometry.WatchGeometryRegistry(
            request,
            cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return _mapper.ToRegistryEvent(call.ResponseStream.Current, connectionId);
        }
    }

    private async Task<ILogosOperationalSession> RequireSessionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new GeometryGatewayUnavailableException("A Logos connection is required.");
        }

        if (!_sessions.TryGet(connectionId, out var session) || session is null)
        {
            throw new GeometryGatewayUnavailableException(
                $"Connection '{connectionId}' has no active Logos operational session.");
        }

        var snapshot = session.Status;
        var status = snapshot.Get(OperationalApiDomain.Geometry);
        if (snapshot.InspectedAt == DateTimeOffset.MinValue ||
            status.Availability is OperationalApiAvailability.Unknown or OperationalApiAvailability.Inspecting)
        {
            snapshot = await session.InspectAsync(cancellationToken: cancellationToken);
            status = snapshot.Get(OperationalApiDomain.Geometry);
        }

        if (!status.Available)
        {
            throw new GeometryGatewayUnavailableException(
                $"Geometry API is not available on connection '{connectionId}': {status.Detail}");
        }

        return session;
    }

    private bool ConnectionSupportsGeometry(string connectionId)
        => _sessions.TryGet(connectionId, out var session) &&
           session is not null &&
           session.Status.IsAvailable(OperationalApiDomain.Geometry);

    private static void AddFilters(
        V1.ListGeometryObjectsRequest request,
        GeometryQuery query)
    {
        request.Kinds.Add((query.Kinds ?? [])
            .Select(GeometryProtoMapper.ToProto)
            .Where(item => item != V1.GeometryKind.Unspecified)
            .Distinct());
        request.FrameScopes.Add((query.Frames ?? [])
            .Select(GeometryProtoMapper.ToProto)
            .Where(item => item != V1.GeometryFrameScope.Unspecified && item != V1.GeometryFrameScope.Unknown)
            .Distinct());
        request.PolicyKinds.Add(NormalizeFilters(query.PolicyKinds));
        request.PolicyConstraints.Add(NormalizeFilters(query.PolicyConstraints));
        request.PolicyOperations.Add(NormalizeFilters(query.PolicyOperations));
        request.PolicyTags.Add(NormalizeFilters(query.PolicyTags));
    }

    private static void AddFilters(
        V1.WatchGeometryRegistryRequest request,
        GeometryQuery query)
    {
        request.Kinds.Add((query.Kinds ?? [])
            .Select(GeometryProtoMapper.ToProto)
            .Where(item => item != V1.GeometryKind.Unspecified)
            .Distinct());
        request.FrameScopes.Add((query.Frames ?? [])
            .Select(GeometryProtoMapper.ToProto)
            .Where(item => item != V1.GeometryFrameScope.Unspecified && item != V1.GeometryFrameScope.Unknown)
            .Distinct());
    }


    private static V1.GeometryValidationOptions ValidationOptions(bool checkUpdateCompatibility)
    {
        var options = new V1.GeometryValidationOptions
        {
            CheckUpdateCompatibility = checkUpdateCompatibility,
            ValidatePolicyMetadata = true
        };
        options.Checks.Add(new[]
        {
            V1.GeometryValidationCheck.Schema,
            V1.GeometryValidationCheck.Id,
            V1.GeometryValidationCheck.Kind,
            V1.GeometryValidationCheck.Frame,
            V1.GeometryValidationCheck.ZoneRing,
            V1.GeometryValidationCheck.PolicyMetadata
        });
        return options;
    }

    private static IEnumerable<string> NormalizeFilters(IEnumerable<string>? filters)
        => (filters ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private V1.CommandRequestMetadata CommandMetadata(
        string operation,
        string targetId,
        GeometryCreateRequest request)
        => _metadata.Create(
            operation,
            targetId,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private V1.CommandRequestMetadata CommandMetadata(
        string operation,
        string targetId,
        GeometryUpdateRequest request)
        => _metadata.Create(
            operation,
            targetId,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private V1.CommandRequestMetadata CommandMetadata(
        string operation,
        string targetId,
        GeometryDeleteRequest request)
        => _metadata.Create(
            operation,
            targetId,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private static GeometryValidationResult ValidationFromRpc(RpcException exception)
    {
        var unavailable = exception.StatusCode is
            StatusCode.Unimplemented or
            StatusCode.Unavailable or
            StatusCode.DeadlineExceeded;
        var message = RpcMessage("Validate geometry", exception);
        return new GeometryValidationResult(
            unavailable ? GeometryValidationState.Unavailable : GeometryValidationState.Invalid,
            message,
            [new GeometryValidationIssue(
                unavailable ? "GEOMETRY_VALIDATION_UNAVAILABLE" : "GEOMETRY_VALIDATION_RPC_REJECTED",
                unavailable ? GeometryValidationSeverity.Warning : GeometryValidationSeverity.Error,
                message,
                Source: "Logos GeometryService")]);
    }

    private static GeometryCommandResult CommandFromRpc(string operation, RpcException exception)
    {
        var state = exception.StatusCode switch
        {
            StatusCode.AlreadyExists or StatusCode.Aborted or StatusCode.FailedPrecondition => GeometryCommandState.Conflict,
            StatusCode.PermissionDenied or StatusCode.Unauthenticated or StatusCode.InvalidArgument => GeometryCommandState.Rejected,
            _ => GeometryCommandState.Failed
        };
        return new GeometryCommandResult(false, state, RpcMessage(operation, exception));
    }

    private static GeometryCommandResult Rejected(string message)
        => new(false, GeometryCommandState.Rejected, message);

    private static void EnsureSuccessful(V1.DomainStatus? status, string operation)
    {
        if (status is { Ok: true })
        {
            return;
        }

        var message = status is null
            ? "Logos returned no domain status."
            : string.IsNullOrWhiteSpace(status.Message)
                ? status.Code.ToString()
                : $"{status.Code}: {status.Message}";
        throw new InvalidOperationException($"Could not {operation}. {message}");
    }

    private static void RequireGeometryId(string geometryId)
    {
        if (string.IsNullOrWhiteSpace(geometryId))
        {
            throw new ArgumentException("A geometry ID is required.", nameof(geometryId));
        }
    }

    private static string RpcMessage(string operation, RpcException exception)
        => string.IsNullOrWhiteSpace(exception.Status.Detail)
            ? $"{operation} failed: {exception.StatusCode}."
            : $"{operation} failed: {exception.Status.Detail}";

    private static DateTime ReadDeadline() => DateTime.UtcNow.Add(ReadTimeout);

    private static DateTime CommandDeadline() => DateTime.UtcNow.Add(CommandTimeout);

    private sealed class GeometryGatewayUnavailableException(string message)
        : InvalidOperationException(message);
}
