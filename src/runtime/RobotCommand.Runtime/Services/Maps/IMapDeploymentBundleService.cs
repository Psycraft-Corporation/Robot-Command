using RobotCommand.Models;

namespace RobotCommand.Services.Maps;

public interface IMapDeploymentBundleService
{
    Task<MapDeploymentExportResult> ExportAsync(
        MapDeploymentExportRequest request,
        CancellationToken cancellationToken = default);

    Task<MapDeploymentImportResult> ImportAsync(
        string bundlePath,
        CancellationToken cancellationToken = default);
}
