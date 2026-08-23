using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

/// <summary>
/// Lifecycle wrapper around the existing connection manager. The current
/// telemetry connection remains authoritative; this wrapper attaches one
/// reusable generated operational-client session to the same connection ID.
/// </summary>
public sealed class OperationalLogosConnectionManager : ILogosConnectionManager
{
    private readonly ILogosConnectionManager _inner;
    private readonly ILogosOperationalSessionRegistry _sessions;
    private readonly ILogger<OperationalLogosConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, ConnectionCredentials> _credentials =
        new(StringComparer.Ordinal);
    private int _disposed;

    public OperationalLogosConnectionManager(
        ILogosConnectionManager inner,
        ILogosOperationalSessionRegistry sessions,
        ILogger<OperationalLogosConnectionManager> logger)
    {
        _inner = inner;
        _sessions = sessions;
        _logger = logger;
    }

    public IReadOnlyList<ConnectionDefinition> Definitions => _inner.Definitions;

    public bool TryGetDefinition(string connectionId, out ConnectionDefinition? definition)
        => _inner.TryGetDefinition(connectionId, out definition);

    public Task RegisterAsync(
        ConnectionDefinition definition,
        CancellationToken cancellationToken = default)
        => _inner.RegisterAsync(definition, cancellationToken);

    public async Task UpdateAsync(
        ConnectionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (IsLogosConnection(definition))
        {
            await _sessions.CloseAsync(definition.Id, cancellationToken);
        }
        await _inner.UpdateAsync(definition, cancellationToken);
    }

    public async Task RemoveAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (TryGetLogosDefinition(connectionId, out _))
        {
            await _sessions.CloseAsync(connectionId, cancellationToken);
        }
        _credentials.TryRemove(connectionId, out _);
        await _inner.RemoveAsync(connectionId, cancellationToken);
    }

    public async Task ConnectAsync(
        string connectionId,
        ConnectionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var normalized = credentials.Normalize();
        _credentials[connectionId] = normalized;
        await _inner.ConnectAsync(connectionId, normalized, cancellationToken);
        if (TryGetLogosDefinition(connectionId, out _))
        {
            await EnsureOperationalSessionAsync(connectionId, normalized, forceInspection: true, cancellationToken);
        }
    }

    public async Task DisconnectAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (TryGetLogosDefinition(connectionId, out _))
        {
            await _sessions.CloseAsync(connectionId, cancellationToken);
        }
        await _inner.DisconnectAsync(connectionId, cancellationToken);
    }

    public async Task RefreshAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        await _inner.RefreshAsync(connectionId, cancellationToken);
        if (TryGetLogosDefinition(connectionId, out _) &&
            _sessions.TryGet(connectionId, out var session) && session is not null)
        {
            await session.InspectAsync(force: true, cancellationToken);
        }
    }

    public Task<CameraStreamRecord> OpenCameraStreamAsync(
        string connectionId,
        CameraStreamOpenRequest request,
        CancellationToken cancellationToken = default)
        => _inner.OpenCameraStreamAsync(connectionId, request, cancellationToken);

    public Task CloseCameraStreamAsync(
        string connectionId,
        string streamId,
        CancellationToken cancellationToken = default)
        => _inner.CloseCameraStreamAsync(connectionId, streamId, cancellationToken);

    public Task<OperatorPolicyEvaluation> EvaluateOperatorPolicyAsync(
        string connectionId,
        OperatorPolicyRequest request,
        CancellationToken cancellationToken = default)
        => _inner.EvaluateOperatorPolicyAsync(connectionId, request, cancellationToken);

    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        await _inner.ConnectAllAsync(cancellationToken);
        await EnsureAllOperationalSessionsAsync(cancellationToken);
    }

    public async Task StartAutoConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await _inner.StartAutoConnectionsAsync(cancellationToken);
        await EnsureAllOperationalSessionsAsync(cancellationToken);
    }

    public async Task SuperviseAsync(CancellationToken cancellationToken = default)
    {
        await _inner.SuperviseAsync(cancellationToken);
        await EnsureAllOperationalSessionsAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _sessions.DisposeAsync();
        await _inner.DisposeAsync();
    }

    private async Task EnsureAllOperationalSessionsAsync(
        CancellationToken cancellationToken)
    {
        foreach (var definition in Definitions.Where(IsLogosConnection))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credentials = _credentials.TryGetValue(definition.Id, out var remembered)
                ? remembered
                : ConnectionCredentials.Empty;
            await EnsureOperationalSessionAsync(
                definition.Id,
                credentials,
                forceInspection: false,
                cancellationToken);
        }
    }

    private async Task EnsureOperationalSessionAsync(
        string connectionId,
        ConnectionCredentials credentials,
        bool forceInspection,
        CancellationToken cancellationToken)
    {
        if (!TryGetLogosDefinition(connectionId, out var definition) || definition is null)
        {
            return;
        }

        try
        {
            var session = await _sessions.OpenAsync(definition, credentials, cancellationToken);
            var status = await session.InspectAsync(forceInspection, cancellationToken);
            _logger.LogInformation(
                "Logos operational APIs for {ConnectionId}: {Summary}",
                connectionId,
                status.Summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Observation/telemetry connectivity remains useful even if the
            // generated operational clients are temporarily unavailable.
            _logger.LogWarning(
                ex,
                "Could not initialize Logos operational APIs for {ConnectionId}",
                connectionId);
        }
    }

    private bool TryGetLogosDefinition(
        string connectionId,
        out ConnectionDefinition? definition)
        => TryGetDefinition(connectionId, out definition) &&
           definition is not null &&
           IsLogosConnection(definition);

    private static bool IsLogosConnection(ConnectionDefinition definition)
        => definition.Mode == ConnectionMode.Direct;
}
