using Grpc.Core;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Missions;

public sealed class LogosBehaviourPackageCatalog : IBehaviourPackageCatalog
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public LogosBehaviourPackageCatalog(
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata)
    {
        _sessions = sessions;
        _metadata = metadata;
    }

    public async Task<IReadOnlyList<BehaviourPackageOption>> ListAsync(
        string connectionId,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }

        var normalizedConnectionId = connectionId.Trim();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh &&
                _cache.TryGetValue(normalizedConnectionId, out var cached) &&
                DateTimeOffset.UtcNow - cached.LoadedAt <= CacheAge)
            {
                return cached.Packages;
            }

            var session = _sessions.GetRequired(normalizedConnectionId);
            var status = await session.InspectAsync(cancellationToken: cancellationToken);
            if (!status.IsAvailable(OperationalApiDomain.Autonomy))
            {
                var autonomy = status.Get(OperationalApiDomain.Autonomy);
                throw new InvalidOperationException(
                    $"The Logos Autonomy API is not available for connection '{normalizedConnectionId}': {autonomy.Detail}");
            }

            V1.ListBehaviourPackagesResponse response;
            try
            {
                response = await session.Clients.Autonomy.ListBehaviourPackagesAsync(
                    new V1.ListBehaviourPackagesRequest
                    {
                        RequestId = _metadata.CreateRequestId(),
                        CorrelationId = _metadata.CreateCorrelationId(),
                        IncludeDeprecated = false,
                        IncludeGeometrySlots = true
                    },
                    deadline: DateTime.UtcNow.Add(ReadTimeout),
                    cancellationToken: cancellationToken);
            }
            catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
            {
                throw new InvalidOperationException(
                    $"Could not list Logos behaviour packages: {ex.Status.Detail}",
                    ex);
            }

            if (response.Status is { Ok: false })
            {
                var message = string.IsNullOrWhiteSpace(response.Status.Message)
                    ? response.Status.Code.ToString()
                    : response.Status.Message;
                throw new InvalidOperationException(
                    $"Logos rejected the behaviour package query: {message}");
            }

            var packages = response.Packages
                .Where(item => !string.IsNullOrWhiteSpace(item.BehaviourId))
                .Select(ToModel)
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _cache[normalizedConnectionId] = new CacheEntry(DateTimeOffset.UtcNow, packages);
            return packages;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BehaviourPackageOption> GetRequiredAsync(
        string connectionId,
        string behaviourId,
        string? version = null,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(behaviourId))
        {
            throw new ArgumentException("A behaviour ID is required.", nameof(behaviourId));
        }

        var packages = await ListAsync(connectionId, refresh, cancellationToken);
        var candidates = packages
            .Where(item => string.Equals(item.BehaviourId, behaviourId.Trim(), StringComparison.Ordinal))
            .ToArray();
        var package = string.IsNullOrWhiteSpace(version)
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(item =>
                string.Equals(item.Version, version.Trim(), StringComparison.Ordinal));
        return package ?? throw new KeyNotFoundException(
            string.IsNullOrWhiteSpace(version)
                ? $"Behaviour package '{behaviourId}' is not installed on the selected Logos runtime."
                : $"Behaviour package '{behaviourId}' version '{version}' is not installed on the selected Logos runtime.");
    }

    private static BehaviourPackageOption ToModel(V1.BehaviourPackageSummary summary)
        => new(
            summary.BehaviourId,
            summary.Version,
            string.IsNullOrWhiteSpace(summary.DisplayName) ? summary.BehaviourId : summary.DisplayName,
            summary.Description,
            summary.Status.ToString(),
            summary.Channel.ToString(),
            summary.RequiredCapabilities.ToArray(),
            summary.ProvidedCapabilities.ToArray(),
            summary.GeometrySlots.Select(slot => new BehaviourGeometryRequirement(
                slot.SlotId,
                slot.KindHint,
                slot.RequiredRegistration,
                slot.RequireObjectAtStart,
                slot.AllowEmptyGeometry,
                slot.ExpectedPolicyKind,
                string.IsNullOrWhiteSpace(slot.DefaultGeometryId) ? null : slot.DefaultGeometryId,
                slot.Description)).ToArray(),
            summary.UpdatedAt is null
                ? null
                : new DateTimeOffset(summary.UpdatedAt.ToDateTime(), TimeSpan.Zero));

    private sealed record CacheEntry(
        DateTimeOffset LoadedAt,
        IReadOnlyList<BehaviourPackageOption> Packages);
}
