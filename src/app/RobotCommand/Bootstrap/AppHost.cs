using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
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
using RobotCommand.ViewModels;
using RobotCommand.Views;
using RobotCommand.Views.Workspaces;

namespace RobotCommand.Bootstrap;

internal static class AppHost
{
    public static IHost Build(string baseDirectory)
    {
        return RobotCommandRuntimeHost.Build(baseDirectory, RobotCommandRuntimeMode.Gui, builder =>
        {
            builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
            builder.Services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
            RegisterViewModels(builder.Services);
            builder.Services.AddSingleton<MainWindow>(services =>
            {
                _ = services.GetRequiredService<ILocalizationService>();
                return new MainWindow(
                    services.GetRequiredService<IApplicationSettingsService>(),
                    services.GetRequiredService<ILocalizationService>())
                {
                    DataContext = services.GetRequiredService<ShellViewModel>()
                };
            });
        });
    }

    private static void RegisterTeamServer(IServiceCollection services, string baseDirectory)
    {
        services.AddSingleton<ITeamServerSettingsService>(_ => new TeamServerSettingsService(baseDirectory));
        services.AddSingleton<ITeamCertificateService, TeamCertificateService>();
        services.AddSingleton<ITeamPairingService, TeamPairingService>();
        services.AddSingleton<ITeamPassphraseService, TeamPassphraseService>();
        services.AddSingleton<TeamAccessCoordinator>();
        services.AddSingleton<ITeamAccessCoordinator>(provider => provider.GetRequiredService<TeamAccessCoordinator>());
        services.AddHostedService(provider => provider.GetRequiredService<TeamAccessCoordinator>());
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
    }

