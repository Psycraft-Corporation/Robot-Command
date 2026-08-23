using RobotCommand.Models;

namespace RobotCommand.Services.Autonomy;

public interface IAutonomyDeploymentBundleService
{
    string RootPath { get; }

    Task<AutonomyDeploymentBundleManifest> ExportAsync(
        AutonomyBundleExportRequest request,
        CancellationToken cancellationToken = default);

    Task<AutonomyBundleInspectionResult> InspectAsync(
        string archivePath,
        CancellationToken cancellationToken = default);

    Task<AutonomyBundleImportResult> ImportAsync(
        string archivePath,
        AutonomyBundleImportOptions options,
        CancellationToken cancellationToken = default);
}
