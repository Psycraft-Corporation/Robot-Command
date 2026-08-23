using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

/// <summary>UI-neutral current operational map presentation shared by desktop and headless hosts.</summary>
public interface IMapPresentationState
{
    OperationalMapPresentation Presentation { get; }
    MapViewportSnapshot? CurrentViewport { get; }
    bool GoToIndicatorsVisible { get; }
    event EventHandler? Changed;

    void Update(OperationalMapPresentation presentation, MapViewportSnapshot? viewport, bool goToIndicatorsVisible);

    void UpdateMotion(MapVehicleMotionSnapshot motion);
}

public sealed class MapPresentationState : IMapPresentationState
{
    private readonly object _gate = new();
    private OperationalMapPresentation _presentation = OperationalMapPresentation.Empty;
    private MapViewportSnapshot? _viewport;
    private bool _goToIndicatorsVisible = true;

    public OperationalMapPresentation Presentation { get { lock (_gate) return _presentation; } }
    public MapViewportSnapshot? CurrentViewport { get { lock (_gate) return _viewport; } }
    public bool GoToIndicatorsVisible { get { lock (_gate) return _goToIndicatorsVisible; } }
    public event EventHandler? Changed;

    public void Update(OperationalMapPresentation presentation, MapViewportSnapshot? viewport, bool goToIndicatorsVisible)
    {
        lock (_gate)
        {
            _presentation = presentation;
            _viewport = viewport;
            _goToIndicatorsVisible = goToIndicatorsVisible;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateMotion(MapVehicleMotionSnapshot motion)
    {
        lock (_gate)
        {
            _presentation = _presentation with { Motion = motion };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
