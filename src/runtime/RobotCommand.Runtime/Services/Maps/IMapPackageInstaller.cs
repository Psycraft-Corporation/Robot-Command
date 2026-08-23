using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IMapPackageInstaller
{
    Task<MapPackageImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
