using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Terrain;

namespace RobotCommand.Services;

public sealed class AppConfiguration
{
    public AppUiSettings Ui { get; init; } = new();

    public IReadOnlyList<ConnectionProfile> Connections { get; init; } =
        [new("Local Logos", "http://localhost:50051")];

    // Compatibility alias for existing callers and legacy configuration tests.
    public IReadOnlyList<ConnectionProfile> Profiles => Connections;

    public int RefreshSeconds { get; init; } = 2;

    public int StaleAfterSeconds { get; init; } = 10;

    public int OfflineAfterSeconds { get; init; } = 30;

    public bool LiveStreamsEnabled { get; init; } = true;

    public int StreamHeartbeatSeconds { get; init; } = 2;

    public int StreamRetrySeconds { get; init; } = 2;

    public int PollFallbackSeconds { get; init; } = 15;

    public int MaxEvents { get; init; } = 500;

    public int MaxGeometryObjects { get; init; } = 500;

    public int MaxPerceptionTracks { get; init; } = 100;

    public double PerceptionMinConfidence { get; init; } = 0.25;

    public int MaxCameraSources { get; init; } = 32;

    public int MaxCommandHistory { get; init; } = 250;

    public int OperatorConfirmationTimeoutSeconds { get; init; } = 30;

    public bool RequireTypedOperatorConfirmation { get; init; } = true;

    public string? OperationalMapPath { get; init; }

    public string OperationalMapAttribution { get; init; } = "Offline operational map";

    public bool OnlineMapFallbackEnabled { get; init; } = true;

    public double MapDefaultLongitude { get; init; } = -79.42;

    public double MapDefaultLatitude { get; init; } = 43.73;

    public double MapDefaultResolution { get; init; } = 60;

    public string MapLibraryPath { get; init; } = ResolveMapLibraryPath(AppContext.BaseDirectory, null);

    public int MapTrailMaxAgeMinutes { get; init; } = 15;

    public int MapTrailMaxPointsPerVehicle { get; init; } = 600;

    public double MapTrailMinimumDistanceMetres { get; init; } = 2;

    public string? GStreamerBinPath { get; init; }

    public string? GStreamerPluginPath { get; init; }

    public string? GStreamerTestFilePath { get; init; }

    public int GStreamerTestWidth { get; init; } = 960;

    public int GStreamerTestHeight { get; init; } = 540;

    public int GStreamerTestFrameRate { get; init; } = 30;

    public string GStreamerTestPattern { get; init; } = "smpte";

    public RtspTransportMode GStreamerRtspTransport { get; init; } = RtspTransportMode.Tcp;

    public int GStreamerRtspLatencyMilliseconds { get; init; } = 100;

    public int GStreamerRtspReconnectAttempts { get; init; } = 3;

    public int GStreamerRtspReconnectDelayMilliseconds { get; init; } = 2000;

    public int GStreamerSrtLatencyMilliseconds { get; init; } = 125;

    public int GStreamerHlsTimeoutSeconds { get; init; } = 15;

    public int GStreamerWhepTimeoutSeconds { get; init; } = 15;

    public bool GStreamerWhepUseLinkHeaders { get; init; } = true;

    public bool LocalVideoRecordingEnabled { get; init; } = true;

    public string LocalVideoRecordingPath { get; init; } = ResolveLocalVideoRecordingPath(AppContext.BaseDirectory, null);

    public int LocalVideoRollingBufferMinutes { get; init; } = 10;

    public int LocalVideoSegmentSeconds { get; init; } = 10;

    public int LocalVideoMaximumStorageGigabytes { get; init; } = 20;

    public int LocalVideoEncodingBitrateKbps { get; init; } = 4000;

    public IReadOnlyList<RemoteVideoRecordingProfile> RemoteVideoRecordingProfiles { get; init; } = [];

    public string RemoteVideoCachePath { get; init; } = ResolveRemoteVideoCachePath(AppContext.BaseDirectory, null);

    public int RemoteVideoLookbackHours { get; init; } = 24;

    public int RemoteVideoRequestTimeoutSeconds { get; init; } = 20;

    public int RemoteVideoCacheMaximumGigabytes { get; init; } = 50;

    public int RemoteVideoMaximumDownloadGigabytes { get; init; } = 10;

    public string EvidenceLibraryPath { get; init; } = ResolveEvidenceLibraryPath(AppContext.BaseDirectory, null);

    public string BehaviourLibraryPath { get; init; } = BehaviourPackageLibraryPaths.DefaultRootPath(AppContext.BaseDirectory);

    public int MaximumBehaviourPackageBytes { get; init; } = 50 * 1024 * 1024;

    public int MaximumBehaviourPackageFiles { get; init; } = 500;

    public TerrainOptions Terrain { get; init; } = new();

