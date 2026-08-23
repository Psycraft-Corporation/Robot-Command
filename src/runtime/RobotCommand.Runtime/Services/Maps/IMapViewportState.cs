using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IMapViewportState
{
    MapViewportSnapshot? Current { get; }

    event EventHandler? Changed;

    void Update(MapViewportSnapshot snapshot);
}

public sealed class MapViewportState : IMapViewportState
{
    private readonly object _gate = new();
    private MapViewportSnapshot? _current;

    public event EventHandler? Changed;

    public MapViewportSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(MapViewportSnapshot snapshot)
    {
        var changed = false;
        lock (_gate)
        {
            changed = _current != snapshot;
            _current = snapshot;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
}
