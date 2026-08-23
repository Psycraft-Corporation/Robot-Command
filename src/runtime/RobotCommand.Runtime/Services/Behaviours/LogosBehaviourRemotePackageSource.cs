using Grpc.Core;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Missions;
using V1 = global::Logos.Api.V1;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Enriches the existing package summary catalogue with the authoritative
/// package SHA reported by Logos. It does not inspect behaviour-tree nodes or
/// attempt to reproduce Logos package validation.
/// </summary>
public sealed class LogosBehaviourRemotePackageSource : IBehaviourRemotePackageSource
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IBehaviourPackageCatalog _catalog;
    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogosCommandMetadataFactory _metadata;
    private readonly ILogger<LogosBehaviourRemotePackageSource> _logger;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public LogosBehaviourRemotePackageSource(
        IBehaviourPackageCatalog catalog,
        ILogosOperationalSessionRegistry sessions,
        ILogosCommandMetadataFactory metadata,
        ILogger<LogosBehaviourRemotePackageSource> logger)
    {
        _catalog = catalog;
        _sessions = sessions;
        _metadata = metadata;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RemoteBehaviourPackageRecord>> ListAsync(
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

            var summaries = await _catalog.ListAsync(
                normalizedConnectionId,
                refresh,
                cancellationToken);
            var session = _sessions.GetRequired(normalizedConnectionId);
            var records = new List<RemoteBehaviourPackageRecord>(summaries.Count);
            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageSha256 = await TryReadPackageHashAsync(
                    normalizedConnectionId,
                    session,
                    summary,
                    cancellationToken);
                records.Add(summary.ToRemoteRecord(
                    normalizedConnectionId,
                    packageSha256));
            }

            var result = records
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.Identity.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _cache[normalizedConnectionId] = new CacheEntry(DateTimeOffset.UtcNow, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> TryReadPackageHashAsync(
        string connectionId,
        ILogosOperationalSession session,
        BehaviourPackageOption summary,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await session.Clients.Autonomy.GetBehaviourPackageAsync(
                new V1.GetBehaviourPackageRequest
                {
                    RequestId = _metadata.CreateRequestId(),
                    CorrelationId = _metadata.CreateCorrelationId(),
                    BehaviourId = summary.BehaviourId,
                    Version = summary.Version,
                    IncludeManifest = false,
                    IncludeTreeXml = false,
                    IncludeGeometryJson = false
                },
                deadline: DateTime.UtcNow.Add(ReadTimeout),
                cancellationToken: cancellationToken);
            if (response.Status is { Ok: false } || response.Package is null)
            {
                return null;
            }

            return NormalizeHash(response.Package.PackageSha256);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            _logger.LogWarning(
                ex,
                "Could not read package hash for {BehaviourId}@{Version} on {ConnectionId}",
                summary.BehaviourId,
                summary.Version,
                connectionId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not enrich behaviour package {BehaviourId}@{Version} with a remote package hash",
                summary.BehaviourId,
                summary.Version);
            return null;
        }
    }

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private sealed record CacheEntry(
        DateTimeOffset LoadedAt,
        IReadOnlyList<RemoteBehaviourPackageRecord> Packages);
}
