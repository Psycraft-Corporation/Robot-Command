using System.Collections.Specialized;
using RobotCommand.Core;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Evidence;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Media;
using RobotCommand.Services.Team;
using RobotCommand.State;

namespace RobotCommand.Services.Workflows;

/// <summary>Projects the evidence library without exposing local source paths to front ends.</summary>
public sealed class EvidenceWorkflow : IEvidenceWorkflow
{
    private readonly IEvidenceLibrary _library;
    public EvidenceWorkflow(IEvidenceLibrary library)
    {
        _library = library;
        _library.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Changed;
    public EvidenceWorkflowSnapshot Current => Project(_library.Snapshot, "Evidence library is current.");
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _library.RefreshAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task ExportAsync(string evidenceId, string destinationPath, CancellationToken cancellationToken = default)
    {
        var item = _library.Snapshot.Items.FirstOrDefault(value => value.Id == evidenceId)
            ?? throw new KeyNotFoundException($"Evidence '{evidenceId}' was not found.");
        var target = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new ArgumentException("An export file path is required.", nameof(destinationPath)));
        await using var source = File.OpenRead(item.FilePath);
        await using var destination = File.Create(target);
        await source.CopyToAsync(destination, cancellationToken);
    }
    public Task RemoveAsync(string evidenceId, CancellationToken cancellationToken = default) => _library.DeleteAsync(evidenceId, cancellationToken);
    private static EvidenceWorkflowSnapshot Project(EvidenceLibrarySnapshot source, string status) => new(
        source.Items.Select(item => new EvidenceWorkflowItem(item.Id, item.Kind.ToString(), item.Title, item.FileName, item.MediaType,
            item.SizeBytes, item.CreatedAt, item.MediaStartedAt, item.MediaEndedAt,
            string.Join(" · ", new[] { item.Context.VehicleName, item.Context.CameraSourceId, item.Context.MissionId }.Where(value => !string.IsNullOrWhiteSpace(value))!))).ToArray(),
        source.TotalBytes, source.RootPath, source.UpdatedAt, status);
}