    public static AppConfiguration Load(string baseDirectory)
    {
        var config = new MutableConfiguration();
        Merge(config, Path.Combine(baseDirectory, "appsettings.json"));
        Merge(config, Path.Combine(baseDirectory, "appsettings.local.json"));

        var validProfiles = config.Connections
            .Where(profile => Uri.TryCreate(profile.Target, UriKind.Absolute, out _))
            .ToArray();

        IReadOnlyList<ConnectionProfile> profiles = validProfiles.Length == 0
            ? [new ConnectionProfile("Local Logos", "http://localhost:50051")]
            : validProfiles;

        var refreshSeconds = config.RefreshSeconds <= 0 ? 2 : config.RefreshSeconds;
        var staleAfterSeconds = Math.Max(
            config.StaleAfterSeconds <= 0 ? refreshSeconds * 5 : config.StaleAfterSeconds,
            refreshSeconds * 2);
        var offlineAfterSeconds = Math.Max(
            config.OfflineAfterSeconds <= 0 ? staleAfterSeconds * 3 : config.OfflineAfterSeconds,
            staleAfterSeconds + refreshSeconds);
        var streamHeartbeatSeconds = config.StreamHeartbeatSeconds <= 0 ? 2 : config.StreamHeartbeatSeconds;
        var streamRetrySeconds = config.StreamRetrySeconds <= 0 ? 2 : config.StreamRetrySeconds;
        var pollFallbackSeconds = Math.Max(
            config.PollFallbackSeconds <= 0 ? 15 : config.PollFallbackSeconds,
            refreshSeconds);
        var maxEvents = Math.Clamp(config.MaxEvents <= 0 ? 500 : config.MaxEvents, 50, 5000);
        var maxGeometryObjects = Math.Clamp(config.MaxGeometryObjects <= 0 ? 500 : config.MaxGeometryObjects, 10, 5000);
        var maxPerceptionTracks = Math.Clamp(config.MaxPerceptionTracks <= 0 ? 100 : config.MaxPerceptionTracks, 1, 1000);
        var perceptionMinConfidence = Math.Clamp(config.PerceptionMinConfidence, 0, 1);
        var maxCameraSources = Math.Clamp(config.MaxCameraSources <= 0 ? 32 : config.MaxCameraSources, 1, 256);
        var maxCommandHistory = Math.Clamp(config.MaxCommandHistory <= 0 ? 250 : config.MaxCommandHistory, 25, 5000);
        var operatorConfirmationTimeoutSeconds = Math.Clamp(
            config.OperatorConfirmationTimeoutSeconds <= 0 ? 30 : config.OperatorConfirmationTimeoutSeconds,
            10,
            300);
        var mapTrailMaxAgeMinutes = Math.Clamp(
            config.MapTrailMaxAgeMinutes <= 0 ? 15 : config.MapTrailMaxAgeMinutes,
            1,
            24 * 60);
        var mapTrailMaxPointsPerVehicle = Math.Clamp(
            config.MapTrailMaxPointsPerVehicle <= 0 ? 600 : config.MapTrailMaxPointsPerVehicle,
            10,
            20_000);
        var mapTrailMinimumDistanceMetres = Math.Clamp(
            config.MapTrailMinimumDistanceMetres < 0 ? 2 : config.MapTrailMinimumDistanceMetres,
            0,
            10_000);
        var mapDefaultLongitude = double.IsFinite(config.MapDefaultLongitude) &&
                                  config.MapDefaultLongitude is >= -180 and <= 180
            ? config.MapDefaultLongitude
            : -79.42;
        var mapDefaultLatitude = double.IsFinite(config.MapDefaultLatitude) &&
                                 config.MapDefaultLatitude is >= -90 and <= 90
            ? config.MapDefaultLatitude
            : 43.73;
        var mapDefaultResolution = double.IsFinite(config.MapDefaultResolution) &&
                                   config.MapDefaultResolution > 0
            ? Math.Clamp(config.MapDefaultResolution, 1, 100_000)
            : 60;
        var gStreamerTestWidth = Math.Clamp(
            config.GStreamerTestWidth <= 0 ? 960 : config.GStreamerTestWidth,
            160,
            3840);
        var gStreamerTestHeight = Math.Clamp(
            config.GStreamerTestHeight <= 0 ? 540 : config.GStreamerTestHeight,
            90,
            2160);
        var gStreamerTestFrameRate = Math.Clamp(
            config.GStreamerTestFrameRate <= 0 ? 30 : config.GStreamerTestFrameRate,
            1,
            60);
        var gStreamerRtspLatencyMilliseconds = Math.Clamp(
            config.GStreamerRtspLatencyMilliseconds < 0 ? 100 : config.GStreamerRtspLatencyMilliseconds,
            0,
            60_000);
        var gStreamerRtspReconnectAttempts = Math.Clamp(
            config.GStreamerRtspReconnectAttempts < 0 ? 3 : config.GStreamerRtspReconnectAttempts,
            0,
            20);
        var gStreamerRtspReconnectDelayMilliseconds = Math.Clamp(
            config.GStreamerRtspReconnectDelayMilliseconds <= 0 ? 2000 : config.GStreamerRtspReconnectDelayMilliseconds,
            100,
            30_000);
        var gStreamerSrtLatencyMilliseconds = Math.Clamp(
            config.GStreamerSrtLatencyMilliseconds < 0 ? 125 : config.GStreamerSrtLatencyMilliseconds,
            0,
            60_000);
        var gStreamerHlsTimeoutSeconds = Math.Clamp(
            config.GStreamerHlsTimeoutSeconds <= 0 ? 15 : config.GStreamerHlsTimeoutSeconds,
            1,
            300);
        var gStreamerWhepTimeoutSeconds = Math.Clamp(
            config.GStreamerWhepTimeoutSeconds <= 0 ? 15 : config.GStreamerWhepTimeoutSeconds,
            1,
            300);
        var localVideoRollingBufferMinutes = Math.Clamp(
            config.LocalVideoRollingBufferMinutes <= 0 ? 10 : config.LocalVideoRollingBufferMinutes,
            1,
            24 * 60);
        var localVideoSegmentSeconds = Math.Clamp(
            config.LocalVideoSegmentSeconds <= 0 ? 10 : config.LocalVideoSegmentSeconds,
            2,
            300);
        var localVideoMaximumStorageGigabytes = Math.Clamp(
            config.LocalVideoMaximumStorageGigabytes <= 0 ? 20 : config.LocalVideoMaximumStorageGigabytes,
            1,
            2048);
        var localVideoEncodingBitrateKbps = Math.Clamp(
            config.LocalVideoEncodingBitrateKbps <= 0 ? 4000 : config.LocalVideoEncodingBitrateKbps,
            128,
            100_000);
        var remoteVideoLookbackHours = Math.Clamp(
            config.RemoteVideoLookbackHours <= 0 ? 24 : config.RemoteVideoLookbackHours,
            1,
            24 * 90);
        var remoteVideoRequestTimeoutSeconds = Math.Clamp(
            config.RemoteVideoRequestTimeoutSeconds <= 0 ? 20 : config.RemoteVideoRequestTimeoutSeconds,
            2,
            300);
        var remoteVideoCacheMaximumGigabytes = Math.Clamp(
            config.RemoteVideoCacheMaximumGigabytes <= 0 ? 50 : config.RemoteVideoCacheMaximumGigabytes,
            1,
            4096);
        var remoteVideoMaximumDownloadGigabytes = Math.Clamp(
            config.RemoteVideoMaximumDownloadGigabytes <= 0 ? 10 : config.RemoteVideoMaximumDownloadGigabytes,
            1,
            1024);
        var maximumBehaviourPackageBytes = Math.Clamp(
            config.MaximumBehaviourPackageBytes <= 0 ? 50 * 1024 * 1024 : config.MaximumBehaviourPackageBytes,
            1024 * 1024,
            1024 * 1024 * 1024);
        var maximumBehaviourPackageFiles = Math.Clamp(
            config.MaximumBehaviourPackageFiles <= 0 ? 500 : config.MaximumBehaviourPackageFiles,
            10,
            10_000);
        var remoteVideoProfiles = config.RemoteVideoRecordingProfiles
            .Where(profile => profile.Enabled &&
                Uri.TryCreate(profile.PlaybackBaseUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" &&
                string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment) &&
                !string.IsNullOrWhiteSpace(profile.PathTemplate) &&
                (!string.IsNullOrWhiteSpace(profile.ConnectionId) || !string.IsNullOrWhiteSpace(profile.ConnectionTarget)))
            .ToArray();

        return new AppConfiguration
        {
            Ui = new AppUiSettings(
                config.BrightModeEnabled,
                NormalizeLanguage(config.Language),
                Math.Clamp(config.OperateInspectorWidth <= 0 ? 380 : config.OperateInspectorWidth, 340, 460))
            {
                Units = new AppUnitSettings(
                    config.HorizontalDistanceUnit,
                    config.VerticalDistanceUnit,
                    config.AreaUnit,
                    config.SpeedUnit,
                    config.TemperatureUnit)
            },
            Connections = profiles,
            RefreshSeconds = refreshSeconds,
            StaleAfterSeconds = staleAfterSeconds,
            OfflineAfterSeconds = offlineAfterSeconds,
            LiveStreamsEnabled = config.LiveStreamsEnabled,
            StreamHeartbeatSeconds = streamHeartbeatSeconds,
            StreamRetrySeconds = streamRetrySeconds,
            PollFallbackSeconds = pollFallbackSeconds,
            MaxEvents = maxEvents,
            MaxGeometryObjects = maxGeometryObjects,
            MaxPerceptionTracks = maxPerceptionTracks,
            PerceptionMinConfidence = perceptionMinConfidence,
            MaxCameraSources = maxCameraSources,
            MaxCommandHistory = maxCommandHistory,
            OperatorConfirmationTimeoutSeconds = operatorConfirmationTimeoutSeconds,
            RequireTypedOperatorConfirmation = config.RequireTypedOperatorConfirmation,
            OperationalMapPath = ResolveOptionalPath(baseDirectory, config.OperationalMapPath),
            OperationalMapAttribution = string.IsNullOrWhiteSpace(config.OperationalMapAttribution)
                ? "Offline operational map"
                : config.OperationalMapAttribution.Trim(),
            OnlineMapFallbackEnabled = config.OnlineMapFallbackEnabled,
            MapDefaultLongitude = mapDefaultLongitude,
            MapDefaultLatitude = mapDefaultLatitude,
            MapDefaultResolution = mapDefaultResolution,
            MapLibraryPath = ResolveMapLibraryPath(baseDirectory, config.MapLibraryPath),
            MapTrailMaxAgeMinutes = mapTrailMaxAgeMinutes,
            MapTrailMaxPointsPerVehicle = mapTrailMaxPointsPerVehicle,
            MapTrailMinimumDistanceMetres = mapTrailMinimumDistanceMetres,
            GStreamerBinPath = ResolveOptionalPath(baseDirectory, config.GStreamerBinPath),
            GStreamerPluginPath = ResolveOptionalPath(baseDirectory, config.GStreamerPluginPath),
            GStreamerTestFilePath = ResolveOptionalPath(baseDirectory, config.GStreamerTestFilePath),
            GStreamerTestWidth = gStreamerTestWidth,
            GStreamerTestHeight = gStreamerTestHeight,
            GStreamerTestFrameRate = gStreamerTestFrameRate,
            GStreamerTestPattern = string.IsNullOrWhiteSpace(config.GStreamerTestPattern)
                ? "smpte"
                : config.GStreamerTestPattern.Trim(),
            GStreamerRtspTransport = config.GStreamerRtspTransport,
            GStreamerRtspLatencyMilliseconds = gStreamerRtspLatencyMilliseconds,
            GStreamerRtspReconnectAttempts = gStreamerRtspReconnectAttempts,
            GStreamerRtspReconnectDelayMilliseconds = gStreamerRtspReconnectDelayMilliseconds,
            GStreamerSrtLatencyMilliseconds = gStreamerSrtLatencyMilliseconds,
            GStreamerHlsTimeoutSeconds = gStreamerHlsTimeoutSeconds,
            GStreamerWhepTimeoutSeconds = gStreamerWhepTimeoutSeconds,
            GStreamerWhepUseLinkHeaders = config.GStreamerWhepUseLinkHeaders,
            LocalVideoRecordingEnabled = config.LocalVideoRecordingEnabled,
            LocalVideoRecordingPath = ResolveLocalVideoRecordingPath(baseDirectory, config.LocalVideoRecordingPath),
            LocalVideoRollingBufferMinutes = localVideoRollingBufferMinutes,
            LocalVideoSegmentSeconds = localVideoSegmentSeconds,
            LocalVideoMaximumStorageGigabytes = localVideoMaximumStorageGigabytes,
            LocalVideoEncodingBitrateKbps = localVideoEncodingBitrateKbps,
            RemoteVideoRecordingProfiles = remoteVideoProfiles,
            RemoteVideoCachePath = ResolveRemoteVideoCachePath(baseDirectory, config.RemoteVideoCachePath),
            RemoteVideoLookbackHours = remoteVideoLookbackHours,
            RemoteVideoRequestTimeoutSeconds = remoteVideoRequestTimeoutSeconds,
            RemoteVideoCacheMaximumGigabytes = remoteVideoCacheMaximumGigabytes,
            RemoteVideoMaximumDownloadGigabytes = remoteVideoMaximumDownloadGigabytes,
            EvidenceLibraryPath = ResolveEvidenceLibraryPath(baseDirectory, config.EvidenceLibraryPath),
            BehaviourLibraryPath = BehaviourPackageLibraryPaths.ResolveRootPath(baseDirectory, config.BehaviourLibraryPath),
            MaximumBehaviourPackageBytes = maximumBehaviourPackageBytes,
            MaximumBehaviourPackageFiles = maximumBehaviourPackageFiles,
            Terrain = new TerrainOptions(
                config.TerrainProviderId,
                config.TerrainEndpoint,
                config.TerrainTimeoutSeconds,
                config.TerrainCacheMaximumAgeDays,
                config.TerrainCacheMaximumEntries,
                config.TerrainRequestsPerSecond,
                ResolveTerrainCachePath(baseDirectory, config.TerrainCachePath)).Normalize()
        };
    }

