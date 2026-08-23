using Grpc.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Geometry;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Missions;

public sealed class LogosBehaviourGeometryBindingService : IBehaviourGeometryBindingService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly IGeometryGateway _geometry;
    private readonly IBehaviourPackageCatalog _packages;
    private readonly ILogger<LogosBehaviourGeometryBindingService> _logger;

    public LogosBehaviourGeometryBindingService(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata,
        IGeometryGateway geometry,
        IBehaviourPackageCatalog packages,
        ILogger<LogosBehaviourGeometryBindingService> logger)
    {
        _sessions = sessions;
        _metadata = metadata;
        _geometry = geometry;
        _packages = packages;
        _logger = logger;
    }

    public bool IsAvailable => _sessions.ConnectionIds.Any(ConnectionSupportsBindings);

    public string AvailabilityMessage
    {
        get
        {
            if (_sessions.ConnectionIds.Count == 0)
            {
                return "Connect to a Logos runtime to manage behaviour geometry bindings.";
            }

            var count = _sessions.ConnectionIds.Count(ConnectionSupportsBindings);
            return count > 0
                ? $"Behaviour geometry bindings are available on {count} connected Logos runtime(s)."
                : "Connected Logos runtimes do not currently expose both AutonomyService and GeometryService.";
        }
    }

    public async Task<IReadOnlyList<BehaviourGeometryBindingRecord>> ListAsync(
        string connectionId,
        string behaviourId,
        string version,
        bool checkObjectExists = true,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(connectionId, behaviourId, version);
        var session = await RequireSessionAsync(connectionId, cancellationToken);
        try
        {
            var response = await session.Clients.Autonomy.ListBehaviourGeometryBindingsAsync(
                new V1.ListBehaviourGeometryBindingsRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    BehaviourId = behaviourId.Trim(),
                    Version = version.Trim(),
                    IncludeUnbound = true,
                    CheckObjectExists = checkObjectExists
                },
                deadline: DateTime.UtcNow.Add(ReadTimeout),
                cancellationToken: cancellationToken);
            EnsureSuccessful(response.Status, "list behaviour geometry bindings");
            return response.Bindings
                .Select(item => ToModel(connectionId, version, item))
                .OrderBy(item => item.SlotId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            throw Unavailable("list behaviour geometry bindings", connectionId, ex);
        }
    }

    public async Task<BehaviourGeometryReadiness> AssessAsync(
        string connectionId,
        BehaviourPackageOption package,
        bool refreshGeometry = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.GeometrySlots.Count == 0)
        {
            return BehaviourGeometryReadiness.NoRequirements(
                connectionId,
                package.BehaviourId,
                package.Version);
        }

        var listed = await ListAsync(
            connectionId,
            package.BehaviourId,
            package.Version,
            checkObjectExists: true,
            cancellationToken);
        var bySlot = listed.ToDictionary(item => item.SlotId, StringComparer.Ordinal);
        var normalized = new List<BehaviourGeometryBindingRecord>(package.GeometrySlots.Count);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var geometryIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var slot in package.GeometrySlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bySlot.TryGetValue(slot.SlotId, out var binding);
            var geometryId = binding is { Bound: true } && !string.IsNullOrWhiteSpace(binding.GeometryId)
                ? binding.GeometryId
                : slot.DefaultGeometryId;
            var required = (slot.RequiredRegistration || slot.RequireObjectAtStart) && !slot.AllowEmptyGeometry;
            RemoteGeometryObject? geometry = null;
            var issues = new List<string>();

            if (!string.IsNullOrWhiteSpace(geometryId))
            {
                try
                {
                    geometry = await _geometry.GetAsync(
                        connectionId,
                        geometryId,
                        refreshGeometry,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    issues.Add($"Geometry lookup failed: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(geometryId))
            {
                if (required)
                {
                    blockers.Add($"Required geometry slot '{slot.SlotId}' is unbound.");
                }
                else
                {
                    warnings.Add($"Optional geometry slot '{slot.SlotId}' is unbound.");
                }
            }
            else if (geometry is null)
            {
                var message = $"Geometry '{geometryId}' bound to slot '{slot.SlotId}' does not exist on the selected Logos runtime.";
                if (required) blockers.Add(message); else warnings.Add(message);
            }
            else
            {
                geometryIds.Add(geometryId);
                if (!BehaviourGeometryCompatibility.KindMatches(slot.KindHint, geometry.Document.Kind))
                {
                    var message = $"Slot '{slot.SlotId}' expects {BehaviourGeometryCompatibility.ExpectedKindLabel(slot.KindHint)}, but geometry '{geometryId}' is {geometry.Document.Kind}.";
                    if (required) blockers.Add(message); else warnings.Add(message);
                    issues.Add(message);
                }

                if (!BehaviourGeometryCompatibility.PolicyMatches(slot.ExpectedPolicyKind, geometry.Document.Policy))
                {
                    var message = $"Slot '{slot.SlotId}' expects policy kind '{slot.ExpectedPolicyKind}', but geometry '{geometryId}' is '{geometry.Document.Policy.Kind}/{geometry.Document.Policy.Constraint}'.";
                    if (required) blockers.Add(message); else warnings.Add(message);
                    issues.Add(message);
                }
            }

            if (binding?.Issues is { Count: > 0 })
            {
                issues.AddRange(binding.Issues);
                if (required) blockers.AddRange(binding.Issues); else warnings.AddRange(binding.Issues);
            }

            normalized.Add(new BehaviourGeometryBindingRecord(
                connectionId,
                package.BehaviourId,
                package.Version,
                slot.SlotId,
                geometryId,
                !string.IsNullOrWhiteSpace(geometryId),
                geometry is not null,
                slot.RequiredRegistration,
                slot.RequireObjectAtStart,
                slot.AllowEmptyGeometry,
                slot.ExpectedPolicyKind,
                binding?.UpdatedAt,
                issues.Distinct(StringComparer.Ordinal).ToArray()));
        }

        return new BehaviourGeometryReadiness(
            connectionId,
            package.BehaviourId,
            package.Version,
            normalized,
            geometryIds.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            blockers.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<BehaviourGeometryBindingCommandResult> SetAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.ConnectionId, request.BehaviourId, request.Version);
        Require(request.SlotId, "A behaviour geometry slot ID is required.");
        Require(request.GeometryId, "A geometry ID is required to set a binding.");

        var geometry = await _geometry.GetAsync(
            request.ConnectionId,
            request.GeometryId!,
            refresh: true,
            cancellationToken: cancellationToken);
        if (geometry is null)
        {
            return new BehaviourGeometryBindingCommandResult(
                false,
                $"Geometry '{request.GeometryId}' is not registered on the selected Logos runtime.");
        }

        var package = await _packages.GetRequiredAsync(
            request.ConnectionId,
            request.BehaviourId,
            request.Version,
            refresh: false,
            cancellationToken);
        var requirement = package.GeometrySlots.FirstOrDefault(item =>
            string.Equals(item.SlotId, request.SlotId, StringComparison.Ordinal));
        if (requirement is null)
        {
            return new BehaviourGeometryBindingCommandResult(
                false,
                $"Behaviour '{request.BehaviourId}@{request.Version}' does not declare geometry slot '{request.SlotId}'.");
        }

        if (!BehaviourGeometryCompatibility.KindMatches(requirement.KindHint, geometry.Document.Kind))
        {
            return new BehaviourGeometryBindingCommandResult(
                false,
                $"Slot '{request.SlotId}' expects {BehaviourGeometryCompatibility.ExpectedKindLabel(requirement.KindHint)}, but geometry '{request.GeometryId}' is {geometry.Document.Kind}.");
        }

        if (!BehaviourGeometryCompatibility.PolicyMatches(requirement.ExpectedPolicyKind, geometry.Document.Policy))
        {
            return new BehaviourGeometryBindingCommandResult(
                false,
                $"Slot '{request.SlotId}' expects policy kind '{requirement.ExpectedPolicyKind}', but geometry '{request.GeometryId}' is '{geometry.Document.Policy.Kind}/{geometry.Document.Policy.Constraint}'.");
        }

        var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
        try
        {
            var response = await session.Clients.Autonomy.SetBehaviourGeometryBindingAsync(
                new V1.SetBehaviourGeometryBindingRequest
                {
                    Command = _metadata.Create(
                        "autonomy.behaviour-geometry-binding.set",
                        $"{request.BehaviourId}:{request.Version}:{request.SlotId}",
                        request.RequestId,
                        request.CorrelationId,
                        request.IdempotencyKey),
                    BehaviourId = request.BehaviourId.Trim(),
                    Version = request.Version.Trim(),
                    SlotId = request.SlotId.Trim(),
                    GeometryId = request.GeometryId!.Trim(),
                    RequireObjectExists = true
                },
                deadline: DateTime.UtcNow.Add(CommandTimeout),
                cancellationToken: cancellationToken);

            var issues = CollectIssues(response.Status?.Issues, response.Authorization?.Status?.Issues, response.Binding?.Issues);
            if (response.Authorization is { Allowed: false })
            {
                return new BehaviourGeometryBindingCommandResult(
                    false,
                    Message(response.Authorization.DeniedReasons, "Logos denied the behaviour geometry binding."),
                    Issues: issues);
            }

            if (response.Status is { Ok: false })
            {
                return new BehaviourGeometryBindingCommandResult(
                    false,
                    DomainMessage(response.Status),
                    Issues: issues);
            }

            var binding = response.Binding is null
                ? null
                : ToModel(request.ConnectionId, request.Version, response.Binding);
            return new BehaviourGeometryBindingCommandResult(
                binding is { Bound: true, ObjectExists: true },
                binding is { Bound: true, ObjectExists: true }
                    ? $"Bound slot '{request.SlotId}' to geometry '{request.GeometryId}'."
                    : "Logos returned a binding that is not ready.",
                binding,
                issues);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Could not set behaviour geometry binding {BehaviourId}/{SlotId}", request.BehaviourId, request.SlotId);
            throw Unavailable("set behaviour geometry binding", request.ConnectionId, ex);
        }
    }

    public async Task<BehaviourGeometryBindingCommandResult> DeleteAsync(
        BehaviourGeometryBindingCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.ConnectionId, request.BehaviourId, request.Version);
        Require(request.SlotId, "A behaviour geometry slot ID is required.");
        var session = await RequireSessionAsync(request.ConnectionId, cancellationToken);
        try
        {
            var response = await session.Clients.Autonomy.DeleteBehaviourGeometryBindingAsync(
                new V1.DeleteBehaviourGeometryBindingRequest
                {
                    Command = _metadata.Create(
                        "autonomy.behaviour-geometry-binding.delete",
                        $"{request.BehaviourId}:{request.Version}:{request.SlotId}",
                        request.RequestId,
                        request.CorrelationId,
                        request.IdempotencyKey),
                    BehaviourId = request.BehaviourId.Trim(),
                    Version = request.Version.Trim(),
                    SlotId = request.SlotId.Trim()
                },
                deadline: DateTime.UtcNow.Add(CommandTimeout),
                cancellationToken: cancellationToken);

            var issues = CollectIssues(response.Result?.Status?.Issues, response.Authorization?.Status?.Issues, response.Binding?.Issues);
            if (response.Authorization is { Allowed: false })
            {
                return new BehaviourGeometryBindingCommandResult(
                    false,
                    Message(response.Authorization.DeniedReasons, "Logos denied removal of the behaviour geometry binding."),
                    Issues: issues);
            }

            var accepted = response.Result?.CommandStatus is
                V1.CommandStatus.Accepted or
                V1.CommandStatus.InProgress or
                V1.CommandStatus.Succeeded;
            return new BehaviourGeometryBindingCommandResult(
                accepted,
                accepted
                    ? $"Removed the geometry binding for slot '{request.SlotId}'."
                    : Message(response.Result?.Status?.Message, "Logos did not remove the geometry binding."),
                response.Binding is null ? null : ToModel(request.ConnectionId, request.Version, response.Binding),
                issues);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(ex, "Could not remove behaviour geometry binding {BehaviourId}/{SlotId}", request.BehaviourId, request.SlotId);
            throw Unavailable("remove behaviour geometry binding", request.ConnectionId, ex);
        }
    }

    private async Task<ILogosOperationalSession> RequireSessionAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGet(connectionId, out var session) || session is null)
        {
            throw new InvalidOperationException(
                $"Connection '{connectionId}' has no active Logos operational session.");
        }

        var snapshot = session.Status;
        if (snapshot.InspectedAt == DateTimeOffset.MinValue ||
            snapshot.Get(OperationalApiDomain.Autonomy).Availability is
                OperationalApiAvailability.Unknown or OperationalApiAvailability.Inspecting ||
            snapshot.Get(OperationalApiDomain.Geometry).Availability is
                OperationalApiAvailability.Unknown or OperationalApiAvailability.Inspecting)
        {
            snapshot = await session.InspectAsync(cancellationToken: cancellationToken);
        }

        foreach (var domain in new[] { OperationalApiDomain.Autonomy, OperationalApiDomain.Geometry })
        {
            var status = snapshot.Get(domain);
            if (!status.Available)
            {
                throw new InvalidOperationException(
                    $"{domain} API is not available on connection '{connectionId}': {status.Detail}");
            }
        }

        return session;
    }

    private bool ConnectionSupportsBindings(string connectionId)
        => _sessions.TryGet(connectionId, out var session) &&
           session is not null &&
           session.Status.IsAvailable(OperationalApiDomain.Autonomy) &&
           session.Status.IsAvailable(OperationalApiDomain.Geometry);

    private static BehaviourGeometryBindingRecord ToModel(
        string connectionId,
        string version,
        V1.BehaviourGeometryBinding binding)
        => new(
            connectionId,
            binding.BehaviourId,
            version,
            binding.SlotId,
            string.IsNullOrWhiteSpace(binding.GeometryId) ? null : binding.GeometryId,
            binding.Bound,
            binding.ObjectExists,
            binding.RequiredRegistration,
            binding.RequireObjectAtStart,
            binding.AllowEmptyGeometry,
            binding.ExpectedPolicyKind,
            binding.UpdatedAt is null
                ? null
                : new DateTimeOffset(binding.UpdatedAt.ToDateTime(), TimeSpan.Zero),
            binding.Issues.Select(FormatIssue).ToArray());

    private static void EnsureSuccessful(V1.DomainStatus? status, string operation)
    {
        if (status is { Ok: false })
        {
            throw new InvalidOperationException($"Logos could not {operation}: {DomainMessage(status)}");
        }
    }

    private static string[] CollectIssues(params IEnumerable<V1.Issue>?[] sources)
        => sources
            .Where(source => source is not null)
            .SelectMany(source => source!)
            .Select(FormatIssue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string FormatIssue(V1.Issue issue)
    {
        var code = string.IsNullOrWhiteSpace(issue.Code) ? string.Empty : $"{issue.Code}: ";
        var field = string.IsNullOrWhiteSpace(issue.FieldPath) ? string.Empty : $" [{issue.FieldPath}]";
        return $"{code}{issue.Message}{field}".Trim();
    }

    private static string DomainMessage(V1.DomainStatus status)
        => string.IsNullOrWhiteSpace(status.Message)
            ? status.Code.ToString()
            : $"{status.Code}: {status.Message}";

    private static string Message(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static InvalidOperationException Unavailable(
        string operation,
        string connectionId,
        RpcException exception)
        => new(
            $"Could not {operation} on connection '{connectionId}': " +
            (string.IsNullOrWhiteSpace(exception.Status.Detail)
                ? exception.StatusCode.ToString()
                : exception.Status.Detail),
            exception);

    private static void ValidateIdentity(string connectionId, string behaviourId, string version)
    {
        Require(connectionId, "A Logos connection ID is required.");
        Require(behaviourId, "A behaviour ID is required.");
        Require(version, "A behaviour version is required.");
    }

    private static void Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message);
        }
    }
}
