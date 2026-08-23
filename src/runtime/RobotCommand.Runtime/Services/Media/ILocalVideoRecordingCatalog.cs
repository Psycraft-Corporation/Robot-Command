using RobotCommand.Models;

namespace RobotCommand.Services.Media;

public interface ILocalVideoRecordingCatalog
{
    string RootPath { get; }

    Task WriteSessionAsync(
        string sessionDirectory,
        LocalVideoSessionManifest manifest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalVideoSegment>> LoadAsync(
        LocalVideoCatalogContext context,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalVideoSegment>> CleanupAndLoadAsync(
        LocalVideoCatalogContext context,
        CancellationToken cancellationToken = default);

    Task RetainAsync(
        LocalVideoSegment segment,
        CancellationToken cancellationToken = default);
}
