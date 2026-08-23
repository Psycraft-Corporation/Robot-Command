using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IOperationalMapEngine
{
    event EventHandler? Changed;

    IReadOnlyList<MapStyleOption> AvailableStyles { get; }

    string? SelectedStyleId { get; }

    MapViewportSnapshot StartupViewport { get; }

    OperationalMapPresentation Prepare(OperationalMapScene scene);

    Task SelectStyleAsync(string styleId, CancellationToken cancellationToken = default);
}
