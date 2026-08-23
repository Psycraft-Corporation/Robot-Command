using System.Text.Json;
using RobotCommand.Models;
using RobotCommand.Services;
using Xunit;

namespace RobotCommand.Tests;

public sealed class AppConfigurationTests
{
    [Fact]
    public void Load_ReadsMavlinkConnectionOptionsAndAliases()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "appsettings.json"),
            """
            {
              "connections": [
                {
                  "id": "px4-sitl",
                  "name": "PX4 SITL",
                  "target": "udp-listen://0.0.0.0:14550",
                  "mode": "Mavlink",
                  "autoConnect": true,
                  "mavlink": {
                    "transport": "UdpListener",
                    "autopilot": "Px4",
                    "sourceSystemId": 255,
                    "sourceComponentId": 190,
                    "systemAliases": [
                      { "systemId": 1, "name": "Dracula PX4" }
                    ]
                  }
                }
              ]
            }
            """);

        var config = AppConfiguration.Load(directory);

        var profile = Assert.Single(config.Connections);
        Assert.Equal(ConnectionMode.Mavlink, profile.Mode);
        Assert.True(profile.AutoConnect);
        Assert.NotNull(profile.Mavlink);
        Assert.Equal(MavlinkAutopilotProfile.Px4, profile.Mavlink.Autopilot);
        Assert.Equal("Dracula PX4", profile.Mavlink.DisplayNameFor(1));
        Assert.Equal("PX4 System 2", profile.Mavlink.DisplayNameFor(2));
    }

    [Fact]
    public void Load_ReadsNumericConnectionAndMavlinkEnumValuesWrittenByJsonSerializer()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(
            Path.Combine(directory, "appsettings.json"),
            """
            {
              "connections": [
                {
                  "id": "px4-sitl",
                  "name": "PX4 SITL",
                  "target": "udp-listen://0.0.0.0:14550",
                  "mode": 3,
                  "mavlink": {
                    "transport": 0,
                    "autopilot": 0
                  }
                }
              ]
            }
            """);

        var config = AppConfiguration.Load(directory);

        var profile = Assert.Single(config.Connections);
        Assert.Equal(ConnectionMode.Mavlink, profile.Mode);
        Assert.NotNull(profile.Mavlink);
        Assert.Equal(MavlinkTransportKind.UdpListener, profile.Mavlink.Transport);
        Assert.Equal(MavlinkAutopilotProfile.Px4, profile.Mavlink.Autopilot);
    }

    [Fact]
    public void Load_ReadsSerialSikConnectionIdentityAndBaudRate()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"),
            """
            {
              "connections": [{
                "id": "px4-sik",
                "name": "PX4 SiK",
                "target": "serial://COM3",
                "mode": "Mavlink",
                "mavlink": {
                  "transport": "Serial",
                  "autopilot": "Px4",
                  "baudRate": 57600,
                  "serialDeviceId": "FTDIBUS\\VID_0403+PID_6015+DU0D8IQQA\\0000",
                  "lastKnownPort": "COM3"
                }
              }]
            }
            """);

        var profile = Assert.Single(AppConfiguration.Load(directory).Connections);

        Assert.Equal(MavlinkTransportKind.Serial, profile.Mavlink!.Transport);
        Assert.Equal(57600, profile.Mavlink.EffectiveBaudRate);
        Assert.Equal("COM3", profile.Mavlink.LastKnownPort);
        Assert.Contains("DU0D8IQQ", profile.Mavlink.SerialDeviceId);
    }

    [Fact]
    public void Load_ReadsLinkdRadioSelectionAndBaudRate()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"),
            """
            {
              "connections": [{
                "id": "linkd",
                "name": "Logos Radio",
                "target": "http://127.0.0.1:9467",
                "mode": "FieldLink",
                "linkd": {
                  "transportPlugin": "sik_serial",
                  "baudRate": 57600,
                  "serialDeviceId": "FTDIBUS\\VID_0403+PID_6015+DU0D8IQQA\\0000",
                  "lastKnownPort": "COM3",
                  "radioProfileKey": "3dr_915_dracula_v1"
                }
              }]
            }
            """);

        var profile = Assert.Single(AppConfiguration.Load(directory).Connections);

        Assert.Equal(ConnectionMode.FieldLink, profile.Mode);
        Assert.NotNull(profile.Linkd);
        Assert.Equal("sik_serial", profile.Linkd.TransportPlugin);
        Assert.Equal(57600, profile.Linkd.EffectiveBaudRate);
        Assert.Equal("COM3", profile.Linkd.LastKnownPort);
        Assert.True(profile.Linkd.HasSelectedDevice);
    }

    [Fact]
    public void Load_UsesDefaults_WhenNoFilesExist()
    {
        var directory = CreateTempDirectory();
        var config = AppConfiguration.Load(directory);

        Assert.NotEmpty(config.Profiles);
        Assert.Equal("http://localhost:50051", config.Profiles[0].Target);
        Assert.Equal(2, config.RefreshSeconds);
        Assert.True(config.StaleAfterSeconds > config.RefreshSeconds);
        Assert.True(config.OfflineAfterSeconds > config.StaleAfterSeconds);
        Assert.True(config.LiveStreamsEnabled);
        Assert.Equal(2, config.StreamHeartbeatSeconds);
        Assert.Equal(2, config.StreamRetrySeconds);
        Assert.Equal(15, config.PollFallbackSeconds);
        Assert.Equal(500, config.MaxEvents);
        Assert.Equal(500, config.MaxGeometryObjects);
        Assert.Equal(100, config.MaxPerceptionTracks);
        Assert.Equal(0.25, config.PerceptionMinConfidence);
        Assert.Equal(32, config.MaxCameraSources);
        Assert.Equal(250, config.MaxCommandHistory);
        Assert.Equal(30, config.OperatorConfirmationTimeoutSeconds);
        Assert.True(config.RequireTypedOperatorConfirmation);
        Assert.False(config.Ui.BrightModeEnabled);
        Assert.Equal(380, config.Ui.OperateInspectorWidth);
        Assert.Null(config.OperationalMapPath);
        Assert.Equal("Offline operational map", config.OperationalMapAttribution);
        Assert.True(config.OnlineMapFallbackEnabled);
        Assert.Equal(-79.42, config.MapDefaultLongitude);
        Assert.Equal(43.73, config.MapDefaultLatitude);
        Assert.Equal(60, config.MapDefaultResolution);
        Assert.Contains(Path.Combine("Psycraft", "Robot Command", "Maps"), config.MapLibraryPath);
        Assert.Equal(15, config.MapTrailMaxAgeMinutes);
        Assert.Equal(600, config.MapTrailMaxPointsPerVehicle);
        Assert.Equal(2, config.MapTrailMinimumDistanceMetres);
        Assert.Null(config.GStreamerBinPath);
        Assert.Null(config.GStreamerPluginPath);
        Assert.Null(config.GStreamerTestFilePath);
        Assert.Equal(960, config.GStreamerTestWidth);
        Assert.Equal(540, config.GStreamerTestHeight);
        Assert.Equal(30, config.GStreamerTestFrameRate);
        Assert.Equal("smpte", config.GStreamerTestPattern);
        Assert.Equal(RtspTransportMode.Tcp, config.GStreamerRtspTransport);
        Assert.Equal(100, config.GStreamerRtspLatencyMilliseconds);
        Assert.Equal(3, config.GStreamerRtspReconnectAttempts);
        Assert.Equal(2000, config.GStreamerRtspReconnectDelayMilliseconds);
        Assert.Equal(125, config.GStreamerSrtLatencyMilliseconds);
        Assert.Equal(15, config.GStreamerHlsTimeoutSeconds);
        Assert.Equal(15, config.GStreamerWhepTimeoutSeconds);
        Assert.True(config.GStreamerWhepUseLinkHeaders);
        Assert.True(config.LocalVideoRecordingEnabled);
        Assert.Contains(Path.Combine("Psycraft", "Robot Command", "Video"), config.LocalVideoRecordingPath);
        Assert.Equal(10, config.LocalVideoRollingBufferMinutes);
        Assert.Equal(10, config.LocalVideoSegmentSeconds);
        Assert.Equal(20, config.LocalVideoMaximumStorageGigabytes);
        Assert.Equal(4000, config.LocalVideoEncodingBitrateKbps);
        Assert.Empty(config.RemoteVideoRecordingProfiles);
        Assert.Contains(Path.Combine("Psycraft", "Robot Command", "Video", "VehicleCache"), config.RemoteVideoCachePath);
        Assert.Equal(24, config.RemoteVideoLookbackHours);
        Assert.Equal(20, config.RemoteVideoRequestTimeoutSeconds);
        Assert.Equal(50, config.RemoteVideoCacheMaximumGigabytes);
        Assert.Equal(10, config.RemoteVideoMaximumDownloadGigabytes);
        Assert.Contains(Path.Combine("Psycraft", "Robot Command", "Evidence"), config.EvidenceLibraryPath);
        Assert.Equal("copernicus", config.Terrain.ProviderId);
        Assert.Equal(15, config.Terrain.TimeoutSeconds);
        Assert.Equal(30, config.Terrain.CacheMaximumAgeDays);
        Assert.Equal(10_000, config.Terrain.CacheMaximumEntries);
        Assert.Equal(5, config.Terrain.RequestsPerSecond);
        Assert.Contains(Path.Combine("data", "terrain", "elevation-cache.json"), config.Terrain.CachePath);
    }

    [Fact]
    public void Load_ReadsTerrainProviderOptions()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"),
            """
            {
              "terrain": {
                "provider": "copernicus",
                "endpoint": "https://terrain.example",
                "timeoutSeconds": 22,
                "cacheMaximumAgeDays": 14,
                "cacheMaximumEntries": 250,
                "requestsPerSecond": 3,
                "cachePath": "terrain-cache/elevation.json"
              }
            }
            """);

        var config = AppConfiguration.Load(directory);

        Assert.Equal("copernicus", config.Terrain.ProviderId);
        Assert.Equal("https://terrain.example", config.Terrain.Endpoint);
        Assert.Equal(22, config.Terrain.TimeoutSeconds);
        Assert.Equal(14, config.Terrain.CacheMaximumAgeDays);
        Assert.Equal(250, config.Terrain.CacheMaximumEntries);
        Assert.Equal(3, config.Terrain.RequestsPerSecond);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(directory, "terrain-cache", "elevation.json")),
            config.Terrain.CachePath);
    }

    [Fact]
    public void Load_MergesLocalConnectionSettingsOverBaseConfig()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), """
        {
          "profiles": [
            { "name": "Base", "target": "http://localhost:50051" }
          ],
          "refreshSeconds": 2,
          "staleAfterSeconds": 10,
          "offlineAfterSeconds": 30
        }
        """);
        File.WriteAllText(Path.Combine(directory, "appsettings.local.json"), """
        {
          "profiles": [
            {
              "id": "dracula",
              "name": "Local",
              "target": "http://localhost:50051",
              "mode": "direct",
              "autoConnect": true,
              "autoReconnect": false
            }
          ],
          "refreshSeconds": 5,
          "staleAfterSeconds": 20,
          "offlineAfterSeconds": 60,
          "liveStreamsEnabled": false,
          "streamHeartbeatSeconds": 4,
          "streamRetrySeconds": 6,
          "pollFallbackSeconds": 25,
          "maxEvents": 750,
          "maxGeometryObjects": 900,
          "maxPerceptionTracks": 80,
          "perceptionMinConfidence": 0.6,
          "maxCameraSources": 12,
          "maxCommandHistory": 75,
          "operatorConfirmationTimeoutSeconds": 45,
          "requireTypedOperatorConfirmation": false,
            "maps": {
            "operationalMbTilesPath": "maps/toronto.mbtiles",
            "libraryPath": "maps/library",
            "attribution": "Psycraft test map",
            "onlineFallbackEnabled": false,
            "defaultLongitude": -79.4,
            "defaultLatitude": 43.7,
            "defaultResolution": 75,
            "trailMaxAgeMinutes": 45,
            "trailMaxPointsPerVehicle": 1200,
            "trailMinimumDistanceMetres": 3.5
          },
          "video": {
            "gstreamerBinPath": "native/gstreamer/bin",
            "gstreamerPluginPath": "native/gstreamer/plugins",
            "testFilePath": "media/test.mp4",
            "testPattern": "ball",
            "testWidth": 1280,
            "testHeight": 720,
            "testFrameRate": 24,
            "rtspTransport": "udp-multicast",
            "rtspLatencyMilliseconds": 350,
            "rtspReconnectAttempts": 5,
            "rtspReconnectDelayMilliseconds": 750,
            "srtLatencyMilliseconds": 275,
            "hlsTimeoutSeconds": 45,
            "whepTimeoutSeconds": 30,
            "whepUseLinkHeaders": false,
            "localRecordingEnabled": true,
            "localRecordingPath": "media/rolling",
            "rollingBufferMinutes": 30,
            "segmentSeconds": 15,
            "maximumStorageGigabytes": 50,
            "encodingBitrateKbps": 6500,
            "remoteRecordingCachePath": "media/vehicle-cache",
            "remoteRecordingLookbackHours": 72,
            "remoteRecordingRequestTimeoutSeconds": 35,
            "remoteRecordingCacheMaximumGigabytes": 80,
            "remoteRecordingMaximumDownloadGigabytes": 12,
            "evidenceLibraryPath": "media/evidence",
            "remoteRecordingProfiles": [
              {
                "name": "Dracula MediaMTX",
                "connectionId": "dracula",
                "playbackBaseUrl": "http://localhost:9996",
                "pathTemplate": "camera/{cameraSourceId}",
                "bearerTokenEnvironmentVariable": "MEDIAMTX_TOKEN",
                "enabled": true
              }
            ]
          }
        }
        """);

        var config = AppConfiguration.Load(directory);

        var profile = Assert.Single(config.Profiles);
        Assert.Equal("dracula", profile.Id);
        Assert.Equal("Local", profile.Name);
        Assert.Equal("http://localhost:50051", profile.Target);
        Assert.Equal(ConnectionMode.Direct, profile.Mode);
        Assert.True(profile.AutoConnect);
        Assert.False(profile.AutoReconnect);
        Assert.Equal(5, config.RefreshSeconds);
        Assert.Equal(20, config.StaleAfterSeconds);
        Assert.Equal(60, config.OfflineAfterSeconds);
        Assert.False(config.LiveStreamsEnabled);
        Assert.Equal(4, config.StreamHeartbeatSeconds);
        Assert.Equal(6, config.StreamRetrySeconds);
        Assert.Equal(25, config.PollFallbackSeconds);
        Assert.Equal(750, config.MaxEvents);
        Assert.Equal(900, config.MaxGeometryObjects);
        Assert.Equal(80, config.MaxPerceptionTracks);
        Assert.Equal(0.6, config.PerceptionMinConfidence);
        Assert.Equal(12, config.MaxCameraSources);
        Assert.Equal(75, config.MaxCommandHistory);
        Assert.Equal(45, config.OperatorConfirmationTimeoutSeconds);
        Assert.False(config.RequireTypedOperatorConfirmation);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "maps/toronto.mbtiles")), config.OperationalMapPath);
        Assert.Equal("Psycraft test map", config.OperationalMapAttribution);
        Assert.False(config.OnlineMapFallbackEnabled);
        Assert.Equal(-79.4, config.MapDefaultLongitude);
        Assert.Equal(43.7, config.MapDefaultLatitude);
        Assert.Equal(75, config.MapDefaultResolution);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "maps/library")), config.MapLibraryPath);
        Assert.Equal(45, config.MapTrailMaxAgeMinutes);
        Assert.Equal(1200, config.MapTrailMaxPointsPerVehicle);
        Assert.Equal(3.5, config.MapTrailMinimumDistanceMetres);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "native/gstreamer/bin")), config.GStreamerBinPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "native/gstreamer/plugins")), config.GStreamerPluginPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "media/test.mp4")), config.GStreamerTestFilePath);
        Assert.Equal(1280, config.GStreamerTestWidth);
        Assert.Equal(720, config.GStreamerTestHeight);
        Assert.Equal(24, config.GStreamerTestFrameRate);
        Assert.Equal("ball", config.GStreamerTestPattern);
        Assert.Equal(RtspTransportMode.UdpMulticast, config.GStreamerRtspTransport);
        Assert.Equal(350, config.GStreamerRtspLatencyMilliseconds);
        Assert.Equal(5, config.GStreamerRtspReconnectAttempts);
        Assert.Equal(750, config.GStreamerRtspReconnectDelayMilliseconds);
        Assert.Equal(275, config.GStreamerSrtLatencyMilliseconds);
        Assert.Equal(45, config.GStreamerHlsTimeoutSeconds);
        Assert.Equal(30, config.GStreamerWhepTimeoutSeconds);
        Assert.False(config.GStreamerWhepUseLinkHeaders);
        Assert.True(config.LocalVideoRecordingEnabled);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "media/rolling")), config.LocalVideoRecordingPath);
        Assert.Equal(30, config.LocalVideoRollingBufferMinutes);
        Assert.Equal(15, config.LocalVideoSegmentSeconds);
        Assert.Equal(50, config.LocalVideoMaximumStorageGigabytes);
        Assert.Equal(6500, config.LocalVideoEncodingBitrateKbps);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "media/vehicle-cache")), config.RemoteVideoCachePath);
        Assert.Equal(72, config.RemoteVideoLookbackHours);
        Assert.Equal(35, config.RemoteVideoRequestTimeoutSeconds);
        Assert.Equal(80, config.RemoteVideoCacheMaximumGigabytes);
        Assert.Equal(12, config.RemoteVideoMaximumDownloadGigabytes);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "media/evidence")), config.EvidenceLibraryPath);
        var media = Assert.Single(config.RemoteVideoRecordingProfiles);
        Assert.Equal("Dracula MediaMTX", media.Name);
        Assert.Equal("dracula", media.ConnectionId);
        Assert.Equal("http://localhost:9996", media.PlaybackBaseUrl);
        Assert.Equal("camera/{cameraSourceId}", media.PathTemplate);
        Assert.Equal("MEDIAMTX_TOKEN", media.BearerTokenEnvironmentVariable);
    }

    [Fact]
    public void Load_FallsBackWhenConfiguredProfilesAreInvalid()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), """
        {
          "profiles": [
            { "name": "Invalid", "target": "not-a-url" }
          ]
        }
        """);

        var config = AppConfiguration.Load(directory);

        var profile = Assert.Single(config.Profiles);
        Assert.Equal("Local Logos", profile.Name);
    }

    [Fact]
    public void Load_ReadsConnectionsAndKeepsLegacyProfilesReadable()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), """
        {
          "connections": [
            { "id": "one", "name": "One", "target": "http://one:9000" }
          ]
        }
        """);

        var config = AppConfiguration.Load(directory);

        Assert.Equal("one", Assert.Single(config.Connections).Id);
        Assert.Equal("one", Assert.Single(config.Profiles).Id);
    }

    [Fact]
    public async Task ConnectionPersistence_WritesConnectionsAndRemovesLegacyProfiles()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.local.json"), """
        { "profiles": [{ "name": "Old", "target": "http://old:9000" }], "refreshSeconds": 5 }
        """);

        var persistence = new ConnectionPersistence(directory);
        await persistence.SaveAsync([new ConnectionProfile("New", "http://new:9000", Id: "new")]);
        var text = await File.ReadAllTextAsync(Path.Combine(directory, "appsettings.local.json"));

        Assert.Contains("\"connections\"", text);
        Assert.Contains("http://new:9000", text);
        Assert.DoesNotContain("\"profiles\"", text);
        Assert.Contains("\"refreshSeconds\": 5", text);
    }

    [Fact]
    public void Load_ReadsBrightModeSettingFromUi()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.local.json"),
            "{ \"ui\": { \"brightMode\": true } }");

        var config = AppConfiguration.Load(directory);

        Assert.True(config.Ui.BrightModeEnabled);
    }

    [Fact]
    public void Load_MigratesLegacyHighContrastSettingToBrightMode()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.local.json"),
            "{ \"ui\": { \"highContrast\": true } }");

        Assert.True(AppConfiguration.Load(directory).Ui.BrightModeEnabled);
    }

    [Fact]
    public void Load_InvalidBrightModeSettingDefaultsToOff()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.local.json"),
            "{ \"ui\": { \"brightMode\": \"yes\" } }");

        var config = AppConfiguration.Load(directory);

        Assert.False(config.Ui.BrightModeEnabled);
    }

    [Fact]
    public void Load_ReadsSupportedLanguageAndFallsBackForUnknownLanguage()
    {
        var frenchDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(frenchDirectory, "appsettings.local.json"),
            "{ \"ui\": { \"language\": \"fr\" } }");

        Assert.Equal("fr", AppConfiguration.Load(frenchDirectory).Ui.Language);

        var unknownDirectory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(unknownDirectory, "appsettings.local.json"),
            "{ \"ui\": { \"language\": \"de\" } }");

        Assert.Equal("en", AppConfiguration.Load(unknownDirectory).Ui.Language);
    }

    [Fact]
    public async Task ConnectionPersistence_RoundTripsSerialMavlinkIdentityAndBaudRate()
    {
        var directory = CreateTempDirectory();
        var persistence = new ConnectionPersistence(directory);
        var profile = new ConnectionProfile(
            "PX4 SiK",
            "serial://COM3",
            Id: "px4-sik",
            Mode: ConnectionMode.Mavlink,
            Mavlink: new MavlinkConnectionOptions(
                MavlinkTransportKind.Serial,
                MavlinkAutopilotProfile.Px4,
                BaudRate: 57600,
                SerialDeviceId: @"FTDIBUS\VID_0403+PID_6015+DU0D8IQQA\0000",
                LastKnownPort: "COM3"));

        await persistence.SaveAsync([profile], TestContext.Current.CancellationToken);
        var loaded = AppConfiguration.Load(directory).Connections.Single();

        Assert.Equal(MavlinkTransportKind.Serial, loaded.Mavlink!.Transport);
        Assert.Equal(57600, loaded.Mavlink.EffectiveBaudRate);
        Assert.Equal("COM3", loaded.Mavlink.LastKnownPort);
        Assert.Contains("DU0D8IQQ", loaded.Mavlink.SerialDeviceId);
    }

    [Fact]
    public async Task ConnectionPersistence_RoundTripsLinkdRadioSelection()
    {
        var directory = CreateTempDirectory();
        var persistence = new ConnectionPersistence(directory);
        var profile = new ConnectionProfile(
            "Logos Radio",
            "http://127.0.0.1:9467",
            Id: "linkd",
            Mode: ConnectionMode.FieldLink,
            Linkd: new LinkdConnectionOptions(
                BaudRate: 57600,
                SerialDeviceId: "radio-1",
                LastKnownPort: "COM3"));

        await persistence.SaveAsync([profile], TestContext.Current.CancellationToken);
        var loaded = AppConfiguration.Load(directory).Connections.Single();

        Assert.Equal(ConnectionMode.FieldLink, loaded.Mode);
        Assert.Equal("radio-1", loaded.Linkd!.SerialDeviceId);
        Assert.Equal("COM3", loaded.Linkd.LastKnownPort);
        Assert.Equal(57600, loaded.Linkd.EffectiveBaudRate);
    }

    [Fact]
    public async Task ApplicationSettingsPersistence_PreservesExistingConfiguration()
    {
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.local.json"),
            "{ \"connections\": [{ \"name\": \"PX4\", \"target\": \"serial://COM3\" }], \"refreshSeconds\": 5 }");

        var persistence = new ApplicationSettingsPersistence(directory);
        await persistence.SaveAsync(new AppUiSettings(true), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "appsettings.local.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal("serial://COM3", document.RootElement.GetProperty("connections")[0].GetProperty("target").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("refreshSeconds").GetInt32());
        Assert.True(document.RootElement.GetProperty("ui").GetProperty("brightMode").GetBoolean());
    }

    [Fact]
    public async Task ApplicationSettingsService_NotifiesAndRoundTripsBrightMode()
    {
        var directory = CreateTempDirectory();
        var service = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SetBrightModeEnabledAsync(true, TestContext.Current.CancellationToken);

        Assert.True(service.Current.BrightModeEnabled);
        Assert.Equal(1, changed);
        Assert.True(AppConfiguration.Load(directory).Ui.BrightModeEnabled);
    }

    [Fact]
    public async Task ApplicationSettingsService_NotifiesAndRoundTripsLanguage()
    {
        var directory = CreateTempDirectory();
        var service = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SetLanguageAsync("fr", TestContext.Current.CancellationToken);

        Assert.Equal("fr", service.Current.Language);
        Assert.Equal(1, changed);
        Assert.Equal("fr", AppConfiguration.Load(directory).Ui.Language);
    }

    [Fact]
    public async Task ApplicationSettingsService_PersistsBoundedOperateInspectorWidth()
    {
        var directory = CreateTempDirectory();
        var service = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));

        await service.SetOperateInspectorWidthAsync(512, TestContext.Current.CancellationToken);

        Assert.Equal(460, service.Current.OperateInspectorWidth);
        Assert.Equal(460, AppConfiguration.Load(directory).Ui.OperateInspectorWidth);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
