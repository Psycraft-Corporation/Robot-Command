using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IMapDisplayPreferences
{
    event EventHandler? Changed;

    string ResolveStyleId(InstalledMapPackage package);

    Task SelectStyleAsync(
        string packageKey,
        string styleId,
        CancellationToken cancellationToken = default);
}
