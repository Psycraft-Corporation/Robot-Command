namespace RobotCommand.Services.Mavlink;

public interface IMavlinkConnectionRegistry
{
    bool TryGet(string connectionId, out MavlinkConnection? connection);
}

public sealed class MavlinkConnectionRegistry : IMavlinkConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MavlinkConnection> _connections = new(StringComparer.Ordinal);

    public bool TryGet(string connectionId, out MavlinkConnection? connection)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out connection);
        }
    }

    internal void Register(MavlinkConnection connection)
    {
        lock (_gate)
        {
            _connections[connection.Definition.Id] = connection;
        }
    }

    internal void Unregister(MavlinkConnection connection)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connection.Definition.Id, out var current) &&
                ReferenceEquals(current, connection))
            {
                _connections.Remove(connection.Definition.Id);
            }
        }
    }
}
