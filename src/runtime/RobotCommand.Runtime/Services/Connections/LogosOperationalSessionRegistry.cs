using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Connections;

public sealed class LogosOperationalSessionRegistry : ILogosOperationalSessionRegistry
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogosOperationalSessionFactory _factory;
    private readonly ILogger<LogosOperationalSessionRegistry> _logger;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    public LogosOperationalSessionRegistry(
        ILogosOperationalSessionFactory factory,
        ILogger<LogosOperationalSessionRegistry> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public IReadOnlyList<string> ConnectionIds
    {
        get
        {
            lock (_entries)
            {
                return _entries.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            }
        }
    }

    public bool TryGet(string connectionId, out ILogosOperationalSession? session)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(connectionId, out var entry))
            {
                session = entry.Session;
                return true;
            }
        }

        session = null;
        return false;
    }

    public ILogosOperationalSession GetRequired(string connectionId)
        => TryGet(connectionId, out var session) && session is not null
            ? session
            : throw new InvalidOperationException(
                $"Connection '{connectionId}' does not have an active Logos operational session.");

    public async Task<ILogosOperationalSession> OpenAsync(
        ConnectionDefinition definition,
        ConnectionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(credentials);
        ThrowIfDisposed();
        var normalized = credentials.Normalize();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Entry? existing;
            lock (_entries)
            {
                _entries.TryGetValue(definition.Id, out existing);
            }

            if (existing is not null &&
                existing.Definition == definition &&
                existing.Credentials == normalized)
            {
                return existing.Session;
            }

            if (existing is not null)
            {
                lock (_entries)
                {
                    _entries.Remove(definition.Id);
                }
                await existing.Session.DisposeAsync();
            }

            var session = _factory.Create(definition, normalized);
            lock (_entries)
            {
                _entries[definition.Id] = new Entry(definition, normalized, session);
            }

            _logger.LogInformation(
                "Opened Logos operational session for {ConnectionId} at {Target}",
                definition.Id,
                definition.Target);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Entry? entry;
            lock (_entries)
            {
                _entries.Remove(connectionId, out entry);
            }

            if (entry is not null)
            {
                await entry.Session.DisposeAsync();
                _logger.LogInformation(
                    "Closed Logos operational session for {ConnectionId}",
                    connectionId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            Entry[] entries;
            lock (_entries)
            {
                entries = _entries.Values.ToArray();
                _entries.Clear();
            }

            foreach (var entry in entries)
            {
                await entry.Session.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private sealed record Entry(
        ConnectionDefinition Definition,
        ConnectionCredentials Credentials,
        ILogosOperationalSession Session);
}
