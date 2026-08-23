using Grpc.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Missions;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Behaviours;

public sealed class LogosBehaviourPackageGateway : IBehaviourPackageGateway
{
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly BehaviourPackageProtoMapper _mapper;
    private readonly ILogger<LogosBehaviourPackageGateway> _logger;

    public LogosBehaviourPackageGateway(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata,
        BehaviourPackageProtoMapper mapper,
        ILogger<LogosBehaviourPackageGateway> logger)
    {
        _sessions = sessions;
        _metadata = metadata;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<BehaviourPackageValidationResult> ValidateAsync(
        BehaviourPackageGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
            var package = await BehaviourPackageProtoMapper.ToProtoAsync(request.Package, cancellationToken);
            var response = await session.Clients.Autonomy.ValidateBehaviourPackageAsync(
                new V1.ValidateBehaviourPackageRequest
                {
                    Command = CommandMetadata("autonomy.behaviour-package.validate", request, request.Package.Identity.Key),
                    BehaviourId = request.Package.Identity.BehaviourId,
                    Version = request.Package.Identity.Version ?? string.Empty,
                    Package = package,
                    ValidateGeometryReferences = true
                },
                deadline: DateTime.UtcNow.Add(ValidationTimeout),
                cancellationToken: cancellationToken);
            return BehaviourPackageProtoMapper.ToValidationResult(
                response.Validation,
                response.Status,
                response.Authorization);
        }
        catch (BehaviourPackageGatewayUnavailableException ex)
        {
            return BehaviourPackageProtoMapper.UnavailableValidation(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Validate behaviour package RPC failed for {Identity}", request.Package.Identity.Key);
            return BehaviourPackageProtoMapper.UnavailableValidation(RpcMessage("Validate behaviour package", ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not prepare behaviour package {Identity} for Logos validation", request.Package.Identity.Key);
            return new BehaviourPackageValidationResult(
                BehaviourPackageValidationAuthority.Logos,
                BehaviourPackageValidationState.Invalid,
                ex.Message,
                [new BehaviourPackageValidationFinding(
                    "BEHAVIOUR_PACKAGE_PAYLOAD_UNAVAILABLE",
                    BehaviourPackageFindingSeverity.Error,
                    ex.Message)],
                DateTimeOffset.UtcNow);
        }
    }

    public Task<BehaviourPackageGatewayMutationResult> CreateAsync(
        BehaviourPackageGatewayRequest request,
        CancellationToken cancellationToken = default)
        => ExecutePackageCommandAsync(
            request,
            "autonomy.behaviour-package.create",
            async (session, package, command) => _mapper.ToCreateResult(
                await session.Clients.Autonomy.CreateBehaviourPackageAsync(
                    new V1.CreateBehaviourPackageRequest
                    {
                        Command = command,
                        Package = package,
                        ValidateOnCreate = true
                    },
                    deadline: DateTime.UtcNow.Add(CommandTimeout),
                    cancellationToken: cancellationToken)),
            cancellationToken);

    public Task<BehaviourPackageGatewayMutationResult> UpdateAsync(
        BehaviourPackageGatewayRequest request,
        CancellationToken cancellationToken = default)
        => ExecutePackageCommandAsync(
            request,
            "autonomy.behaviour-package.update",
            async (session, package, command) => _mapper.ToUpdateResult(
                await session.Clients.Autonomy.UpdateBehaviourPackageAsync(
                    new V1.UpdateBehaviourPackageRequest
                    {
                        Command = command,
                        BehaviourId = request.Package.Identity.BehaviourId,
                        Version = request.Package.Identity.Version ?? string.Empty,
                        Package = package,
                        ValidateOnUpdate = true
                    },
                    deadline: DateTime.UtcNow.Add(CommandTimeout),
                    cancellationToken: cancellationToken)),
            cancellationToken);

    public async Task<BehaviourPackageGatewayMutationResult> DeleteAsync(
        BehaviourPackageDeleteGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
            var response = await session.Clients.Autonomy.DeleteBehaviourPackageAsync(
                new V1.DeleteBehaviourPackageRequest
                {
                    Command = _metadata.Create(
                        "autonomy.behaviour-package.delete",
                        request.Identity.Key,
                        request.RequestId,
                        request.CorrelationId,
                        request.IdempotencyKey),
                    BehaviourId = request.Identity.BehaviourId,
                    Version = request.Identity.Version ?? string.Empty,
                    AllowDeleteActive = false
                },
                deadline: DateTime.UtcNow.Add(CommandTimeout),
                cancellationToken: cancellationToken);
            return BehaviourPackageProtoMapper.ToDeleteResult(response);
        }
        catch (BehaviourPackageGatewayUnavailableException ex)
        {
            return Failed(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Delete behaviour package RPC failed for {Identity}", request.Identity.Key);
            return Failed(RpcMessage("Delete behaviour package", ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not remove behaviour package {Identity}", request.Identity.Key);
            return Failed(ex.Message);
        }
    }

    private async Task<BehaviourPackageGatewayMutationResult> ExecutePackageCommandAsync(
        BehaviourPackageGatewayRequest request,
        string operation,
        Func<ILogosOperationalSession, V1.BehaviourPackage, V1.CommandRequestMetadata, Task<BehaviourPackageGatewayMutationResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
            var package = await BehaviourPackageProtoMapper.ToProtoAsync(request.Package, cancellationToken);
            var command = CommandMetadata(operation, request, request.Package.Identity.Key);
            return await execute(session, package, command);
        }
        catch (BehaviourPackageGatewayUnavailableException ex)
        {
            return Failed(ex.Message);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Behaviour package RPC {Operation} failed for {Identity}", operation, request.Package.Identity.Key);
            return Failed(RpcMessage(operation, ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not execute {Operation} for behaviour package {Identity}", operation, request.Package.Identity.Key);
            return Failed(ex.Message);
        }
    }

    private V1.CommandRequestMetadata CommandMetadata(
        string operation,
        BehaviourPackageGatewayRequest request,
        string target)
        => _metadata.Create(
            operation,
            target,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private async Task<ILogosOperationalSession> RequireSessionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new BehaviourPackageGatewayUnavailableException("A Logos connection is required.");
        }

        if (!_sessions.TryGet(connectionId.Trim(), out var session) || session is null)
        {
            throw new BehaviourPackageGatewayUnavailableException(
                $"Connection '{connectionId.Trim()}' has no active Logos operational session.");
        }

        var snapshot = session.Status;
        var status = snapshot.Get(OperationalApiDomain.Autonomy);
        if (snapshot.InspectedAt == DateTimeOffset.MinValue ||
            status.Availability is OperationalApiAvailability.Unknown or OperationalApiAvailability.Inspecting)
        {
            snapshot = await session.InspectAsync(cancellationToken: cancellationToken);
            status = snapshot.Get(OperationalApiDomain.Autonomy);
        }

        if (!status.Available)
        {
            throw new BehaviourPackageGatewayUnavailableException(
                $"Autonomy API is not available on connection '{connectionId.Trim()}': {status.Detail}");
        }

        return session;
    }

    private static BehaviourPackageGatewayMutationResult Failed(string message)
        => new(false, BehaviourPackageCommandState.Failed, message);

    private static string RpcMessage(string operation, RpcException ex)
        => $"{operation} failed: {ex.Status.Detail}";

    private sealed class BehaviourPackageGatewayUnavailableException(string message) : Exception(message);
}