    private static void RegisterState(IServiceCollection services)
    {
        services.AddSingleton<IEntityStore<string, ConnectionRecord>>(
            _ => new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, RuntimeRecord>>(
            _ => new EntityStore<string, RuntimeRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, TeamRecord>>(
            _ => new EntityStore<string, TeamRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, VehicleRecord>>(
            _ => new EntityStore<string, VehicleRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, VehicleTelemetryRecord>>(
            _ => new EntityStore<string, VehicleTelemetryRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, VehicleDiagnosticsSnapshot>>(
            _ => new EntityStore<string, VehicleDiagnosticsSnapshot>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, LinkRecord>>(
            _ => new EntityStore<string, LinkRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, LiveStreamRecord>>(
            _ => new EntityStore<string, LiveStreamRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, GeometryOverlayRecord>>(
            _ => new EntityStore<string, GeometryOverlayRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, PerceptionTrackRecord>>(
            _ => new EntityStore<string, PerceptionTrackRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, CameraSourceRecord>>(
            _ => new EntityStore<string, CameraSourceRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, MavlinkCameraDefinitionRecord>>(
            _ => new EntityStore<string, MavlinkCameraDefinitionRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, CameraStreamRecord>>(
            _ => new EntityStore<string, CameraStreamRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, MissionRecord>>(
            _ => new EntityStore<string, MissionRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, OperationalTaskRecord>>(
            _ => new EntityStore<string, OperationalTaskRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, ConsoleEventRecord>>(
            _ => new EntityStore<string, ConsoleEventRecord>(item => item.Id, StringComparer.Ordinal));
        services.AddSingleton<IEntityStore<string, OperationalCommandRecord>>(
            _ => new EntityStore<string, OperationalCommandRecord>(item => item.Id, StringComparer.Ordinal));

        services.AddSingleton<ISelectionService, SelectionService>();
    }

    private static void RegisterConnections(IServiceCollection services, string baseDirectory)
    {
        services.AddSingleton<ISerialDeviceDiscovery, WindowsSerialDeviceDiscovery>();
        services.AddSingleton<ISerialPortLeaseManager, SerialPortLeaseManager>();
        services.AddSingleton<ISerialByteTransportFactory, SerialByteTransportFactory>();
        services.AddSingleton<ISikRadioConfigurationService, SikRadioConfigurationService>();
        services.AddSingleton<ISikRadioPairingService, SikRadioPairingService>();
        services.AddSingleton<IMavlinkCodec, MavlinkSharpCodec>();
        services.AddSingleton<IPx4ParameterFileCodec, Px4ParameterFileCodec>();
        services.AddSingleton<IPx4ParameterProfileStore>(serviceProvider =>
            new Px4ParameterProfileStore(
                AppContext.BaseDirectory,
                serviceProvider.GetRequiredService<IPx4ParameterFileCodec>(),
                serviceProvider.GetRequiredService<ILogger<Px4ParameterProfileStore>>()));
        services.AddSingleton<IPx4ParameterService, Px4ParameterService>();
        services.AddSingleton<IMavlinkAutopilotAdapter, Px4MavlinkAutopilotAdapter>();
        services.AddSingleton<IMavlinkAutopilotAdapter, ArduPilotMavlinkAutopilotAdapter>();
        services.AddSingleton<IVehicleDiagnosticsProvider, Px4VehicleDiagnosticsProvider>();
        services.AddSingleton<IVehicleDiagnosticsProvider, ArduPilotVehicleDiagnosticsProvider>();
        services.AddSingleton<MavlinkConnectionRegistry>();
        services.AddSingleton<IMavlinkConnectionRegistry>(
            serviceProvider => serviceProvider.GetRequiredService<MavlinkConnectionRegistry>());
        services.AddSingleton<IMavlinkCameraControlService, MavlinkCameraControlService>();
        services.AddSingleton<IConnectionProvider, DirectLogosConnectionFactory>();
        services.AddSingleton<IConnectionProvider, LinkdConnectionProvider>();
        services.AddSingleton<IConnectionProvider, MavlinkConnectionProvider>();
        services.AddSingleton<ILogosConnectionFactory, ManagedConnectionFactory>();
        services.AddSingleton<ILogosOperationalSessionFactory, LogosOperationalSessionFactory>();
        services.AddSingleton<ILogosOperationalSessionRegistry, LogosOperationalSessionRegistry>();
        services.AddSingleton<ILogosConnectionManager>(serviceProvider =>
            new OperationalLogosConnectionManager(
                ActivatorUtilities.CreateInstance<LogosConnectionManager>(serviceProvider),
                serviceProvider.GetRequiredService<ILogosOperationalSessionRegistry>(),
                serviceProvider.GetRequiredService<ILogger<OperationalLogosConnectionManager>>()));
        services.AddSingleton<IOperationalMapSceneBuilder, OperationalMapSceneBuilder>();
        services.AddSingleton<IMapPackageValidator, MapPackageValidator>();
        services.AddSingleton<IMapPackageCatalog, MapPackageCatalog>();
        services.AddSingleton<IMapPackageInstaller, MapPackageInstaller>();
        services.AddSingleton<IMapDisplayPreferences, MapDisplayPreferences>();
        services.AddSingleton<IWeatherRadarSource, RainViewerWeatherRadarSource>();
        services.AddSingleton<IMapViewportState, MapViewportState>();
        services.AddSingleton<ITerrainElevationProvider>(serviceProvider =>
            new CopernicusTerrainElevationProvider(
                serviceProvider.GetRequiredService<AppConfiguration>().Terrain,
                serviceProvider.GetRequiredService<ILogger<CopernicusTerrainElevationProvider>>()));
        services.AddSingleton<ITerrainElevationService>(serviceProvider =>
            new TerrainElevationService(
                serviceProvider.GetRequiredService<ITerrainElevationProvider>(),
                serviceProvider.GetRequiredService<AppConfiguration>().Terrain,
                baseDirectory,
                serviceProvider.GetRequiredService<ILogger<TerrainElevationService>>()));
        services.AddSingleton<WindowsOperatorLocationService>();
        services.AddSingleton<IOperatorLocationService>(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsOperatorLocationService>());
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsOperatorLocationService>());
        services.AddSingleton<ISavedMapViewRepository, SavedMapViewRepository>();
        services.AddSingleton<IMapDeploymentBundleService, MapDeploymentBundleService>();
        services.AddSingleton<IOperationalMapEngine, OperationalMapEngine>();
        services.AddSingleton<IVehicleTrackHistory, VehicleTrackHistoryService>();
        services.AddSingleton<IGStreamerRuntime, GStreamerRuntime>();
        services.AddSingleton<IGStreamerVideoPipeline, GStreamerVideoPipeline>();
        services.AddSingleton<IGStreamerVideoPipelineFactory, GStreamerVideoPipelineFactory>();
        services.AddSingleton<ILocalVideoRecordingCatalog, LocalVideoRecordingCatalog>();
        services.AddSingleton<ILocalVideoRecordingService, LocalVideoRecordingService>();
        services.AddSingleton<IRemoteVideoRecordingCatalog, MediaMtxRemoteVideoRecordingCatalog>();
        services.AddSingleton<IUnifiedVideoTimelineService, UnifiedVideoTimelineService>();
        services.AddSingleton<IEvidenceLibrary, EvidenceLibrary>();
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
        services.AddSingleton<IGhostProfileWorkflow>(serviceProvider => serviceProvider.GetRequiredService<GhostProfileWorkflow>());
        services.AddSingleton<IGhostProfileAssetWorkflow>(serviceProvider => new GhostProfileAssetWorkflow(serviceProvider.GetRequiredService<GhostProfileWorkflow>(), baseDirectory));
        services.AddSingleton<IGhostUnitService>(serviceProvider =>
            serviceProvider.GetRequiredService<GhostUnitService>());
        services.AddSingleton<IFlightMissionExecutor, Px4MissionExecutor>();
        services.AddSingleton<IFlightMissionExecutor, GhostMissionExecutor>();
        services.AddSingleton<IFlightMissionExecutor, ArduPilotMissionExecutor>();
        services.AddSingleton<IFlightMissionExecutor, LogosMissionExecutor>();
        services.AddSingleton<IOperatorCommandGateway, RoutedOperatorCommandGateway>();
        services.AddSingleton<IManualControlRegistry, ManualControlRegistry>();
        services.AddSingleton<IOperatorControlService, OperatorControlService>();
        services.AddSingleton<IOperatorCommandWorkflow, OperatorCommandWorkflow>();
        services.AddSingleton<WindowsGamepadInputDeviceProvider>();
        services.AddSingleton<WindowsRawJoystickInputDeviceProvider>();
        services.AddSingleton<IManualInputDeviceProvider, CompositeManualInputDeviceProvider>();
        services.AddSingleton<ManualControlProfileStore>();
        services.AddSingleton<ManualControlService>();
        services.AddSingleton<IManualControlService>(serviceProvider =>
            serviceProvider.GetRequiredService<ManualControlService>());
        services.AddSingleton<IMissionTaskDocumentService, MissionTaskDocumentService>();
        services.AddSingleton<ILogosCommandMetadataFactory, LogosCommandMetadataFactory>();
        services.AddSingleton<GeometryDocumentCodec>();
        services.AddSingleton<GeometryDocumentValidator>();
        services.AddSingleton<IGeometryDocumentStore>(serviceProvider =>
            new GeometryDocumentStore(
                serviceProvider.GetRequiredService<GeometryDocumentCodec>(),
                serviceProvider.GetRequiredService<GeometryDocumentValidator>(),
                serviceProvider.GetRequiredService<ILogger<GeometryDocumentStore>>()));
        services.AddSingleton<IGeometryGroupStore, GeometryGroupStore>();
        services.AddSingleton<GeometryProtoMapper>();
        services.AddSingleton<IGeometryGateway, LogosGeometryGateway>();
        services.AddSingleton<GeometryWorkspaceService>();
        services.AddSingleton<IGeometryWorkspaceService>(serviceProvider =>
            serviceProvider.GetRequiredService<GeometryWorkspaceService>());
        services.AddSingleton<IGeometryRegistryStateSink>(serviceProvider =>
            serviceProvider.GetRequiredService<GeometryWorkspaceService>());
        services.AddSingleton<IGeometryEditSession, GeometryEditSession>();
        services.AddSingleton<IBehaviourPackageValidator, BehaviourPackageValidator>();
        services.AddSingleton<IBehaviourPackageStore, BehaviourPackageStore>();
        services.AddSingleton<IMissionTaskGateway, LogosMissionTaskGateway>();
        services.AddSingleton<IMissionTaskWorkspaceService, MissionTaskWorkspaceService>();
        services.AddSingleton<LogosBehaviourPackageCatalog>();
        services.AddSingleton<IBehaviourPackageCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<LogosBehaviourPackageCatalog>());
        services.AddSingleton<IBehaviourRemotePackageSource, LogosBehaviourRemotePackageSource>();
        services.AddSingleton<IBehaviourWorkspaceService, BehaviourWorkspaceService>();
        services.AddSingleton<BehaviourPackageProtoMapper>();
        services.AddSingleton<IBehaviourPackageGateway, LogosBehaviourPackageGateway>();
        services.AddSingleton<IBehaviourPackageDeploymentService, BehaviourPackageDeploymentService>();
        services.AddSingleton<IBehaviourGeometryBindingService, LogosBehaviourGeometryBindingService>();
        services.AddSingleton<IBehaviourBindingWorkspaceService, BehaviourBindingWorkspaceService>();
        services.AddSingleton<IAutonomyDeploymentBundleService, AutonomyDeploymentBundleService>();
        services.AddSingleton<IOperationalInspectionTargetService, OperationalInspectionTargetService>();
        services.AddSingleton<IOperationalExecutionTargetResolver, OperationalExecutionTargetResolver>();
        services.AddSingleton<IOperationalRunService, OperationalRunService>();
        services.AddSingleton<IOperationalSupervisionService, LogosOperationalSupervisionService>();
        services.AddSingleton<IOperationalInterventionService, LogosOperationalInterventionService>();
        services.AddSingleton<ILinkdServiceController, WindowsLinkdServiceController>();
        services.AddHostedService<LinkdServiceStartupService>();
        services.AddHostedService<ConnectionSupervisorService>();
        services.AddHostedService(serviceProvider =>
            (GhostUnitService)serviceProvider.GetRequiredService<IGhostUnitService>());
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsGamepadInputDeviceProvider>());
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsRawJoystickInputDeviceProvider>());
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<ManualControlService>());
        services.AddHostedService<GeometryRegistrySupervisionService>();
        services.AddHostedService<MissionTaskEventProjectionService>();
    }

    private static void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton<UnitsPanelViewModel>(serviceProvider =>
            new UnitsPanelViewModel(
                serviceProvider.GetRequiredService<IEntityStore<string, RuntimeRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, ConnectionRecord>>(),
                serviceProvider.GetRequiredService<ISelectionService>(),
                serviceProvider.GetRequiredService<IEntityStore<string, VehicleRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, VehicleTelemetryRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, OperationalCommandRecord>>(),
                serviceProvider.GetRequiredService<IGhostUnitService>(),
                serviceProvider.GetRequiredService<IMapViewportState>(),
                serviceProvider.GetRequiredService<OperatorControlsViewModel>(),
                serviceProvider.GetRequiredService<IMapNavigationController>(),
                serviceProvider.GetRequiredService<IManualControlService>(),
                serviceProvider.GetRequiredService<IEntityStore<string, VehicleDiagnosticsSnapshot>>(),
                serviceProvider.GetRequiredService<IUnitDefinitionService>(),
                serviceProvider.GetRequiredService<IUnitSettingsService>(),
                serviceProvider.GetRequiredService<IUnitObservationWorkflow>(),
                serviceProvider.GetRequiredService<IGhostUnitWorkflow>(),
                serviceProvider.GetRequiredService<IFlightMissionWorkflow>(),
                serviceProvider.GetRequiredService<ITeamWorkflow>(),
                serviceProvider.GetRequiredService<IUiDispatcher>(),
                  serviceProvider.GetRequiredService<ITeamSelectionWorkflow>(),
                  serviceProvider.GetRequiredService<IFormationLockWorkflow>(),
                  serviceProvider.GetRequiredService<IReviewedOperationWorkflow>(),
                  serviceProvider.GetRequiredService<IGhostProfileWorkflow>(),
                  serviceProvider.GetRequiredService<IFormationAssignmentWorkflow>(),
                  serviceProvider.GetRequiredService<IFormationAuthoringWorkflow>(),
                  serviceProvider.GetRequiredService<UnitsLibraryViewModel>()));
        services.AddSingleton<ApplicationStatusViewModel>();

        services.AddSingleton<OperationalMapViewModel>();
        services.AddSingleton<IMapNavigationController>(serviceProvider =>
            serviceProvider.GetRequiredService<OperationalMapViewModel>());
        services.AddSingleton<MapLibraryViewModel>();
        services.AddSingleton<GeometryWorkspaceViewModel>();
        services.AddSingleton<GeometryLibraryViewModel>();
        services.AddSingleton<FlightMissionViewModel>();
        services.AddSingleton<FormationAuthoringViewModel>();
        services.AddSingleton<AutonomyWorkspaceViewModel>();
        services.AddSingleton<CameraPanelViewModel>();
        services.AddSingleton<OperatorControlsViewModel>(serviceProvider =>
            new OperatorControlsViewModel(
                serviceProvider.GetRequiredService<IOperatorCommandWorkflow>(),
                serviceProvider.GetRequiredService<ISelectionService>(),
                serviceProvider.GetRequiredService<IEntityStore<string, VehicleRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, VehicleTelemetryRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, ConnectionRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, OperationalCommandRecord>>(),
                serviceProvider.GetServices<IFormationProvider>(),
                serviceProvider.GetRequiredService<IUnitSettingsService>(),
                serviceProvider.GetRequiredService<IUiDispatcher>(),
                serviceProvider.GetRequiredService<IOperatorTargetScopeWorkflow>()));
        services.AddSingleton<ManualControlViewModel>();
        services.AddSingleton<OperateViewModel>();
        services.AddSingleton<EventsViewModel>();
        services.AddSingleton<CommandsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<ConnectionsViewModel>();
        services.AddSingleton<MyTeamViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<GhostProfilesViewModel>();
        services.AddSingleton<UnitsLibraryViewModel>(serviceProvider =>
            new UnitsLibraryViewModel(
                serviceProvider.GetRequiredService<IUnitAssociationWorkflow>(),
                serviceProvider.GetRequiredService<IEntityStore<string, ConnectionRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, VehicleRecord>>(),
                serviceProvider.GetRequiredService<IEntityStore<string, CameraSourceRecord>>(),
                serviceProvider.GetRequiredService<ILocalizationService>(),
                serviceProvider.GetRequiredService<IConnectionManagementWorkflow>(),
                serviceProvider.GetRequiredService<IPx4ParameterService>(),
                serviceProvider.GetRequiredService<IPx4ParameterProfileStore>()));
        services.AddSingleton<UnitsWorkspaceViewModel>();
        services.AddSingleton<EmbeddedTerminalViewModel>();
        services.AddSingleton<ShellViewModel>();
    }
}
