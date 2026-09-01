using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Autonomy;
using RobotCommand.Services.Behaviours;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Evidence;
using RobotCommand.Services.Geometry;
using RobotCommand.Services.Location;
using RobotCommand.Services.ManualControl;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Media;
using RobotCommand.Services.Missions;
using RobotCommand.Services.Operations;
using RobotCommand.Services.Reconciliation;
using RobotCommand.Services.Serial;
using RobotCommand.Services.Simulation;
using RobotCommand.Services.Team;
using RobotCommand.Services.Terrain;
using RobotCommand.Services.Workflows;
using RobotCommand.State;

namespace RobotCommand.Bootstrap;

public enum RobotCommandRuntimeMode { Gui, Headless, Cli }

public static class RobotCommandRuntimeHost
{
    public static IHost Build(string baseDirectory, RobotCommandRuntimeMode mode, Action<HostApplicationBuilder>? configure = null)
    {
        CrashDiagnostics.Install();
        // A headless caller may deliberately choose a fresh --data-dir. Host
        // configuration requires the content root to exist before services can
        // create their normal data/configuration files beneath it.
        Directory.CreateDirectory(baseDirectory);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "RobotCommand",
            ContentRootPath = baseDirectory
        });
        // Both front ends write their own user-facing output. Retain structured
        // diagnostics in the file logger so CLI --json keeps stdout valid NDJSON.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new FileLoggerProvider());
        AddServices(builder.Services, baseDirectory, mode);
        configure?.Invoke(builder);
        return builder.Build();
    }

    public static void AddServices(IServiceCollection services, string baseDirectory, RobotCommandRuntimeMode mode)
    {
        services.AddSingleton(_ => AppConfiguration.Load(baseDirectory));
        services.AddSingleton<IConnectionPersistence>(_ => new ConnectionPersistence(baseDirectory));
        services.AddSingleton<IApplicationSettingsPersistence>(_ => new ApplicationSettingsPersistence(baseDirectory));
        services.AddSingleton<IApplicationSettingsService>(provider => new ApplicationSettingsService(
            provider.GetRequiredService<AppConfiguration>(), provider.GetRequiredService<IApplicationSettingsPersistence>()));
        services.AddSingleton<IMediaSettingsService>(provider => new MediaSettingsService(
            provider.GetRequiredService<AppConfiguration>(), baseDirectory));
        services.AddSingleton<IMediaMtxRuntime, MediaMtxRuntime>();
        services.AddSingleton<IUnitSettingsService, UnitSettingsService>();
        services.AddSingleton<IApplicationPreferencesWorkflow, ApplicationPreferencesWorkflow>();
        services.AddSingleton<UnitDefinitionService>(provider => new UnitDefinitionService(
            baseDirectory,
            provider.GetRequiredService<IEntityStore<string, VehicleRecord>>(),
            provider.GetRequiredService<IEntityStore<string, CameraSourceRecord>>()));
        services.AddSingleton<IUnitDefinitionService>(provider => provider.GetRequiredService<UnitDefinitionService>());
        services.AddSingleton<IUnitAssociationWorkflow>(provider => provider.GetRequiredService<UnitDefinitionService>());
        services.TryAddSingleton<IUiDispatcher, InlineUiDispatcher>();
        services.AddSingleton<IStorePublicationGate, StorePublicationGate>();

        AddState(services);
        AddConnections(services, baseDirectory, mode);
        AddTeam(services, baseDirectory, mode);
    }

    private static void AddTeam(IServiceCollection services, string baseDirectory, RobotCommandRuntimeMode mode)
    {
        services.AddSingleton<ITeamServerSettingsService>(_ => new TeamServerSettingsService(baseDirectory));
        services.AddSingleton<ITeamCertificateService, TeamCertificateService>();
        services.AddSingleton<ITeamPairingService, TeamPairingService>();
        services.AddSingleton<ITeamPassphraseService, TeamPassphraseService>();
        services.AddSingleton<TeamAccessCoordinator>();
        services.AddSingleton<ITeamAccessCoordinator>(provider => provider.GetRequiredService<TeamAccessCoordinator>());
        services.AddHostedService(provider => provider.GetRequiredService<TeamAccessCoordinator>());
        services.AddSingleton<ITeamServerWorkflow, TeamServerWorkflow>();
        services.AddSingleton<RobotCommandSnapshotProjector>();
        services.AddSingleton<IRobotCommandSnapshotProjector>(provider => provider.GetRequiredService<RobotCommandSnapshotProjector>());
        services.AddHostedService(provider => provider.GetRequiredService<RobotCommandSnapshotProjector>());
        services.AddSingleton<LanTeamServer>();
        services.AddSingleton<ILanTeamServer>(provider => provider.GetRequiredService<LanTeamServer>());
        services.AddHostedService(provider => provider.GetRequiredService<LanTeamServer>());
        services.AddSingleton<LanTeamDiscoveryService>();
        services.AddSingleton<ILanTeamDiscoveryService>(provider => provider.GetRequiredService<LanTeamDiscoveryService>());
        services.AddHostedService(provider => provider.GetRequiredService<LanTeamDiscoveryService>());
        services.AddSingleton<RobotCommandObserverService>();
        services.AddSingleton<IRobotCommandObserverService>(provider => provider.GetRequiredService<RobotCommandObserverService>());
        services.AddHostedService(provider => provider.GetRequiredService<RobotCommandObserverService>());
        services.AddSingleton<ITeamObserverClientWorkflow, TeamObserverClientWorkflow>();
    }

    private static void AddState(IServiceCollection services)
    {
        services.AddSingleton<IEntityStore<string, ConnectionRecord>>(_ => new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, RuntimeRecord>>(_ => new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, TeamRecord>>(_ => new EntityStore<string, TeamRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, VehicleRecord>>(_ => new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, VehicleTelemetryRecord>>(_ => new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, VehicleDiagnosticsSnapshot>>(_ => new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, LinkRecord>>(_ => new EntityStore<string, LinkRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, LiveStreamRecord>>(_ => new EntityStore<string, LiveStreamRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, GeometryOverlayRecord>>(_ => new EntityStore<string, GeometryOverlayRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, PerceptionTrackRecord>>(_ => new EntityStore<string, PerceptionTrackRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, CameraSourceRecord>>(_ => new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, MavlinkCameraDefinitionRecord>>(_ => new EntityStore<string, MavlinkCameraDefinitionRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, CameraStreamRecord>>(_ => new EntityStore<string, CameraStreamRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, MissionRecord>>(_ => new EntityStore<string, MissionRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, OperationalTaskRecord>>(_ => new EntityStore<string, OperationalTaskRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, ConsoleEventRecord>>(_ => new EntityStore<string, ConsoleEventRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, OperationalCommandRecord>>(_ => new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<ISelectionService, SelectionService>();
        services.AddSingleton<ITeamSelectionState, TeamSelectionState>();
        services.AddSingleton<SelectionScopeCoordinator>();
    }

    private static void AddConnections(IServiceCollection services, string baseDirectory, RobotCommandRuntimeMode mode)
    {
        services.AddSingleton<ISerialDeviceDiscovery, WindowsSerialDeviceDiscovery>();
        services.AddSingleton<ISerialPortLeaseManager, SerialPortLeaseManager>();
        services.AddSingleton<ISerialByteTransportFactory, SerialByteTransportFactory>();
        services.AddSingleton<ISikRadioConfigurationService, SikRadioConfigurationService>();
        services.AddSingleton<ISikRadioPairingService, SikRadioPairingService>();
        services.AddSingleton<ReviewedOperationWorkflow>();
        services.AddSingleton<IReviewedOperationWorkflow>(provider => provider.GetRequiredService<ReviewedOperationWorkflow>());
        services.AddSingleton<IMavlinkCodec, MavlinkSharpCodec>();
        services.AddSingleton<IFlightMissionCompiler, Px4FlightMissionCompiler>();
        services.AddSingleton<IFlightMissionCompiler, ArduPilotFlightMissionCompiler>();
        services.AddSingleton(provider => new FlightMissionLibraryStore(baseDirectory));
        services.AddSingleton(provider => new Px4FenceLibraryStore(baseDirectory));
        services.AddSingleton(provider => new FenceLibraryStore(baseDirectory));
        services.AddSingleton<IPx4ParameterFileCodec, Px4ParameterFileCodec>();
        services.AddSingleton<IPx4ParameterProfileStore>(provider => new Px4ParameterProfileStore(AppContext.BaseDirectory, provider.GetRequiredService<IPx4ParameterFileCodec>(), provider.GetRequiredService<ILogger<Px4ParameterProfileStore>>()));
        services.AddSingleton<IPx4ParameterService, Px4ParameterService>();
        services.AddSingleton<IPx4ParameterProfileWorkflow, Px4ParameterProfileWorkflow>();
        services.AddSingleton<ISikRadioWorkflow, SikRadioWorkflow>();
        services.AddSingleton<IMavlinkAutopilotAdapter, Px4MavlinkAutopilotAdapter>();
        services.AddSingleton<IMavlinkAutopilotAdapter, ArduPilotMavlinkAutopilotAdapter>();
        services.AddSingleton<IVehicleDiagnosticsProvider, Px4VehicleDiagnosticsProvider>();
        services.AddSingleton<IVehicleDiagnosticsProvider, ArduPilotVehicleDiagnosticsProvider>();
        services.AddSingleton<MavlinkConnectionRegistry>();
        services.AddSingleton<IMavlinkConnectionRegistry>(provider => provider.GetRequiredService<MavlinkConnectionRegistry>());
        services.AddSingleton<IMavlinkCameraControlService, MavlinkCameraControlService>();
        services.AddSingleton<IConnectionProvider, DirectLogosConnectionFactory>();
        services.AddSingleton<IConnectionProvider, LinkdConnectionProvider>();
        services.AddSingleton<IConnectionProvider, MavlinkConnectionProvider>();
        services.AddSingleton<ILogosConnectionFactory, ManagedConnectionFactory>();
        services.AddSingleton<ILogosOperationalSessionFactory, LogosOperationalSessionFactory>();
        services.AddSingleton<ILogosOperationalSessionRegistry, LogosOperationalSessionRegistry>();
        services.AddSingleton<ILogosConnectionManager>(provider => new OperationalLogosConnectionManager(ActivatorUtilities.CreateInstance<LogosConnectionManager>(provider), provider.GetRequiredService<ILogosOperationalSessionRegistry>(), provider.GetRequiredService<ILogger<OperationalLogosConnectionManager>>()));
        services.AddSingleton<IConnectionManagementWorkflow, ConnectionManagementWorkflow>();
        services.AddSingleton<IUnitObservationWorkflow, UnitObservationWorkflow>();
        services.AddSingleton<IConnectionRuntimeLifecycle, ConnectionRuntimeLifecycle>();
        services.AddSingleton<IOperationalMapSceneBuilder, OperationalMapSceneBuilder>();
        services.AddSingleton<IMapPackageValidator, MapPackageValidator>();
        services.AddSingleton<IMapPackageCatalog, MapPackageCatalog>();
        services.AddSingleton<IMapPackageInstaller, MapPackageInstaller>();
        services.AddSingleton<IMapDisplayPreferences, MapDisplayPreferences>();
        services.AddSingleton<IWeatherRadarSource, RainViewerWeatherRadarSource>();
        services.AddSingleton<IMapViewportState, MapViewportState>();
        services.AddSingleton<IMapPresentationState, MapPresentationState>();
        services.AddSingleton<ITerrainElevationProvider>(provider => new CopernicusTerrainElevationProvider(provider.GetRequiredService<AppConfiguration>().Terrain, provider.GetRequiredService<ILogger<CopernicusTerrainElevationProvider>>()));
        services.AddSingleton<ITerrainElevationService>(provider => new TerrainElevationService(provider.GetRequiredService<ITerrainElevationProvider>(), provider.GetRequiredService<AppConfiguration>().Terrain, baseDirectory, provider.GetRequiredService<ILogger<TerrainElevationService>>()));
        services.AddSingleton<WindowsOperatorLocationService>();
        services.AddSingleton<IOperatorLocationService>(provider => provider.GetRequiredService<WindowsOperatorLocationService>());
        if (mode == RobotCommandRuntimeMode.Gui)
            services.AddHostedService(provider => provider.GetRequiredService<WindowsOperatorLocationService>());
        services.AddSingleton<ISavedMapViewRepository, SavedMapViewRepository>();
        services.AddSingleton<IMapDeploymentBundleService, MapDeploymentBundleService>();
        services.AddSingleton<IOperationalMapEngine, OperationalMapEngine>();
        services.AddSingleton<ISelectionWorkflow, SelectionWorkflow>();
        services.AddSingleton<FormationAssignmentFreezeRegistry>();
        services.AddSingleton<ITeamWorkflow>(provider => new UnitTeamWorkflow(
            baseDirectory,
            provider.GetRequiredService<IUnitObservationWorkflow>(),
            provider.GetRequiredService<FormationAssignmentFreezeRegistry>()));
        services.AddSingleton<ITeamSelectionWorkflow, TeamSelectionWorkflow>();
        services.AddSingleton<IOperatorTargetScopeWorkflow, OperatorTargetScopeWorkflow>();
        services.AddSingleton<MapViewWorkflow>();
        services.AddSingleton<IMapViewWorkflow>(provider => provider.GetRequiredService<MapViewWorkflow>());
        services.AddSingleton<IMapLibraryWorkflow, MapLibraryWorkflow>();
        services.AddSingleton<IMapSceneObservationWorkflow, MapSceneObservationWorkflow>();
        services.AddSingleton<ThreeDWorldSceneCoordinator>();
        services.AddSingleton<IThreeDSceneWorkflow>(provider => provider.GetRequiredService<ThreeDWorldSceneCoordinator>());
        services.AddSingleton<IThreeDWorldSceneWorkflow>(provider => provider.GetRequiredService<ThreeDWorldSceneCoordinator>());
        services.AddSingleton<IVehicleTrackHistory, VehicleTrackHistoryService>();
        services.AddSingleton<IGStreamerRuntime, GStreamerRuntime>();
        services.AddSingleton<IGStreamerVideoPipeline, GStreamerVideoPipeline>();
        services.AddSingleton<IGStreamerVideoPipelineFactory, GStreamerVideoPipelineFactory>();
        services.AddSingleton<ILocalVideoRecordingCatalog, LocalVideoRecordingCatalog>();
        services.AddSingleton<ILocalVideoRecordingService, LocalVideoRecordingService>();
        services.AddSingleton<IRemoteVideoRecordingCatalog, MediaMtxRemoteVideoRecordingCatalog>();
        services.AddSingleton<IUnifiedVideoTimelineService, UnifiedVideoTimelineService>();
        services.AddSingleton<IEvidenceLibrary, EvidenceLibrary>();
        services.AddSingleton<IEvidenceWorkflow, EvidenceWorkflow>();
        services.AddSingleton<IMediaWorkflow, MediaWorkflow>();
        services.AddSingleton<ISourceImageCaptureGateway, UnavailableSourceImageCaptureGateway>();
        services.AddSingleton<IVideoProtocolPolicy, VideoProtocolPolicy>();
        services.AddSingleton<IVideoPlaybackAdapter, NativeVideoPlaybackAdapter>();
        services.AddSingleton<LogosOperatorCommandGateway>();
        services.AddSingleton<MavlinkOperatorCommandGateway>();
        services.AddSingleton<IFormationProvider, LineFormationProvider>();
        services.AddSingleton<IFormationProvider, CircleFormationProvider>();
        services.AddSingleton<IFormationProvider, RectangleFormationProvider>();
        services.AddSingleton<GhostUnitService>();
        services.AddSingleton<GhostProfileWorkflow>(_ => new GhostProfileWorkflow(baseDirectory));
        services.AddSingleton<IGhostProfileWorkflow>(provider => provider.GetRequiredService<GhostProfileWorkflow>());
        services.AddSingleton<IGhostProfileAssetWorkflow>(provider => new GhostProfileAssetWorkflow(provider.GetRequiredService<GhostProfileWorkflow>(), baseDirectory));
        services.AddSingleton<IGhostUnitService>(provider => provider.GetRequiredService<GhostUnitService>());
        services.AddSingleton<IGhostSimulationWorkerSupervisor, GhostSimulationWorkerSupervisor>();
        services.AddSingleton<IFormationLockExecutor, GhostFormationLockExecutor>();
        services.AddSingleton<IFormationLockExecutor, Px4FormationLockExecutor>();
        services.AddSingleton<IFormationLockExecutor, ArduPilotFormationLockExecutor>();
        services.AddSingleton<IFormationLockExecutor>(_ => new UnsupportedFormationLockExecutor("Logos", unit => unit.ProfileKey.Contains("logos", StringComparison.OrdinalIgnoreCase)));
        services.AddSingleton<FormationLockWorkflow>();
        services.AddSingleton<IFormationLockWorkflow>(provider => provider.GetRequiredService<FormationLockWorkflow>());
        services.AddSingleton<FormationAssignmentWorkflow>();
        services.AddSingleton<IFormationAssignmentWorkflow>(provider => provider.GetRequiredService<FormationAssignmentWorkflow>());
        services.AddSingleton<IFlightMissionExecutor, Px4MissionExecutor>();
        services.AddSingleton<IFlightMissionExecutor, GhostMissionExecutor>();
        services.AddSingleton<IFlightMissionExecutor, ArduPilotMissionExecutor>();
        services.AddSingleton<IFlightMissionExecutor, LogosMissionExecutor>();
        services.AddSingleton<IGhostUnitWorkflow, GhostUnitWorkflow>();
        services.AddSingleton<IOperatorCommandGateway, RoutedOperatorCommandGateway>();
        services.AddSingleton<IManualControlRegistry, ManualControlRegistry>();
        services.AddSingleton<IOperatorControlService, OperatorControlService>();
        services.AddSingleton<IOperatorCommandWorkflow, OperatorCommandWorkflow>();
        services.AddSingleton<WindowsGamepadInputDeviceProvider>();
        services.AddSingleton<WindowsRawJoystickInputDeviceProvider>();
        services.AddSingleton<IManualInputDeviceProvider, CompositeManualInputDeviceProvider>();
        services.AddSingleton<ManualControlProfileStore>();
        services.AddSingleton<ManualControlService>();
        services.AddSingleton<IManualControlService>(provider => provider.GetRequiredService<ManualControlService>());
        services.AddSingleton<IManualControlWorkflow, ManualControlWorkflow>();
        services.AddSingleton<IMissionTaskDocumentService, MissionTaskDocumentService>();
        services.AddSingleton<ILogosCommandMetadataFactory, LogosCommandMetadataFactory>();
        services.AddSingleton<GeometryDocumentCodec>();
        services.AddSingleton<GeometryDocumentValidator>();
        services.AddSingleton<IGeometryDocumentStore>(provider => new GeometryDocumentStore(provider.GetRequiredService<GeometryDocumentCodec>(), provider.GetRequiredService<GeometryDocumentValidator>(), provider.GetRequiredService<ILogger<GeometryDocumentStore>>()));
        services.AddSingleton<IGeometryGroupStore, GeometryGroupStore>();
        services.AddSingleton<IGeometrySelectionWorkflow, GeometrySelectionWorkflow>();
        services.AddSingleton<GeometryProtoMapper>();
        services.AddSingleton<IGeometryGateway, LogosGeometryGateway>();
        services.AddSingleton<GeometryWorkspaceService>();
        services.AddSingleton<IGeometryWorkspaceService>(provider => provider.GetRequiredService<GeometryWorkspaceService>());
        services.AddSingleton<IGeometryRegistryStateSink>(provider => provider.GetRequiredService<GeometryWorkspaceService>());
        services.AddSingleton<IGeometryEditSession, GeometryEditSession>();
        services.AddSingleton<IBehaviourPackageValidator, BehaviourPackageValidator>();
        services.AddSingleton<IBehaviourPackageStore, BehaviourPackageStore>();
        services.AddSingleton<IMissionTaskGateway, LogosMissionTaskGateway>();
        services.AddSingleton<IMissionTaskWorkspaceService, MissionTaskWorkspaceService>();
        services.AddSingleton<LogosBehaviourPackageCatalog>();
        services.AddSingleton<IBehaviourPackageCatalog>(provider => provider.GetRequiredService<LogosBehaviourPackageCatalog>());
        services.AddSingleton<IBehaviourRemotePackageSource, LogosBehaviourRemotePackageSource>();
        services.AddSingleton<IBehaviourWorkspaceService, BehaviourWorkspaceService>();
        services.AddSingleton<BehaviourPackageProtoMapper>();
        services.AddSingleton<IBehaviourPackageGateway, LogosBehaviourPackageGateway>();
        services.AddSingleton<IBehaviourPackageDeploymentService, BehaviourPackageDeploymentService>();
        services.AddSingleton<IBehaviourGeometryBindingService, LogosBehaviourGeometryBindingService>();
        services.AddSingleton<IBehaviourBindingWorkspaceService, BehaviourBindingWorkspaceService>();
        services.AddSingleton<IAutonomyDeploymentBundleService, AutonomyDeploymentBundleService>();
        services.AddSingleton<IAutonomyWorkflow, AutonomyWorkflow>();
        services.AddSingleton<IBehaviourWorkflow, BehaviourWorkflow>();
        services.AddSingleton<IGeometryWorkflow, GeometryWorkflow>();
        services.AddSingleton(provider => new FormationLibraryStore(baseDirectory));
        services.AddSingleton<IFormationAuthoringWorkflow, FormationAuthoringWorkflow>();
        services.AddSingleton<IFlightMissionWorkflow, FlightMissionWorkflow>();
        services.AddSingleton<FenceWorkflow>();
        services.AddSingleton<IFenceWorkflow>(provider => provider.GetRequiredService<FenceWorkflow>());
        services.AddSingleton<IPx4GeofenceWorkflow>(provider => provider.GetRequiredService<FenceWorkflow>());
        services.AddSingleton<IOperationalInspectionTargetService, OperationalInspectionTargetService>();
        services.AddSingleton<IOperationalExecutionTargetResolver, OperationalExecutionTargetResolver>();
        services.AddSingleton<IOperationalRunService, OperationalRunService>();
        services.AddSingleton<IOperationalSupervisionService, LogosOperationalSupervisionService>();
        services.AddSingleton<IOperationalInterventionService, LogosOperationalInterventionService>();
        services.AddSingleton<ILinkdServiceController, WindowsLinkdServiceController>();
        services.AddHostedService<LinkdServiceStartupService>();
        if (mode is RobotCommandRuntimeMode.Gui or RobotCommandRuntimeMode.Headless)
            // CLI sessions own connection supervision through
            // IConnectionRuntimeLifecycle so --no-auto-connect is meaningful and
            // one-shot/live CLI scopes do not get a second supervisor. GUI and the
            // headless Team server retain the hosted supervisor.
            if (mode != RobotCommandRuntimeMode.Cli)
                services.AddHostedService<ConnectionSupervisorService>();
        services.AddHostedService(provider => (GhostUnitService)provider.GetRequiredService<IGhostUnitService>());
        services.AddHostedService(provider => provider.GetRequiredService<FormationLockWorkflow>());
        services.AddHostedService(provider => provider.GetRequiredService<WindowsGamepadInputDeviceProvider>());
        services.AddHostedService(provider => provider.GetRequiredService<WindowsRawJoystickInputDeviceProvider>());
        services.AddHostedService(provider => provider.GetRequiredService<ManualControlService>());
        // Native Robot Command geometry is deliberately local-only in the
        // developer preview. Do not start the legacy Logos registry watcher:
        // it would both surface remote geometry in the local authoring layer
        // and make an unavailable Logos Geometry API a background concern for
        // an otherwise independent map workspace.
        services.AddHostedService<MissionTaskEventProjectionService>();
        if (mode is RobotCommandRuntimeMode.Headless or RobotCommandRuntimeMode.Cli)
            services.AddHostedService<HeadlessMapPresentationService>();
    }
}
