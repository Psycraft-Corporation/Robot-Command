using System.Security.Cryptography;
using System.Text;

namespace RobotCommand.Models;

public enum EvidenceKind
{
    DisplayedFrame,
    VideoClip,
    SourceImage
}

public sealed record EvidenceCaptureContext(
    string? ConnectionId,
    string? LogosInstanceId,
    string? VehicleId,
    string? VehicleName,
    string? CameraSourceId,
    string? StreamId,
    string? Protocol,
    string? MissionId,
    string? TaskId,
    double? LatitudeDegrees,
    double? LongitudeDegrees,
    double? AltitudeMslMetres,
    double? AltitudeAglMetres,
    double? HeadingDegrees,
    string? SelectedTrackId,
    DateTimeOffset? MediaTimestamp,
    string? TimelineItemId = null);

public sealed record DisplayedFrameCaptureRequest(
    byte[] BgraPixels,
    VideoFrameInfo Frame,
    VideoOverlayScene Overlay,
    bool IncludeOverlays,
    EvidenceCaptureContext Context);

public sealed record VideoClipExportRequest(
    string SourcePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    EvidenceCaptureContext Context,
    string? PreferredExtension = null);

public sealed record EvidenceRecord(
    string Id,
    EvidenceKind Kind,
    string Title,
    string FilePath,
    string MetadataPath,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MediaStartedAt,
    DateTimeOffset? MediaEndedAt,
    bool IncludesOverlays,
    EvidenceCaptureContext Context)
{
    public TimeSpan? Duration => MediaStartedAt is not null && MediaEndedAt > MediaStartedAt
        ? MediaEndedAt - MediaStartedAt
        : null;
}

public sealed record EvidenceLibrarySnapshot(
    IReadOnlyList<EvidenceRecord> Items,
    long TotalBytes,
    string RootPath,
    DateTimeOffset UpdatedAt)
{
    public static EvidenceLibrarySnapshot Empty(string rootPath) => new(
        [],
        0,
        rootPath,
        DateTimeOffset.UtcNow);
}

public sealed record EvidenceManifest(
    string Schema,
    string Id,
    EvidenceKind Kind,
    string Title,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MediaStartedAt,
    DateTimeOffset? MediaEndedAt,
    bool IncludesOverlays,
    EvidenceCaptureContext Context)
{
    public const string CurrentSchema = "logos.evidence.v1";

    public static string CreateId(
        EvidenceKind kind,
        DateTimeOffset createdAt,
        EvidenceCaptureContext context)
    {
        var input = string.Join('\n',
            kind,
            createdAt.ToUniversalTime().ToString("O"),
            context.ConnectionId,
            context.VehicleId,
            context.CameraSourceId,
            context.StreamId,
            context.MediaTimestamp?.ToUniversalTime().ToString("O"),
            Guid.NewGuid().ToString("N"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "evidence-" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}

public sealed record SourceImageCaptureStatus(
    bool Available,
    string Summary,
    string Detail)
{
    public static SourceImageCaptureStatus Unavailable(string detail) => new(
        false,
        "Source image capture unavailable",
        detail);
}

public sealed record SourceImageCaptureRequest(
    string ConnectionId,
    string CameraSourceId,
    EvidenceCaptureContext Context);

public sealed record SourceImageCaptureResult(
    bool Succeeded,
    string Message,
    EvidenceRecord? Evidence = null);
