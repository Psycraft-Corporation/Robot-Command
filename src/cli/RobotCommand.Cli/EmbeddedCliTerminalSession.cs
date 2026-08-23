using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Core;

namespace RobotCommand.Cli;

/// <summary>
/// Output sink for an interactive CLI hosted by another Robot Command front end.
/// The command engine remains in-process and uses that front end's Runtime service graph.
/// </summary>
public interface IEmbeddedCliTerminalOutput
{
    void WriteOutput(string line);
    void WriteError(string line);
}

/// <summary>
/// Hosts the complete interactive Robot Command command surface against an already-running Runtime.
/// It deliberately does not start or stop hosting, connection supervision, or the application itself.
/// </summary>
public sealed class EmbeddedCliTerminalSession : IDisposable
{
    private readonly ISelectionWorkflow _selection;
    private readonly IMapViewWorkflow _map;
    private readonly IMapSceneObservationWorkflow _scene;
    private readonly IThreeDSceneWorkflow _threeD;
    private readonly IGhostUnitWorkflow _ghosts;
    private readonly IGhostProfileWorkflow _ghostProfiles;
    private readonly IGhostProfileAssetWorkflow _ghostAssets;
    private readonly IManualControlWorkflow _manual;
    private readonly IOperatorCommandWorkflow _operatorWorkflow;
    private readonly IUnitObservationWorkflow _units;
    private readonly IAutonomyWorkflow _autonomy;
    private readonly IBehaviourWorkflow _behaviours;
    private readonly IGeometryWorkflow _geometry;
    private readonly IFlightMissionWorkflow _flightMissions;
    private readonly IFenceWorkflow _fences;
    private readonly IPx4ParameterProfileWorkflow _parameters;
    private readonly ISikRadioWorkflow _sik;
    private readonly IReviewedOperationWorkflow _reviewed;
    private readonly IEvidenceWorkflow _evidence;
    private readonly IMediaWorkflow _media;
    private readonly IMapLibraryWorkflow _mapLibrary;
    private readonly ITeamObserverClientWorkflow _teamObserver;
    private readonly ITeamWorkflow _teams;
    private readonly IFormationLockWorkflow _formation;
    private readonly IFormationAuthoringWorkflow _formationAuthoring;
    private readonly ITeamSelectionWorkflow _teamSelection;
    private readonly IOperatorTargetScopeWorkflow _targetScope;
    private readonly IApplicationPreferencesWorkflow _preferences;
    private readonly ConsoleReporter _reporter;
    private readonly SessionCommands.SessionWatcher _watcher;
    private readonly OperatorCommands.WorkflowWatcher _operatorWatcher;
    private bool _disposed;

    public EmbeddedCliTerminalSession(IServiceProvider services, IEmbeddedCliTerminalOutput output)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);

        _selection = services.GetRequiredService<ISelectionWorkflow>();
        _map = services.GetRequiredService<IMapViewWorkflow>();
        _scene = services.GetRequiredService<IMapSceneObservationWorkflow>();
        // The standalone 3D workspace is no longer exposed, but the embedded
        // terminal still supports the existing 3D command surface through the
        // Operate world-scene coordinator.
        _threeD = services.GetRequiredService<IThreeDWorldSceneWorkflow>();
        _ghosts = services.GetRequiredService<IGhostUnitWorkflow>();
        _ghostProfiles = services.GetRequiredService<IGhostProfileWorkflow>();
        _ghostAssets = services.GetRequiredService<IGhostProfileAssetWorkflow>();
        _manual = services.GetRequiredService<IManualControlWorkflow>();
        _operatorWorkflow = services.GetRequiredService<IOperatorCommandWorkflow>();
        _units = services.GetRequiredService<IUnitObservationWorkflow>();
        _autonomy = services.GetRequiredService<IAutonomyWorkflow>();
        _behaviours = services.GetRequiredService<IBehaviourWorkflow>();
        _geometry = services.GetRequiredService<IGeometryWorkflow>();
        _flightMissions = services.GetRequiredService<IFlightMissionWorkflow>();
        _fences = services.GetRequiredService<IFenceWorkflow>();
        _parameters = services.GetRequiredService<IPx4ParameterProfileWorkflow>();
        _sik = services.GetRequiredService<ISikRadioWorkflow>();
        _reviewed = services.GetRequiredService<IReviewedOperationWorkflow>();
        _evidence = services.GetRequiredService<IEvidenceWorkflow>();
        _media = services.GetRequiredService<IMediaWorkflow>();
        _mapLibrary = services.GetRequiredService<IMapLibraryWorkflow>();
        _teamObserver = services.GetRequiredService<ITeamObserverClientWorkflow>();
        _teams = services.GetRequiredService<ITeamWorkflow>();
        _formation = services.GetRequiredService<IFormationLockWorkflow>();
        _formationAuthoring = services.GetRequiredService<IFormationAuthoringWorkflow>();
        _teamSelection = services.GetRequiredService<ITeamSelectionWorkflow>();
        _targetScope = services.GetRequiredService<IOperatorTargetScopeWorkflow>();
        _preferences = services.GetRequiredService<IApplicationPreferencesWorkflow>();
        _reporter = new ConsoleReporter(json: false, output.WriteOutput, output.WriteError);
        _watcher = new SessionCommands.SessionWatcher(_selection, _teamSelection, _map, _scene, _ghosts, _manual, _reporter);
        _operatorWatcher = new OperatorCommands.WorkflowWatcher(_operatorWorkflow, _reporter);
        _operatorWorkflow.Changed += _operatorWatcher.OnChanged;
    }

    public Task<bool> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return SessionCommands.ExecuteAsync(
            command,
            _selection,
            _teamSelection,
            _targetScope,
            _map,
            _scene,
            _threeD,
            _ghosts,
            _ghostProfiles,
            _ghostAssets,
            _manual,
            _operatorWorkflow,
            _units,
            _autonomy,
            _behaviours,
            _geometry,
            _flightMissions,
            _fences,
            _parameters,
            _sik,
            _reviewed,
            _evidence,
            _media,
            _mapLibrary,
            _teamObserver,
            _teams,
            _formation,
            _formationAuthoring,
            _preferences,
            _reporter,
            _watcher,
            _operatorWatcher,
            cancellationToken,
            ManualControlOwnerKind.Gui,
            "embedded-terminal");
    }

    public void WriteWelcome()
        => _reporter.Info("Embedded Robot Command CLI ready. Type 'help' for commands. The GUI Runtime remains active when you close this terminal.");

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _operatorWorkflow.Changed -= _operatorWatcher.OnChanged;
        _watcher.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
