using RobotCommand.Models;

namespace RobotCommand.Services.Evidence;

public interface IEvidenceLibrary
{
    event EventHandler? Changed;

    EvidenceLibrarySnapshot Snapshot { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<EvidenceRecord> CaptureDisplayedFrameAsync(
        DisplayedFrameCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<EvidenceRecord> ExportClipAsync(
        VideoClipExportRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string evidenceId, CancellationToken cancellationToken = default);
}