    private static void Merge(MutableConfiguration config, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        if (root.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.Object)
        {
            // highContrast was the preview setting name. Keep it as a read-only
            // migration path so existing local settings retain their choice.
            config.BrightModeEnabled = ReadBoolean(ui, "brightMode")
                ?? ReadBoolean(ui, "highContrast")
                ?? config.BrightModeEnabled;
            config.Language = ReadString(ui, "language") ?? config.Language;
            config.OperateInspectorWidth = ReadDouble(ui, "operateInspectorWidth") ?? config.OperateInspectorWidth;
            if (ui.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Object)
            {
                config.HorizontalDistanceUnit = ReadEnum(units, "horizontalDistance", config.HorizontalDistanceUnit);
                config.VerticalDistanceUnit = ReadEnum(units, "verticalDistance", config.VerticalDistanceUnit);
                config.AreaUnit = ReadEnum(units, "area", config.AreaUnit);
                config.SpeedUnit = ReadEnum(units, "speed", config.SpeedUnit);
                config.TemperatureUnit = ReadEnum(units, "temperature", config.TemperatureUnit);
            }
        }

        MergePositiveInteger(root, "refreshSeconds", value => config.RefreshSeconds = value);
        MergePositiveInteger(root, "staleAfterSeconds", value => config.StaleAfterSeconds = value);
        MergePositiveInteger(root, "offlineAfterSeconds", value => config.OfflineAfterSeconds = value);
        MergePositiveInteger(root, "streamHeartbeatSeconds", value => config.StreamHeartbeatSeconds = value);
        MergePositiveInteger(root, "streamRetrySeconds", value => config.StreamRetrySeconds = value);
        MergePositiveInteger(root, "pollFallbackSeconds", value => config.PollFallbackSeconds = value);
        MergePositiveInteger(root, "maxEvents", value => config.MaxEvents = value);
        MergePositiveInteger(root, "maxGeometryObjects", value => config.MaxGeometryObjects = value);
        MergePositiveInteger(root, "maxPerceptionTracks", value => config.MaxPerceptionTracks = value);
        MergePositiveInteger(root, "maxCameraSources", value => config.MaxCameraSources = value);
        MergePositiveInteger(root, "maxCommandHistory", value => config.MaxCommandHistory = value);
        MergePositiveInteger(root, "operatorConfirmationTimeoutSeconds", value => config.OperatorConfirmationTimeoutSeconds = value);
        MergeDouble(root, "perceptionMinConfidence", value => config.PerceptionMinConfidence = value);
        config.LiveStreamsEnabled = ReadBoolean(root, "liveStreamsEnabled") ?? config.LiveStreamsEnabled;
        config.RequireTypedOperatorConfirmation = ReadBoolean(root, "requireTypedOperatorConfirmation") ?? config.RequireTypedOperatorConfirmation;

        if (root.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Object)
        {
            config.OperationalMapPath = ReadString(maps, "operationalMbTilesPath") ?? config.OperationalMapPath;
            config.OperationalMapAttribution = ReadString(maps, "attribution") ?? config.OperationalMapAttribution;
            config.OnlineMapFallbackEnabled = ReadBoolean(maps, "onlineFallbackEnabled") ?? config.OnlineMapFallbackEnabled;
            config.MapLibraryPath = ReadString(maps, "libraryPath") ?? config.MapLibraryPath;
            MergeDouble(maps, "defaultLongitude", value => config.MapDefaultLongitude = value);
            MergeDouble(maps, "defaultLatitude", value => config.MapDefaultLatitude = value);
            MergeDouble(maps, "defaultResolution", value => config.MapDefaultResolution = value);
            MergePositiveInteger(maps, "trailMaxAgeMinutes", value => config.MapTrailMaxAgeMinutes = value);
            MergePositiveInteger(maps, "trailMaxPointsPerVehicle", value => config.MapTrailMaxPointsPerVehicle = value);
            MergeDouble(maps, "trailMinimumDistanceMetres", value => config.MapTrailMinimumDistanceMetres = value);
        }

        if (root.TryGetProperty("terrain", out var terrain) && terrain.ValueKind == JsonValueKind.Object)
        {
            config.TerrainProviderId = ReadString(terrain, "provider") ?? config.TerrainProviderId;
            config.TerrainEndpoint = ReadString(terrain, "endpoint") ?? config.TerrainEndpoint;
            config.TerrainCachePath = ReadString(terrain, "cachePath") ?? config.TerrainCachePath;
            MergePositiveInteger(terrain, "timeoutSeconds", value => config.TerrainTimeoutSeconds = value);
            MergePositiveInteger(terrain, "cacheMaximumAgeDays", value => config.TerrainCacheMaximumAgeDays = value);
            MergePositiveInteger(terrain, "cacheMaximumEntries", value => config.TerrainCacheMaximumEntries = value);
            MergePositiveInteger(terrain, "requestsPerSecond", value => config.TerrainRequestsPerSecond = value);
        }

        if (root.TryGetProperty("autonomy", out var autonomy) && autonomy.ValueKind == JsonValueKind.Object)
        {
            config.BehaviourLibraryPath = ReadString(autonomy, "behaviourLibraryPath")
                                          ?? ReadString(autonomy, "behaviorLibraryPath")
                                          ?? config.BehaviourLibraryPath;
            MergePositiveInteger(autonomy, "maximumBehaviourPackageBytes", value => config.MaximumBehaviourPackageBytes = value);
            MergePositiveInteger(autonomy, "maximumBehaviourPackageFiles", value => config.MaximumBehaviourPackageFiles = value);
        }

        if (root.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
        {
            config.GStreamerBinPath = ReadString(video, "gstreamerBinPath") ?? config.GStreamerBinPath;
            config.GStreamerPluginPath = ReadString(video, "gstreamerPluginPath") ?? config.GStreamerPluginPath;
            config.GStreamerTestFilePath = ReadString(video, "testFilePath") ?? config.GStreamerTestFilePath;
            config.GStreamerTestPattern = ReadString(video, "testPattern") ?? config.GStreamerTestPattern;
            MergePositiveInteger(video, "testWidth", value => config.GStreamerTestWidth = value);
            MergePositiveInteger(video, "testHeight", value => config.GStreamerTestHeight = value);
            MergePositiveInteger(video, "testFrameRate", value => config.GStreamerTestFrameRate = value);
            config.GStreamerRtspTransport = ParseRtspTransport(
                ReadString(video, "rtspTransport"),
                config.GStreamerRtspTransport);
            MergeInteger(video, "rtspLatencyMilliseconds", value => config.GStreamerRtspLatencyMilliseconds = value);
            MergeInteger(video, "rtspReconnectAttempts", value => config.GStreamerRtspReconnectAttempts = value);
            MergeInteger(video, "rtspReconnectDelayMilliseconds", value => config.GStreamerRtspReconnectDelayMilliseconds = value);
            MergeInteger(video, "srtLatencyMilliseconds", value => config.GStreamerSrtLatencyMilliseconds = value);
            MergeInteger(video, "hlsTimeoutSeconds", value => config.GStreamerHlsTimeoutSeconds = value);
            MergeInteger(video, "whepTimeoutSeconds", value => config.GStreamerWhepTimeoutSeconds = value);
            config.GStreamerWhepUseLinkHeaders = ReadBoolean(video, "whepUseLinkHeaders") ?? config.GStreamerWhepUseLinkHeaders;
            config.LocalVideoRecordingEnabled = ReadBoolean(video, "localRecordingEnabled") ?? config.LocalVideoRecordingEnabled;
            config.LocalVideoRecordingPath = ReadString(video, "localRecordingPath") ?? config.LocalVideoRecordingPath;
            MergeInteger(video, "rollingBufferMinutes", value => config.LocalVideoRollingBufferMinutes = value);
            MergeInteger(video, "segmentSeconds", value => config.LocalVideoSegmentSeconds = value);
            MergeInteger(video, "maximumStorageGigabytes", value => config.LocalVideoMaximumStorageGigabytes = value);
            MergeInteger(video, "encodingBitrateKbps", value => config.LocalVideoEncodingBitrateKbps = value);
            config.RemoteVideoCachePath = ReadString(video, "remoteRecordingCachePath") ?? config.RemoteVideoCachePath;
            MergeInteger(video, "remoteRecordingLookbackHours", value => config.RemoteVideoLookbackHours = value);
            MergeInteger(video, "remoteRecordingRequestTimeoutSeconds", value => config.RemoteVideoRequestTimeoutSeconds = value);
            MergeInteger(video, "remoteRecordingCacheMaximumGigabytes", value => config.RemoteVideoCacheMaximumGigabytes = value);
            MergeInteger(video, "remoteRecordingMaximumDownloadGigabytes", value => config.RemoteVideoMaximumDownloadGigabytes = value);
            config.EvidenceLibraryPath = ReadString(video, "evidenceLibraryPath") ?? config.EvidenceLibraryPath;
            if (video.TryGetProperty("remoteRecordingProfiles", out var remoteProfiles) &&
                remoteProfiles.ValueKind == JsonValueKind.Array)
            {
                config.RemoteVideoRecordingProfiles.Clear();
                foreach (var item in remoteProfiles.EnumerateArray())
                {
                    var name = ReadString(item, "name");
                    var playbackBaseUrl = ReadString(item, "playbackBaseUrl");
                    var pathTemplate = ReadString(item, "pathTemplate");
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(playbackBaseUrl) ||
                        string.IsNullOrWhiteSpace(pathTemplate))
                    {
                        continue;
                    }
                    config.RemoteVideoRecordingProfiles.Add(new RemoteVideoRecordingProfile(
                        name,
                        playbackBaseUrl,
                        pathTemplate,
                        ReadString(item, "connectionId"),
                        ReadString(item, "connectionTarget"),
                        ReadString(item, "bearerTokenEnvironmentVariable"),
                        ReadBoolean(item, "enabled") ?? true));
                }
            }
        }

        var hasConnections = root.TryGetProperty("connections", out var profiles);
        if (!hasConnections)
        {
            hasConnections = root.TryGetProperty("profiles", out profiles);
        }

        if (hasConnections &&
            profiles.ValueKind == JsonValueKind.Array)
        {
            config.Connections.Clear();
            foreach (var item in profiles.EnumerateArray())
            {
                var name = ReadString(item, "name");
                var target = ReadString(item, "target");
                var description = ReadString(item, "description");
                var id = ReadString(item, "id");
                var mode = ReadEnum(item, "mode", ConnectionMode.Direct);
                var autoConnect = ReadBoolean(item, "autoConnect") ?? false;
                var autoReconnect = ReadBoolean(item, "autoReconnect") ?? true;
                var mavlink = ReadMavlinkOptions(item, mode);
                var linkd = ReadLinkdOptions(item, mode);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                config.Connections.Add(new ConnectionProfile(
                    name,
                    target,
                    description,
                    id,
                    mode,
                    autoConnect,
                    autoReconnect,
                    mavlink,
                    linkd));
            }
        }
    }