/// <summary>Read-only media projection. Playback surfaces remain owned by Avalonia.</summary>
public sealed class MediaWorkflow : IMediaWorkflow
{
    private readonly IEntityStore<string, CameraSourceRecord> _sources;
    private readonly IEntityStore<string, CameraStreamRecord> _streams;
    private readonly IUnifiedVideoTimelineService _timeline;
    public MediaWorkflow(IEntityStore<string, CameraSourceRecord> sources, IEntityStore<string, CameraStreamRecord> streams, IUnifiedVideoTimelineService timeline)
    {
        _sources = sources; _streams = streams; _timeline = timeline;
        Observe(_sources.Items); Observe(_streams.Items); _timeline.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Changed;
    public MediaWorkflowSnapshot Current => new(
        _sources.Items.Select(item => new MediaSourceWorkflowSnapshot(item.Id, item.Name, item.Kind, item.ConnectionId, item.State.ToString(), item.Active, item.Fresh, item.HasImage, item.FrameRateHz, item.LatencyMilliseconds, item.Width, item.Height, item.Message, item.ObservedAt)).ToArray(),
        _streams.Items.Select(item => new MediaStreamWorkflowSnapshot(item.Id, item.CameraSourceId, item.Protocol, item.State, item.Codec, item.Width, item.Height, item.FrameRateHz, item.BitrateKbps, item.OpenedAt, item.ExpiresAt, item.Message)).ToArray(),
        _timeline.LocalStatus.Summary, _timeline.LocalStatus.Detail, _timeline.Timeline.Mode.ToString(), "Media state is current.", DateTimeOffset.UtcNow);
    public async Task RefreshAsync(CancellationToken cancellationToken = default) { await _timeline.RefreshAsync(cancellationToken); Changed?.Invoke(this, EventArgs.Empty); }
    private void Observe(INotifyCollectionChanged collection) => collection.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class MapLibraryWorkflow : IMapLibraryWorkflow
{
    private readonly IMapPackageCatalog _catalog;
    private readonly IMapPackageInstaller _installer;
    private readonly IMapPackageValidator _validator;
    private readonly IMapViewWorkflow _map;
    public MapLibraryWorkflow(IMapPackageCatalog catalog, IMapPackageInstaller installer, IMapPackageValidator validator, IMapViewWorkflow map)
    {
        _catalog = catalog; _installer = installer; _validator = validator; _map = map;
        _catalog.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty); _map.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Changed;
    public MapLibraryWorkflowSnapshot Current => new(_catalog.Packages.Select(Project).ToArray(), _catalog.ActivePackage?.Key, _map.SavedViews, "Map library is current.");
    public async Task RefreshAsync(CancellationToken cancellationToken = default) { await _catalog.RefreshAsync(cancellationToken); Changed?.Invoke(this, EventArgs.Empty); }
    public async Task<MapPackageWorkflowSnapshot> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
        => Project((await _installer.ImportAsync(sourcePath, cancellationToken)).Package);
    public Task ActivateAsync(string packageKey, CancellationToken cancellationToken = default) => _catalog.ActivateAsync(packageKey, cancellationToken);
    public Task RemoveAsync(string packageKey, CancellationToken cancellationToken = default) => _installer.RemoveAsync(packageKey, cancellationToken);
    public Task<IReadOnlyList<SavedMapViewWorkflowSnapshot>> ListViewsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_map.SavedViews);
    public Task<MapPackageWorkflowSnapshot> ValidateAsync(string packageKey, CancellationToken cancellationToken = default)
    {
        if (!_catalog.TryGet(packageKey, out var package) || package is null) throw new KeyNotFoundException($"Map package '{packageKey}' was not found.");
        var validation = _validator.Validate(package.DirectoryPath);
        return Task.FromResult(Project(package with { Valid = validation.IsValid, ValidationSummary = validation.Summary, ValidationIssues = validation.Issues }));
    }
    private static MapPackageWorkflowSnapshot Project(InstalledMapPackage value) => new(value.Key, value.PackageId, value.Version, value.DisplayName, value.Kind.ToString(), value.StateLabel, value.CoverageSummary, value.ZoomSummary, value.InstalledSizeBytes, value.Valid, value.ValidationSummary, value.ValidationIssues, value.StyleIds);
}

public sealed class TeamObserverClientWorkflow : ITeamObserverClientWorkflow
{
    private readonly IRobotCommandObserverService _observers;
    private readonly ILanTeamDiscoveryService _discovery;
    private string _status = "Ready";
    public TeamObserverClientWorkflow(IRobotCommandObserverService observers, ILanTeamDiscoveryService discovery)
    {
        _observers = observers; _discovery = discovery;
        _observers.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty); _discovery.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Changed;
    public TeamObserverClientWorkflowSnapshot Current => new(
        _discovery.Servers.Select(server => new TeamDiscoveryWorkflowSnapshot(server.InstanceId, server.DisplayName, server.Endpoint.ToString(), server.ApiVersion, _discovery.Status)).ToArray(),
        _observers.Observers.Select(server => new TeamObserverServerWorkflowSnapshot(server.Id, server.Endpoint, server.DisplayName, server.Status, server.Detail, server.Fingerprint, server.ConnectedAt, server.DisconnectReason)).ToArray(), _status);
    public async Task DiscoverAsync(CancellationToken cancellationToken = default) { await _discovery.RefreshAsync(cancellationToken); _status = _discovery.Status; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task ProbeAsync(string endpoint, CancellationToken cancellationToken = default) { var probe = await _observers.ProbeAsync(endpoint, cancellationToken); _status = $"Found {probe.ServerInfo.DisplayName} {probe.ServerInfo.ApiVersion}."; Changed?.Invoke(this, EventArgs.Empty); }
    public async Task ConnectAsync(string endpoint, string displayName, string? passphrase = null, CancellationToken cancellationToken = default)
    {
        var probe = await _observers.ProbeAsync(endpoint, cancellationToken);
        if (probe.ServerInfo.RequiresPassphrase && string.IsNullOrWhiteSpace(passphrase))
            throw new InvalidOperationException("This Robot Command requires a passphrase.");
        if (probe.ServerInfo.RequiresPassphrase)
            await _observers.ConnectWithPassphraseAsync(endpoint, passphrase!, displayName, null, cancellationToken);
        else
            await _observers.ConnectAsync(endpoint, probe.ObservedCertificateFingerprint, displayName, string.Empty, null, cancellationToken);
        _status = "Access requested."; Changed?.Invoke(this, EventArgs.Empty);
    }
    public Task DisconnectAsync(string observerId, string? reason = null, CancellationToken cancellationToken = default) => _observers.DisconnectAsync(observerId, cancellationToken);
}

public sealed class ApplicationPreferencesWorkflow : IApplicationPreferencesWorkflow
{
    private readonly IApplicationSettingsService _settings;
    private readonly IUnitSettingsService _units;
    private readonly ILocalizationService? _localization;
    public ApplicationPreferencesWorkflow(IApplicationSettingsService settings, IUnitSettingsService units, ILocalizationService? localization = null)
    {
        _settings = settings; _units = units; _localization = localization; _settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty); _units.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Changed;
    public ApplicationPreferencesSnapshot Current { get { var units = _units.Current; return new(_settings.Current.Language, _settings.Current.BrightModeEnabled, units.HorizontalDistance.ToString(), units.VerticalDistance.ToString(), units.Area.ToString(), units.Speed.ToString(), units.Temperature.ToString()); } }
    public Task SetLanguageAsync(string language, CancellationToken cancellationToken = default) => _localization?.SetLanguageAsync(language, cancellationToken) ?? _settings.SetLanguageAsync(language, cancellationToken);
    public Task SetBrightModeAsync(bool enabled, CancellationToken cancellationToken = default) => _settings.SetBrightModeEnabledAsync(enabled, cancellationToken);
    public Task SetUnitsAsync(string horizontalDistance, string verticalDistance, string area, string speed, string temperature, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<DistanceUnit>(horizontalDistance, true, out var horizontal) || !Enum.TryParse<DistanceUnit>(verticalDistance, true, out var vertical) || !Enum.TryParse<AreaUnit>(area, true, out var parsedArea) || !Enum.TryParse<SpeedUnit>(speed, true, out var parsedSpeed) || !Enum.TryParse<TemperatureUnit>(temperature, true, out var parsedTemperature))
            throw new ArgumentException("One or more display-unit values are invalid.");
        return _units.SetAsync(new AppUnitSettings(horizontal, vertical, parsedArea, parsedSpeed, parsedTemperature), cancellationToken);
    }
}
