namespace RobotCommand.Core;

/// <summary>Redacted evidence metadata suitable for any Robot Command front end.</summary>
public sealed record EvidenceWorkflowItem(
    string Id, string Kind, string Title, string FileName, string MediaType,
    long SizeBytes, DateTimeOffset CreatedAt, DateTimeOffset? MediaStartedAt,
    DateTimeOffset? MediaEndedAt, string Summary);

public sealed record EvidenceWorkflowSnapshot(
    IReadOnlyList<EvidenceWorkflowItem> Items, long TotalBytes, string RootPath,
    DateTimeOffset UpdatedAt, string Status);

public interface IEvidenceWorkflow
{
    event EventHandler? Changed;
    EvidenceWorkflowSnapshot Current { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string evidenceId, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveAsync(string evidenceId, CancellationToken cancellationToken = default);
}

public sealed record MediaSourceWorkflowSnapshot(
    string Id, string Name, string Kind, string ConnectionId, string State,
    bool Active, bool Fresh, bool HasImage, double FrameRateHertz,
    double LatencyMilliseconds, uint Width, uint Height, string Message,
    DateTimeOffset ObservedAt);

public sealed record MediaStreamWorkflowSnapshot(
    string Id, string SourceId, string Protocol, string State, string Codec,
    uint Width, uint Height, double FrameRateHertz, uint BitrateKbps,
    DateTimeOffset? OpenedAt, DateTimeOffset? ExpiresAt, string Message);

public sealed record MediaWorkflowSnapshot(
    IReadOnlyList<MediaSourceWorkflowSnapshot> Sources,
    IReadOnlyList<MediaStreamWorkflowSnapshot> Streams,
    string RecordingState, string RecordingDetail, string PlaybackState,
    string Status, DateTimeOffset UpdatedAt);

/// <summary>Metadata and recording lifecycle only. Rendering stays in the GUI.</summary>
public interface IMediaWorkflow
{
    event EventHandler? Changed;
    MediaWorkflowSnapshot Current { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed record MapPackageWorkflowSnapshot(
    string Key, string PackageId, string Version, string DisplayName, string Kind,
    string State, string Coverage, string Zoom, long SizeBytes, bool Valid,
    string ValidationSummary, IReadOnlyList<string> ValidationIssues,
    IReadOnlyList<string> StyleIds);

public sealed record MapLibraryWorkflowSnapshot(
    IReadOnlyList<MapPackageWorkflowSnapshot> Packages, string? ActivePackageKey,
    IReadOnlyList<SavedMapViewWorkflowSnapshot> SavedViews, string Status);

public interface IMapLibraryWorkflow
{
    event EventHandler? Changed;
    MapLibraryWorkflowSnapshot Current { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<MapPackageWorkflowSnapshot> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<MapPackageWorkflowSnapshot> ValidateAsync(string packageKey, CancellationToken cancellationToken = default);
    Task ActivateAsync(string packageKey, CancellationToken cancellationToken = default);
    Task RemoveAsync(string packageKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedMapViewWorkflowSnapshot>> ListViewsAsync(CancellationToken cancellationToken = default);
}

public sealed record TeamObserverServerWorkflowSnapshot(
    string Id, string Endpoint, string DisplayName, string Status, string Detail,
    string Fingerprint, DateTimeOffset? ConnectedAt, string DisconnectReason);
public sealed record TeamDiscoveryWorkflowSnapshot(
    string Id, string DisplayName, string Endpoint, string ApiVersion, string Status);
public sealed record TeamObserverClientWorkflowSnapshot(
    IReadOnlyList<TeamDiscoveryWorkflowSnapshot> Nearby,
    IReadOnlyList<TeamObserverServerWorkflowSnapshot> Connected, string Status);

public interface ITeamObserverClientWorkflow
{
    event EventHandler? Changed;
    TeamObserverClientWorkflowSnapshot Current { get; }
    Task DiscoverAsync(CancellationToken cancellationToken = default);
    Task ProbeAsync(string endpoint, CancellationToken cancellationToken = default);
    Task ConnectAsync(string endpoint, string displayName, string? passphrase = null, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string observerId, string? reason = null, CancellationToken cancellationToken = default);
}

public sealed record ApplicationPreferencesSnapshot(
    string Language, bool BrightModeEnabled, string HorizontalDistance,
    string VerticalDistance, string Area, string Speed, string Temperature);

public interface IApplicationPreferencesWorkflow
{
    event EventHandler? Changed;
    ApplicationPreferencesSnapshot Current { get; }
    Task SetLanguageAsync(string language, CancellationToken cancellationToken = default);
    Task SetBrightModeAsync(bool enabled, CancellationToken cancellationToken = default);
    Task SetUnitsAsync(string horizontalDistance, string verticalDistance, string area, string speed, string temperature, CancellationToken cancellationToken = default);
}