    private static void MergePositiveInteger(
        JsonElement root,
        string propertyName,
        Action<int> apply)
    {
        if (root.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var value))
        {
            apply(value);
        }
    }

    private static void MergeInteger(
        JsonElement root,
        string propertyName,
        Action<int> apply)
        => MergePositiveInteger(root, propertyName, apply);

    private static void MergeDouble(
        JsonElement root,
        string propertyName,
        Action<double> apply)
    {
        if (root.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out var value))
        {
            apply(value);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return property.GetBoolean();
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var parsed) ||
            !double.IsFinite(parsed))
        {
            return null;
        }

        return parsed;
    }

    private static MavlinkConnectionOptions? ReadMavlinkOptions(
        JsonElement connection,
        ConnectionMode mode)
    {
        if (mode != ConnectionMode.Mavlink)
        {
            return null;
        }

        if (!connection.TryGetProperty("mavlink", out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return new MavlinkConnectionOptions();
        }

        var transport = ReadEnum(
            element,
            "transport",
            MavlinkTransportKind.UdpListener);
        var autopilot = ReadEnum(
            element,
            "autopilot",
            MavlinkAutopilotProfile.Px4);
        var sourceSystemId = ReadByte(element, "sourceSystemId") ?? 255;
        var sourceComponentId = ReadByte(element, "sourceComponentId") ?? 190;
        var baudRate = ReadPositiveInteger(element, "baudRate");
        var serialDeviceId = ReadString(element, "serialDeviceId");
        var lastKnownPort = ReadString(element, "lastKnownPort");
        var aliases = new List<MavlinkSystemAlias>();
        if (element.TryGetProperty("systemAliases", out var aliasesElement) &&
            aliasesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var aliasElement in aliasesElement.EnumerateArray())
            {
                var systemId = ReadByte(aliasElement, "systemId");
                var alias = ReadString(aliasElement, "name");
                if (systemId is not null && !string.IsNullOrWhiteSpace(alias))
                {
                    aliases.Add(new MavlinkSystemAlias(systemId.Value, alias));
                }
            }
        }

        return new MavlinkConnectionOptions(
            transport,
            autopilot,
            sourceSystemId,
            sourceComponentId,
            aliases,
            baudRate,
            serialDeviceId,
            lastKnownPort);
    }

    private static LinkdConnectionOptions? ReadLinkdOptions(
        JsonElement connection,
        ConnectionMode mode)
    {
        if (mode != ConnectionMode.FieldLink)
        {
            return null;
        }

        if (!connection.TryGetProperty("linkd", out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return new LinkdConnectionOptions();
        }

        var transportPlugin = ReadString(element, "transportPlugin") ?? "sik_serial";
        var baudRate = ReadPositiveInteger(element, "baudRate") ?? 57600;
        var serialDeviceId = ReadString(element, "serialDeviceId");
        var lastKnownPort = ReadString(element, "lastKnownPort");
        var radioProfileKey = ReadString(element, "radioProfileKey");
        var wireProfilePath = ReadString(element, "wireProfilePath");
        return new LinkdConnectionOptions(
            transportPlugin,
            baudRate,
            serialDeviceId,
            lastKnownPort,
            radioProfileKey,
            wireProfilePath);
    }

    private static byte? ReadByte(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetByte(out var value)
            ? value
            : null;

    private static int? ReadPositiveInteger(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt32(out var value) &&
           value > 0
            ? value
            : null;

    private static string? ResolveOptionalPath(string baseDirectory, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(baseDirectory, value));
    }

    private static string ResolveMapLibraryPath(string baseDirectory, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(baseDirectory, value));
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = baseDirectory;
        }

        return Path.GetFullPath(Path.Combine(localData, "Psycraft", "Robot Command", "Maps"));
    }


    private static string ResolveLocalVideoRecordingPath(string baseDirectory, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(baseDirectory, value));
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = baseDirectory;
        }

        return Path.GetFullPath(Path.Combine(localData, "Psycraft", "Robot Command", "Video"));
    }

    private static string ResolveRemoteVideoCachePath(string baseDirectory, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(baseDirectory, value));
        }

        return Path.GetFullPath(Path.Combine(
            ResolveLocalVideoRecordingPath(baseDirectory, null),
            "VehicleCache"));
    }

    private static string ResolveEvidenceLibraryPath(string baseDirectory, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(baseDirectory, value));
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = baseDirectory;
        }

        return Path.GetFullPath(Path.Combine(localData, "Psycraft", "Robot Command", "Evidence"));
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement element,
        string propertyName,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind == JsonValueKind.String &&
            Enum.TryParse<TEnum>(property.GetString(), ignoreCase: true, out var parsedName))
        {
            return parsedName;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var numericValue) &&
            Enum.IsDefined(typeof(TEnum), numericValue))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), numericValue);
        }

        return fallback;
    }

    private static string ResolveTerrainCachePath(string baseDirectory, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(Path.IsPathRooted(value)
                ? value
                : Path.Combine(baseDirectory, value));
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "data", "terrain", "elevation-cache.json"));
    }

    private static string NormalizeLanguage(string? language)
        => string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    private static RtspTransportMode ParseRtspTransport(
        string? value,
        RtspTransportMode fallback)
        => value?.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => RtspTransportMode.Automatic,
            "tcp" => RtspTransportMode.Tcp,
            "udp" => RtspTransportMode.Udp,
            "udp-multicast" or "udp_multicast" or "udp-mcast" => RtspTransportMode.UdpMulticast,
            _ => fallback
        };

    private sealed class MutableConfiguration
    {
        public bool BrightModeEnabled { get; set; }

        public string Language { get; set; } = "en";

        public double OperateInspectorWidth { get; set; } = 380;

        public DistanceUnit HorizontalDistanceUnit { get; set; } = DistanceUnit.Meters;

        public DistanceUnit VerticalDistanceUnit { get; set; } = DistanceUnit.Meters;

        public AreaUnit AreaUnit { get; set; } = AreaUnit.SquareMeters;

        public SpeedUnit SpeedUnit { get; set; } = SpeedUnit.MetersPerSecond;

        public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

        public List<ConnectionProfile> Connections { get; } = [];

        public int RefreshSeconds { get; set; } = 2;

        public int StaleAfterSeconds { get; set; } = 10;

        public int OfflineAfterSeconds { get; set; } = 30;

        public bool LiveStreamsEnabled { get; set; } = true;

        public int StreamHeartbeatSeconds { get; set; } = 2;

        public int StreamRetrySeconds { get; set; } = 2;

        public int PollFallbackSeconds { get; set; } = 15;

        public int MaxEvents { get; set; } = 500;

        public int MaxGeometryObjects { get; set; } = 500;

        public int MaxPerceptionTracks { get; set; } = 100;

        public double PerceptionMinConfidence { get; set; } = 0.25;

        public int MaxCameraSources { get; set; } = 32;

        public int MaxCommandHistory { get; set; } = 250;

        public int OperatorConfirmationTimeoutSeconds { get; set; } = 30;

        public bool RequireTypedOperatorConfirmation { get; set; } = true;

        public string? OperationalMapPath { get; set; }

        public string OperationalMapAttribution { get; set; } = "Offline operational map";

        public bool OnlineMapFallbackEnabled { get; set; } = true;

        public double MapDefaultLongitude { get; set; } = -79.42;

        public double MapDefaultLatitude { get; set; } = 43.73;

        public double MapDefaultResolution { get; set; } = 60;

        public string? MapLibraryPath { get; set; }

        public int MapTrailMaxAgeMinutes { get; set; } = 15;

        public int MapTrailMaxPointsPerVehicle { get; set; } = 600;

        public double MapTrailMinimumDistanceMetres { get; set; } = 2;

        public string? GStreamerBinPath { get; set; }

        public string? GStreamerPluginPath { get; set; }

        public string? GStreamerTestFilePath { get; set; }

        public int GStreamerTestWidth { get; set; } = 960;

        public int GStreamerTestHeight { get; set; } = 540;

        public int GStreamerTestFrameRate { get; set; } = 30;

        public string GStreamerTestPattern { get; set; } = "smpte";

        public RtspTransportMode GStreamerRtspTransport { get; set; } = RtspTransportMode.Tcp;

        public int GStreamerRtspLatencyMilliseconds { get; set; } = 100;

        public int GStreamerRtspReconnectAttempts { get; set; } = 3;

        public int GStreamerRtspReconnectDelayMilliseconds { get; set; } = 2000;

        public int GStreamerSrtLatencyMilliseconds { get; set; } = 125;

        public int GStreamerHlsTimeoutSeconds { get; set; } = 15;

        public int GStreamerWhepTimeoutSeconds { get; set; } = 15;

        public bool GStreamerWhepUseLinkHeaders { get; set; } = true;

        public bool LocalVideoRecordingEnabled { get; set; } = true;

        public string? LocalVideoRecordingPath { get; set; }

        public int LocalVideoRollingBufferMinutes { get; set; } = 10;

        public int LocalVideoSegmentSeconds { get; set; } = 10;

        public int LocalVideoMaximumStorageGigabytes { get; set; } = 20;

        public int LocalVideoEncodingBitrateKbps { get; set; } = 4000;

        public List<RemoteVideoRecordingProfile> RemoteVideoRecordingProfiles { get; } = [];

        public string? RemoteVideoCachePath { get; set; }

        public int RemoteVideoLookbackHours { get; set; } = 24;

        public int RemoteVideoRequestTimeoutSeconds { get; set; } = 20;

        public int RemoteVideoCacheMaximumGigabytes { get; set; } = 50;

        public int RemoteVideoMaximumDownloadGigabytes { get; set; } = 10;

        public string? EvidenceLibraryPath { get; set; }

        public string? BehaviourLibraryPath { get; set; }

        public int MaximumBehaviourPackageBytes { get; set; } = 50 * 1024 * 1024;

        public int MaximumBehaviourPackageFiles { get; set; } = 500;

        public string TerrainProviderId { get; set; } = "copernicus";

        public string TerrainEndpoint { get; set; } = CopernicusTerrainElevationProvider.DefaultEndpoint;

        public int TerrainTimeoutSeconds { get; set; } = 15;

        public int TerrainCacheMaximumAgeDays { get; set; } = 30;

        public int TerrainCacheMaximumEntries { get; set; } = 10_000;

        public int TerrainRequestsPerSecond { get; set; } = 5;

        public string? TerrainCachePath { get; set; }
    }
}
