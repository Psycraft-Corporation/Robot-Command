using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Serial;
using RobotCommand.Services.Simulation;
using RobotCommand.State;

namespace RobotCommand.ViewModels;

public sealed class ConnectionsViewModel : ObservableObject
{
    private readonly ILogosConnectionManager _manager;
    private readonly IConnectionManagementWorkflow _workflow;
    private readonly AppConfiguration _configuration;
    private readonly IEntityStore<string, ConnectionRecord> _connectionStore;
    private readonly IEntityStore<string, LinkRecord> _linkStore;
    private readonly IEntityStore<string, ConsoleEventRecord> _eventStore;
    private readonly IEntityStore<string, VehicleRecord> _vehicleStore;
    private readonly ISelectionService _selection;
    private readonly ILogger<ConnectionsViewModel> _logger;
    private readonly IGhostUnitService? _ghosts;
    private readonly ISerialDeviceDiscovery _serialDevices;
    private readonly ISikRadioConfigurationService _sikRadio;
    private readonly ISikRadioPairingService _sikPairing;
    private readonly ISerialPortLeaseManager _serialPortLeases;
    private readonly IUiDispatcher? _dispatcher;
    private readonly SikRadioProbeCache _radioProbeCache = new();
    private readonly IPx4ParameterService? _parameterService;
    private readonly IPx4ParameterProfileStore? _parameterProfiles;
    private ConnectionRecord? _selectedConnection;
    private bool _replacingLiveSelection;
    private string? _selectedConnectionId;
    private string _name = "";
    private string _target = "";
    private ConnectionMode _mode = ConnectionMode.Direct;
    private MavlinkAutopilotProfile _mavlinkAutopilot = MavlinkAutopilotProfile.Px4;
    private MavlinkTransportKind _mavlinkTransport = MavlinkTransportKind.UdpListener;
    private string _mavlinkSystemAliases = "";
    private int _mavlinkBaudRate = 57600;
    private int _linkdBaudRate = 57600;
    private string? _serialDeviceId;
    private string? _serialPortName;
    private SerialDeviceDescriptor? _selectedSerialDevice;
    private SerialDeviceDescriptor? _pairingSourceRadio;
    private SerialDeviceDescriptor? _pairingTargetRadio;
    private string _serialDiscoveryStatus = "Select Detect devices to find attached serial radios.";
    private string _radioProbeStatus = "Probe the disconnected radio to read its settings.";
    private bool _showAdvancedRadioSettings;
    private bool _radioApplyPending;
    private bool _radioApplyIncludesRemote;
    private string _radioApplySummary = "";
    private string _radioPairingStatus = "Attach two SiK radios directly by USB to pair them.";
    private bool _autoReconnect = true;
    private string _apiKey = "";
    private string _bearerToken = "";
    private string _actionStatus = "Select a connection or add a new one.";
    private string _sdkCallSummary = "No SDK calls have been made yet.";
    private Px4ParameterTarget? _selectedParameterTarget;
    private Px4ParameterProfile? _selectedParameterProfile;
    private string _parameterStatus = "Select a PX4 MAVLink unit to manage parameter profiles.";
    private bool _parameterBusy;

    public ConnectionsViewModel(
        AppConfiguration configuration,
        ILogosConnectionManager manager,
        IConnectionManagementWorkflow workflow,
        IEntityStore<string, ConnectionRecord> connectionStore,
        IEntityStore<string, LinkRecord> linkStore,
        IEntityStore<string, ConsoleEventRecord> eventStore,
        IEntityStore<string, VehicleRecord> vehicleStore,
        ISelectionService selection,
        ILogger<ConnectionsViewModel> logger,
        ISerialDeviceDiscovery serialDevices,
        ISikRadioConfigurationService sikRadio,
        ISikRadioPairingService sikPairing,
        ISerialPortLeaseManager serialPortLeases,
        IGhostUnitService? ghosts = null,
        IPx4ParameterService? parameterService = null,
        IPx4ParameterProfileStore? parameterProfiles = null,
        IUiDispatcher? dispatcher = null)
    {
        _configuration = configuration;
        _manager = manager;
        _workflow = workflow;
        _connectionStore = connectionStore;
        _linkStore = linkStore;
        _eventStore = eventStore;
        _vehicleStore = vehicleStore;
        _selection = selection;
        _logger = logger;
        _serialDevices = serialDevices;
        _sikRadio = sikRadio;
        _sikPairing = sikPairing;
        _serialPortLeases = serialPortLeases;
        _dispatcher = dispatcher;
        _ghosts = ghosts;
        _parameterService = parameterService;
        _parameterProfiles = parameterProfiles;

        Connections = connectionStore.Items;
        Links = new ObservableCollection<LinkRecord>();
        LinkDetails = new ObservableCollection<string>();
        Events = new ObservableCollection<ConsoleEventRecord>();
        EventLines = new ObservableCollection<string>();
        LogLines = new ObservableCollection<string>();
        SdkCalls = new ObservableCollection<string>();
        Tiles = new ObservableCollection<ConnectionMetric>(EmptyTiles());
        Modes = new ObservableCollection<ConnectionMode>(Enum.GetValues<ConnectionMode>().Where(item => item != ConnectionMode.Ghost));
        MavlinkAutopilotProfiles = new ObservableCollection<MavlinkAutopilotProfile>(
            Enum.GetValues<MavlinkAutopilotProfile>());
        MavlinkTransports = new ObservableCollection<MavlinkTransportKind>(Enum.GetValues<MavlinkTransportKind>());
        DetectedSerialDevices = new ObservableCollection<SerialDeviceDescriptor>();
        RadioSettings = new ObservableCollection<SikRadioSettingRow>();
        ParameterTargets = new ObservableCollection<Px4ParameterTarget>();
        ParameterProfiles = new ObservableCollection<Px4ParameterProfile>();
        ParameterDiffs = new ObservableCollection<Px4ParameterDiff>();

        ((INotifyCollectionChanged)Connections).CollectionChanged += OnConnectionsChanged;
        ((INotifyCollectionChanged)linkStore.Items).CollectionChanged += OnLiveDataChanged;
        ((INotifyCollectionChanged)eventStore.Items).CollectionChanged += OnLiveDataChanged;
        ((INotifyCollectionChanged)vehicleStore.Items).CollectionChanged += OnLiveDataChanged;

        AddCommand = new AsyncRelayCommand(AddAsync, CanAdd);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedConnection is not null);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        ToggleConnectionCommand = new RelayCommand(_ => _ = ToggleConnectionAsync(), _ => SelectedConnection is not null);
        NewConnectionCommand = new RelayCommand(_ => BeginNew());
        DetectSerialDevicesCommand = new AsyncRelayCommand(DetectSerialDevicesAsync);
        UseSerialDeviceCommand = new RelayCommand(_ => UseSelectedSerialDevice(), _ => SelectedSerialDevice is not null);
        ProbeRadioCommand = new AsyncRelayCommand(ProbeRadioAsync, CanProbeRadio);
        PairRadiosCommand = new AsyncRelayCommand(PairRadiosAsync, CanPairRadios);
        ApplyLocalRadioCommand = new AsyncRelayCommand(
            (cancellationToken) => ApplyRadioAsync(includeRemote: false, cancellationToken),
            CanApplyRadio);
        ApplyPairedRadioCommand = new AsyncRelayCommand(
            (cancellationToken) => ApplyRadioAsync(includeRemote: true, cancellationToken),
            () => CanApplyRadio() && RadioSettings.Any(item => item.RemoteValue is not null));
        ReviewLocalRadioCommand = new RelayCommand(_ => ReviewRadioApply(false), _ => CanApplyRadio());
        ReviewPairedRadioCommand = new RelayCommand(_ => ReviewRadioApply(true), _ => CanApplyRadio() && RadioSettings.Any(item => item.RemoteValue is not null));
        ConfirmRadioApplyCommand = new AsyncRelayCommand(
            cancellationToken => ApplyRadioAsync(_radioApplyIncludesRemote, cancellationToken),
            () => IsRadioApplyPending);
        CancelRadioApplyCommand = new RelayCommand(_ => IsRadioApplyPending = false, _ => IsRadioApplyPending);
        DownloadParametersCommand = new AsyncRelayCommand(DownloadParametersAsync, CanManageParameters);
        CompareParametersCommand = new AsyncRelayCommand(CompareParametersAsync, CanCompareParameters);
        ApplyParametersCommand = new AsyncRelayCommand(ApplyParametersAsync, CanApplyParameters);

        SelectedConnection = Connections.FirstOrDefault();
        if (SelectedConnection is null)
        {
            BeginNew();
        }
        _ = DetectSerialDevicesAsync(CancellationToken.None);
        _ = RefreshParameterProfilesAsync();
    }

    public ReadOnlyObservableCollection<ConnectionRecord> Connections { get; }
    public bool IsEmpty => Connections.Count == 0;
    public ObservableCollection<LinkRecord> Links { get; }
    public ObservableCollection<string> LinkDetails { get; }
    public ObservableCollection<ConsoleEventRecord> Events { get; }
    public ObservableCollection<string> EventLines { get; }
    public ObservableCollection<string> LogLines { get; }
    public ObservableCollection<string> SdkCalls { get; }
    public ObservableCollection<ConnectionMetric> Tiles { get; }
    public ObservableCollection<ConnectionMode> Modes { get; }
    public ObservableCollection<MavlinkAutopilotProfile> MavlinkAutopilotProfiles { get; }
    public ObservableCollection<MavlinkTransportKind> MavlinkTransports { get; }
    public ObservableCollection<SerialDeviceDescriptor> DetectedSerialDevices { get; }
    public ObservableCollection<SikRadioSettingRow> RadioSettings { get; }
    public ObservableCollection<Px4ParameterTarget> ParameterTargets { get; }
    public ObservableCollection<Px4ParameterProfile> ParameterProfiles { get; }
    public ObservableCollection<Px4ParameterDiff> ParameterDiffs { get; }

    public ConnectionRecord? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            // The connection store replaces records as live state changes. Avalonia can
            // briefly push null into a bound SelectedItem during that replacement. Keep
            // the selection by stable ID instead of allowing that transient null to clear it.
            if (value is null && _selectedConnectionId is not null)
            {
                value = Connections.FirstOrDefault(item => item.Id == _selectedConnectionId);
            }

            if (!SetProperty(ref _selectedConnection, value)) return;
            _selectedConnectionId = value?.Id;
            OnPropertyChanged(nameof(SelectedConnectionId));
            OnPropertyChanged(nameof(IsCreatingConnection));
            OnPropertyChanged(nameof(IsExistingConnection));
            OnPropertyChanged(nameof(IsGhostConnection));
            OnPropertyChanged(nameof(IsEditableConnection));
            OnPropertyChanged(nameof(IsMavlinkConnection));
            OnPropertyChanged(nameof(IsLinkdConnection));
            OnPropertyChanged(nameof(UsesLogosCredentials));
            OnPropertyChanged(nameof(ShowConnectionControls));
            OnPropertyChanged(nameof(ConnectionKind));
            if (value is not null)
            {
                _name = value.Name;
                _target = value.Target;
                _mode = value.Mode;
                _autoReconnect = value.AutoReconnect;
                _serialDeviceId = null;
                _serialPortName = null;
                _selectedSerialDevice = null;
                if (_manager.TryGetDefinition(value.Id, out var definition))
                {
                    if (definition?.Mavlink is { } mavlink)
                    {
                        _mavlinkTransport = mavlink.Transport;
                        _mavlinkAutopilot = mavlink.Autopilot;
                        _mavlinkSystemAliases = FormatAliases(mavlink.Aliases);
                        _mavlinkBaudRate = mavlink.EffectiveBaudRate;
                        _serialDeviceId = mavlink.SerialDeviceId;
                        _serialPortName = mavlink.LastKnownPort;
                    }
                    else if (definition?.Linkd is { } linkd)
                    {
                        _linkdBaudRate = linkd.EffectiveBaudRate;
                        _serialDeviceId = linkd.SerialDeviceId;
                        _serialPortName = linkd.LastKnownPort;
                    }
                    _selectedSerialDevice = DetectedSerialDevices.FirstOrDefault(item =>
                        (!string.IsNullOrWhiteSpace(_serialDeviceId) && item.DeviceId.Equals(_serialDeviceId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(_serialPortName) && item.PortName.Equals(_serialPortName, StringComparison.OrdinalIgnoreCase)));
                }
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Target));
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(AutoReconnect));
                OnPropertyChanged(nameof(MavlinkAutopilot));
                OnPropertyChanged(nameof(MavlinkTransport));
                OnPropertyChanged(nameof(MavlinkSystemAliases));
                OnPropertyChanged(nameof(MavlinkBaudRate));
                OnPropertyChanged(nameof(LinkdBaudRate));
                OnPropertyChanged(nameof(SelectedSerialDevice));
                OnPropertyChanged(nameof(IsSerialMavlink));
                OnPropertyChanged(nameof(IsUdpMavlink));
                RestoreRadioProbeForSelectedRadio();
                if (!_replacingLiveSelection)
                {
                    _selection.Select(SelectionFactory.From(value));
                }
                ActionStatus = $"Selected {value.Name}.";
            }
            RefreshDetails();
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(IsConnectionActive));
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// Stable selection key for the live connection list. Connection records
    /// are immutable snapshots and are replaced as telemetry changes, so the
    /// list must select by identity rather than by object reference.
    /// </summary>
    public string? SelectedConnectionId
    {
        get => _selectedConnectionId;
        set
        {
            if (string.Equals(_selectedConnectionId, value, StringComparison.Ordinal))
            {
                return;
            }

            var connection = value is null
                ? null
                : Connections.FirstOrDefault(item => item.Id == value);
            if (value is not null && connection is null)
            {
                return;
            }

            SelectedConnection = connection;
        }
    }

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) RaiseCommandStates(); } }
    public string Target { get => _target; set { if (SetProperty(ref _target, value)) RaiseCommandStates(); } }
    public ConnectionMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            if (IsCreatingConnection)
            {
                Target = value switch
                {
                    ConnectionMode.Mavlink => "udp-listen://0.0.0.0:14550",
                    ConnectionMode.FieldLink => "http://127.0.0.1:9467",
                    _ => "http://localhost:19000"
                };
            }
            OnPropertyChanged(nameof(IsMavlinkConnection));
            OnPropertyChanged(nameof(IsLinkdConnection));
            OnPropertyChanged(nameof(IsSerialMavlink));
            OnPropertyChanged(nameof(IsUdpMavlink));
            OnPropertyChanged(nameof(UsesLogosCredentials));
            OnPropertyChanged(nameof(Protocol));
            OnPropertyChanged(nameof(BaudRate));
            OnPropertyChanged(nameof(ConnectionKind));
            RaiseCommandStates();
        }
    }
    public MavlinkAutopilotProfile MavlinkAutopilot
    {
        get => _mavlinkAutopilot;
        set => SetProperty(ref _mavlinkAutopilot, value);
    }
    public MavlinkTransportKind MavlinkTransport
    {
        get => _mavlinkTransport;
        set
        {
            if (!SetProperty(ref _mavlinkTransport, value)) return;
            if (Mode == ConnectionMode.Mavlink)
            {
                Target = value == MavlinkTransportKind.Serial
                    ? $"serial://{_serialPortName ?? "COM3"}"
                    : "udp-listen://0.0.0.0:14550";
            }
            OnPropertyChanged(nameof(IsSerialMavlink));
            OnPropertyChanged(nameof(IsUdpMavlink));
            OnPropertyChanged(nameof(BaudRate));
            RaiseCommandStates();
        }
    }
    public string MavlinkSystemAliases
    {
        get => _mavlinkSystemAliases;
        set
        {
            if (SetProperty(ref _mavlinkSystemAliases, value))
            {
                RaiseCommandStates();
            }
        }
    }
    public int MavlinkBaudRate
    {
        get => _mavlinkBaudRate;
        set { if (SetProperty(ref _mavlinkBaudRate, value)) { OnPropertyChanged(nameof(BaudRate)); RaiseCommandStates(); } }
    }
    public int LinkdBaudRate
    {
        get => _linkdBaudRate;
        set { if (SetProperty(ref _linkdBaudRate, value)) { OnPropertyChanged(nameof(BaudRate)); RaiseCommandStates(); } }
    }
    public SerialDeviceDescriptor? SelectedSerialDevice
    {
        get => _selectedSerialDevice;
        set
        {
            if (!SetProperty(ref _selectedSerialDevice, value)) return;
            if (value is not null && IsLinkdConnection)
            {
                _serialDeviceId = value.DeviceId;
                _serialPortName = value.PortName;
                SerialDiscoveryStatus = $"LinkD will use {value.DisplayName} on {value.PortName} when connected.";
            }
            RestoreRadioProbeForSelectedRadio();
            (UseSerialDeviceCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseCommandStates();
        }
    }
    public SerialDeviceDescriptor? PairingSourceRadio
    {
        get => _pairingSourceRadio;
        set
        {
            if (!SetProperty(ref _pairingSourceRadio, value)) return;
            EnsurePairingTargetIsDistinct();
            RaiseCommandStates();
        }
    }
    public SerialDeviceDescriptor? PairingTargetRadio
    {
        get => _pairingTargetRadio;
        set
        {
            if (!SetProperty(ref _pairingTargetRadio, value)) return;
            RaiseCommandStates();
        }
    }
    public string SerialDiscoveryStatus { get => _serialDiscoveryStatus; private set => SetProperty(ref _serialDiscoveryStatus, value); }
    public string RadioProbeStatus { get => _radioProbeStatus; private set => SetProperty(ref _radioProbeStatus, value); }
    public string RadioPairingStatus { get => _radioPairingStatus; private set => SetProperty(ref _radioPairingStatus, value); }
    public bool ShowAdvancedRadioSettings
    {
        get => _showAdvancedRadioSettings;
        set { if (SetProperty(ref _showAdvancedRadioSettings, value)) OnPropertyChanged(nameof(VisibleRadioSettings)); }
    }
    public IEnumerable<SikRadioSettingRow> VisibleRadioSettings => RadioSettings.Where(item => ShowAdvancedRadioSettings || item.IsCommon);
    public bool IsRadioApplyPending
    {
        get => _radioApplyPending;
        private set
        {
            if (!SetProperty(ref _radioApplyPending, value)) return;
            RaiseCommandStates();
        }
    }
    public string RadioApplySummary { get => _radioApplySummary; private set => SetProperty(ref _radioApplySummary, value); }
    public bool AutoReconnect { get => _autoReconnect; set => SetProperty(ref _autoReconnect, value); }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public string BearerToken { get => _bearerToken; set => SetProperty(ref _bearerToken, value); }
    public string ActionStatus { get => _actionStatus; private set => SetProperty(ref _actionStatus, value); }
    public bool IsCreatingConnection => SelectedConnection is null;
    public bool IsExistingConnection => SelectedConnection is not null;
    public bool IsGhostConnection => SelectedConnection?.IsGhost == true;
    public bool IsEditableConnection => !IsGhostConnection;
    public bool IsMavlinkConnection => Mode == ConnectionMode.Mavlink;
    public bool IsLinkdConnection => Mode == ConnectionMode.FieldLink;
    public bool IsSerialMavlink => IsMavlinkConnection && MavlinkTransport == MavlinkTransportKind.Serial;
    public bool IsUdpMavlink => IsMavlinkConnection && MavlinkTransport == MavlinkTransportKind.UdpListener;
    public bool UsesLogosCredentials => Mode == ConnectionMode.Direct;
    public bool ShowConnectionControls => IsExistingConnection && !IsGhostConnection;
    public string ConnectionKind => IsGhostConnection
        ? "Simulated in-app connection"
        : Mode == ConnectionMode.FieldLink ? "Logos LinkD ground link" : Mode.ToString();
    public string Protocol => Mode switch
    {
        ConnectionMode.Direct => "gRPC",
        ConnectionMode.FieldLink => "LinkD gRPC",
        ConnectionMode.Mavlink => "MAVLink 2",
        ConnectionMode.Ghost => "In-app simulation",
        _ => "Not reported"
    };
    public string BaudRate => IsSerialMavlink
        ? $"{MavlinkBaudRate:N0}"
        : IsLinkdConnection
        ? $"{LinkdBaudRate:N0}"
        : Mode is ConnectionMode.Direct or ConnectionMode.Mavlink
        ? "Not applicable"
        : "Not reported";
    public string ConnectedAt => SelectedConnection?.ConnectedAt?.ToLocalTime().ToString("G") ?? "Not connected";
    public string LastConnectedAt => SelectedConnection?.LastConnectedAt?.ToLocalTime().ToString("G") ?? "Never";
    public string LastSeen => SelectedConnection?.LastSeen?.ToLocalTime().ToString("G") ?? "Never";
    public string LastAttempt => SelectedConnection?.LastAttempt?.ToLocalTime().ToString("G") ?? "Never";
    private ConnectionRecord? CurrentConnection => SelectedConnection is null
        ? null
        : Connections.FirstOrDefault(item => item.Id == SelectedConnection.Id);

    public bool IsDisconnected => CurrentConnection is null ||
        CurrentConnection.State is AvailabilityState.Offline or AvailabilityState.Faulted or AvailabilityState.Unknown;
    public bool IsConnectionActive => !IsDisconnected;
    public string ActionButtonText => IsDisconnected ? "Connect" : "Disconnect";
    public string SignalSummary => Links.Count == 0 ? "Not reported" : $"{Links.Count} link(s) reported";
    public string SdkCallSummary => _sdkCallSummary;
    public bool IsPx4ParameterPanel => IsExistingConnection && IsMavlinkConnection && MavlinkAutopilot == MavlinkAutopilotProfile.Px4;
    public bool IsArduPilotParameterPanel => IsExistingConnection && IsMavlinkConnection && MavlinkAutopilot == MavlinkAutopilotProfile.ArduPilot;
    public bool IsMavlinkParameterPanel => IsPx4ParameterPanel || IsArduPilotParameterPanel;
    public string ParameterBackendName => IsArduPilotParameterPanel ? "ArduPilot" : "PX4";
    public string ParameterPanelTitle => $"{ParameterBackendName} parameters";
    public string ParameterPanelDescription => "Download, compare, import, export, and safely apply profiles while disarmed.";
    public string ParameterBackendUnit => $"{ParameterBackendName} MAVLink unit";
    public Px4ParameterTarget? SelectedParameterTarget
    {
        get => _selectedParameterTarget;
        set { if (SetProperty(ref _selectedParameterTarget, value)) { ParameterDiffs.Clear(); RaiseCommandStates(); } }
    }
    public Px4ParameterProfile? SelectedParameterProfile
    {
        get => _selectedParameterProfile;
        set { if (SetProperty(ref _selectedParameterProfile, value)) { ParameterDiffs.Clear(); RaiseCommandStates(); } }
    }
    public string ParameterStatus { get => _parameterStatus; private set => SetProperty(ref _parameterStatus, value); }
    public bool IsParameterBusy { get => _parameterBusy; private set { if (SetProperty(ref _parameterBusy, value)) RaiseCommandStates(); } }

    public ICommand AddCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectionCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand NewConnectionCommand { get; }
    public ICommand DetectSerialDevicesCommand { get; }
    public ICommand UseSerialDeviceCommand { get; }
    public ICommand ProbeRadioCommand { get; }
    public ICommand PairRadiosCommand { get; }
    public ICommand ApplyLocalRadioCommand { get; }
    public ICommand ApplyPairedRadioCommand { get; }
    public ICommand ReviewLocalRadioCommand { get; }
    public ICommand ReviewPairedRadioCommand { get; }
    public ICommand ConfirmRadioApplyCommand { get; }
    public ICommand CancelRadioApplyCommand { get; }
    public ICommand DownloadParametersCommand { get; }
    public ICommand CompareParametersCommand { get; }
    public ICommand ApplyParametersCommand { get; }

    private void BeginNew()
    {
        _selectedConnectionId = null;
        SelectedConnection = null;
        OnPropertyChanged(nameof(IsCreatingConnection));
        OnPropertyChanged(nameof(IsExistingConnection));
        Name = "";
        Target = "http://localhost:19000";
        Mode = ConnectionMode.Direct;
        MavlinkTransport = MavlinkTransportKind.UdpListener;
        MavlinkSystemAliases = "";
        _linkdBaudRate = 57600;
        _serialDeviceId = null;
        _serialPortName = null;
        _selectedSerialDevice = null;
        AutoReconnect = true;
        ActionStatus = "Enter connection details and click Add connection.";
        RestoreRadioProbeForSelectedRadio();
        RefreshDetails();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsConnectionActive));
        OnPropertyChanged(nameof(ActionButtonText));
        RaiseCommandStates();
    }

    private async Task AddAsync(CancellationToken cancellationToken)
    {
        try
        {
            var definition = BuildDefinition(Guid.NewGuid().ToString("N"));
            await _workflow.CreateAsync(ToMutation(definition), cancellationToken);
            SelectedConnection = Connections.First(item => item.Id == definition.Id);
            AddLog($"Added {definition.Name}.");
            ActionStatus = $"Added {definition.Name}.";
        }
        catch (Exception ex) { Fail("Add connection", ex); }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null) return;
        try
        {
            var definition = BuildDefinition(SelectedConnection.Id);
            await _workflow.UpdateAsync(definition.Id, ToMutation(definition), cancellationToken);
            SelectedConnection = Connections.FirstOrDefault(item => item.Id == definition.Id);
            AddLog($"Saved {definition.Name}.");
            ActionStatus = $"Saved {definition.Name}.";
        }
        catch (Exception ex) { Fail("Save connection", ex); }
    }

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null) return;
        var name = SelectedConnection.Name;
        try
        {
            if (SelectedConnection.IsGhost)
            {
                if (_ghosts is not null)
                {
                    await _ghosts.DeleteByConnectionAsync(SelectedConnection.Id, cancellationToken);
                }

                SelectedConnection = Connections.FirstOrDefault(item => !item.IsGhost);
                ActionStatus = $"Deleted {name}.";
                return;
            }
            await _workflow.RemoveAsync(SelectedConnection.Id, cancellationToken);
            SelectedConnection = Connections.FirstOrDefault();
            AddLog($"Deleted {name}.");
            ActionStatus = $"Deleted {name}.";
        }
        catch (Exception ex) { Fail("Delete connection", ex); }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = SelectedConnection;
        if (connection is null) return;
        var connectionId = connection.Id;
        var connectionName = connection.Name;
        var connectionTarget = connection.Target;
        try
        {
            await _workflow.ConnectAsync(connectionId, new ConnectionCredentialInput(ApiKey, BearerToken), cancellationToken);
            ActionStatus = $"Connected to {connectionName}.";
            AddLog($"Connected to {connectionTarget}.");
            SdkCalls.Clear();
            if (connection.Mode == ConnectionMode.Mavlink)
            {
                SdkCalls.Add("Direct MAVLink connection; no Logos SDK session is used.");
            }
            else if (connection.Mode == ConnectionMode.FieldLink)
            {
                SdkCalls.Add("LinkD control/telemetry connection; no Logos SDK session is used.");
            }
            else
            {
                SdkCalls.Add("System.GetIdentityAsync");
                SdkCalls.Add("System.GetHealthAsync");
                SdkCalls.Add("Vehicle.GetStateAsync");
                SdkCalls.Add("Sensors.GetVehicleTelemetryAsync");
                SdkCalls.Add("Links.ListLinksAsync");
                SdkCalls.Add("Events.ListEventsAsync");
            }
        }
        catch (Exception ex) { Fail("Connect", ex); }
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null) return;
        try { await _workflow.DisconnectAsync(SelectedConnection.Id, cancellationToken); ActionStatus = "Disconnected."; AddLog("Disconnected."); }
        catch (Exception ex) { Fail("Disconnect", ex); }
    }

    private async Task ToggleConnectionAsync()
    {
        try
        {
            if (IsDisconnected)
                await ConnectAsync(CancellationToken.None);
            else
                await DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Fail(IsDisconnected ? "Connect" : "Disconnect", ex);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null) return;
        try { await _workflow.RefreshAsync(SelectedConnection.Id, cancellationToken); ActionStatus = "Connection refreshed."; AddLog("Connection refreshed."); }
        catch (Exception ex) { Fail("Refresh", ex); }
    }

    private bool CanAdd() => SelectedConnection is null && ValidFields();
    private bool CanSave() => SelectedConnection is not null && !IsGhostConnection && ValidFields();
    private bool CanConnect() => !IsGhostConnection && SelectedConnection?.State is AvailabilityState.Unknown or AvailabilityState.Offline or AvailabilityState.Faulted;
    private bool CanDisconnect() => !IsGhostConnection && SelectedConnection is not null && SelectedConnection.State != AvailabilityState.Offline;
    private bool CanRefresh() => !IsGhostConnection && SelectedConnection?.State is AvailabilityState.Online or AvailabilityState.Degraded or AvailabilityState.Stale;
    private bool ValidFields()
        => !string.IsNullOrWhiteSpace(Name) &&
           (Mode == ConnectionMode.Mavlink
               ? TryParseMavlinkTarget(Target, MavlinkTransport) &&
                 (MavlinkTransport != MavlinkTransportKind.Serial || MavlinkBaudRate is >= 1200 and <= 921600) &&
                 TryParseAliases(MavlinkSystemAliases, out _)
               : Mode == ConnectionMode.FieldLink
                   ? Uri.TryCreate(Target, UriKind.Absolute, out var linkdUri) &&
                     linkdUri.Scheme is "http" or "https" &&
                     LinkdBaudRate is >= 1200 and <= 921600
                   : Uri.TryCreate(Target, UriKind.Absolute, out _));

    private ConnectionDefinition BuildDefinition(string id)
    {
        MavlinkConnectionOptions? mavlink = null;
        LinkdConnectionOptions? linkd = null;
        if (Mode == ConnectionMode.Mavlink)
        {
            if (!TryParseAliases(MavlinkSystemAliases, out var aliases))
            {
                throw new InvalidOperationException(
                    "MAVLink aliases must use system-id=name entries separated by semicolons.");
            }

            mavlink = new MavlinkConnectionOptions(
                MavlinkTransport,
                MavlinkAutopilot,
                SystemAliases: aliases,
                BaudRate: MavlinkTransport == MavlinkTransportKind.Serial ? MavlinkBaudRate : null,
                SerialDeviceId: MavlinkTransport == MavlinkTransportKind.Serial ? _serialDeviceId : null,
                LastKnownPort: MavlinkTransport == MavlinkTransportKind.Serial
                    ? _serialPortName ?? MavlinkConnectionProvider.ParseSerialPort(Target)
                    : null);
        }
        else if (Mode == ConnectionMode.FieldLink)
        {
            linkd = new LinkdConnectionOptions(
                BaudRate: LinkdBaudRate,
                SerialDeviceId: _serialDeviceId,
                LastKnownPort: _serialPortName,
                RadioProfileKey: "3dr_915_dracula_v1",
                WireProfilePath: "C:\\ProgramData\\Psycraft\\Logos\\LinkD\\profiles\\3dr_constrained_v1.json");
        }

        return new ConnectionDefinition(
            id,
            Name.Trim(),
            Target.Trim(),
            Mode,
            false,
            AutoReconnect,
            Mavlink: mavlink,
            Linkd: linkd);
    }

    private static ConnectionMutationRequest ToMutation(ConnectionDefinition definition)
        => new(
            definition.Name,
            definition.Target,
            definition.Mode switch
            {
                ConnectionMode.FieldLink => ManagedConnectionMode.FieldLink,
                ConnectionMode.Mavlink => ManagedConnectionMode.Mavlink,
                _ => ManagedConnectionMode.Direct
            },
            definition.AutoConnect,
            definition.AutoReconnect,
            definition.Description,
            definition.Mavlink is null ? null : new ManagedMavlinkOptions(
                definition.Mavlink.Transport == MavlinkTransportKind.Serial ? ManagedMavlinkTransport.Serial : ManagedMavlinkTransport.UdpListener,
                definition.Mavlink.Autopilot == MavlinkAutopilotProfile.ArduPilot ? ManagedMavlinkAutopilot.ArduPilot : ManagedMavlinkAutopilot.Px4,
                definition.Mavlink.SourceSystemId,
                definition.Mavlink.SourceComponentId,
                definition.Mavlink.Aliases.ToDictionary(item => item.SystemId, item => item.Name),
                definition.Mavlink.BaudRate,
                definition.Mavlink.SerialDeviceId,
                definition.Mavlink.LastKnownPort),
            definition.Linkd is null ? null : new ManagedLinkdOptions(
                definition.Linkd.TransportPlugin, definition.Linkd.BaudRate, definition.Linkd.SerialDeviceId,
                definition.Linkd.LastKnownPort, definition.Linkd.RadioProfileKey, definition.Linkd.WireProfilePath),
            definition.Id);

    private static bool TryParseMavlinkTarget(string target, MavlinkTransportKind transport)
    {
        try
        {
            if (transport == MavlinkTransportKind.Serial)
            {
                _ = MavlinkConnectionProvider.ParseSerialPort(target);
                return true;
            }
            _ = MavlinkConnectionProvider.ParseUdpListener(target);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string FormatAliases(IReadOnlyList<MavlinkSystemAlias> aliases)
        => string.Join("; ", aliases.Select(item => $"{item.SystemId}={item.Name}"));

    private static bool TryParseAliases(
        string value,
        out IReadOnlyList<MavlinkSystemAlias> aliases)
    {
        var parsed = new List<MavlinkSystemAlias>();
        var seen = new HashSet<byte>();
        foreach (var entry in value.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 ||
                !byte.TryParse(entry[..separator].Trim(), out var systemId) ||
                systemId == 0 ||
                !seen.Add(systemId))
            {
                aliases = [];
                return false;
            }

            var name = entry[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                aliases = [];
                return false;
            }
            parsed.Add(new MavlinkSystemAlias(systemId, name));
        }

        aliases = parsed;
        return true;
    }

    private async Task DetectSerialDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await _serialDevices.DiscoverAsync(cancellationToken);
            DetectedSerialDevices.Clear();
            foreach (var device in devices) DetectedSerialDevices.Add(device);
            SelectedSerialDevice = devices.FirstOrDefault(item =>
                    (!string.IsNullOrWhiteSpace(_serialDeviceId) && item.DeviceId.Equals(_serialDeviceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(_serialPortName) && item.PortName.Equals(_serialPortName, StringComparison.OrdinalIgnoreCase)))
                ?? devices.FirstOrDefault(item => item.IsLikelySikRadio)
                ?? devices.FirstOrDefault();
            UpdatePairingDefaults();
            SerialDiscoveryStatus = devices.Count == 0
                ? "No serial devices were detected."
                : $"Detected {devices.Count} serial device(s); {devices.Count(item => item.IsLikelySikRadio)} likely SiK radio(s).";
        }
        catch (Exception ex)
        {
            SerialDiscoveryStatus = $"Serial discovery failed: {ex.Message}";
            _logger.LogWarning(ex, "Serial-device discovery failed");
        }
    }

    private void UseSelectedSerialDevice()
    {
        if (SelectedSerialDevice is not { } device) return;
        _serialDeviceId = device.DeviceId;
        _serialPortName = device.PortName;
        if (IsLinkdConnection)
        {
            _serialDeviceId = device.DeviceId;
            _serialPortName = device.PortName;
            SerialDiscoveryStatus = $"LinkD will use {device.DisplayName} on {device.PortName} when connected.";
        }
        else
        {
            MavlinkTransport = MavlinkTransportKind.Serial;
            Target = $"serial://{device.PortName}";
            SerialDiscoveryStatus = $"Using {device.DisplayName} on {device.PortName}.";
        }
        PairingSourceRadio = device;
        RaiseCommandStates();
    }

    private bool CanProbeRadio()
        => IsSerialMavlink &&
           (SelectedConnection is null || CurrentConnection?.State is AvailabilityState.Offline or AvailabilityState.Faulted) &&
           (SelectedSerialDevice is not null || !string.IsNullOrWhiteSpace(_serialPortName));

    private async Task ProbeRadioAsync(CancellationToken cancellationToken)
    {
        try
        {
            var device = await ResolveSelectedSerialDeviceAsync(cancellationToken);
            RadioProbeStatus = $"Probing local radio on {device.PortName}...";
            var probe = await _sikRadio.ProbeAsync(device, MavlinkBaudRate, cancellationToken);
            _radioProbeCache.Store(probe);
            PopulateRadioSettings(probe);
            RadioProbeStatus = probe.Local.Available
                ? probe.Remote.Available
                    ? $"Local and paired remote SiK radios detected. Local {probe.Local.FirmwareVersion}; remote {probe.Remote.FirmwareVersion}."
                    : $"Local SiK radio detected ({probe.Local.FirmwareVersion}). {probe.Remote.Error}"
                : probe.Local.Error ?? "The local SiK radio did not respond.";
            AddLog(RadioProbeStatus);
        }
        catch (Exception ex) { Fail("Probe SiK radio", ex); RadioProbeStatus = ex.Message; }
        finally { RaiseCommandStates(); }
    }

    private bool CanPairRadios()
        => IsSerialMavlink &&
           (SelectedConnection is null || CurrentConnection?.State is AvailabilityState.Offline or AvailabilityState.Faulted) &&
           PairingSourceRadio is not null &&
           PairingTargetRadio is not null &&
           !SameRadio(PairingSourceRadio, PairingTargetRadio);

    private async Task PairRadiosAsync(CancellationToken cancellationToken)
    {
        if (PairingSourceRadio is not { } source || PairingTargetRadio is not { } target) return;

        RadioPairingStatus = $"Pairing {source.PortName} to {target.PortName}...";
        try
        {
            // A radio cannot carry MAVLink traffic and accept SiK AT commands at the
            // same time. Release every Robot Command MAVLink connection using either
            // directly attached radio before opening the configuration sessions.
            var conflictingConnections = _manager.Definitions
                .Where(definition => UsesSerialPort(definition, source.PortName) || UsesSerialPort(definition, target.PortName))
                .ToArray();
            foreach (var connection in conflictingConnections)
            {
                await _manager.DisconnectAsync(connection.Id, cancellationToken);
            }

            var sourceLeased = _serialPortLeases.IsLeased(source.PortName, out var sourceOwner);
            var targetLeased = _serialPortLeases.IsLeased(target.PortName, out var targetOwner);
            if (sourceLeased || targetLeased)
            {
                var blockedPort = sourceLeased ? source.PortName : target.PortName;
                var owner = sourceOwner ?? targetOwner ?? "Robot Command";
                RadioPairingStatus = $"PAIRING_BLOCKED: {blockedPort} is still owned by {owner}. Disconnect that Robot Command activity, then retry.";
                return;
            }

            var result = await _sikPairing.PairAsync(source, target, MavlinkBaudRate, cancellationToken);
            if (result.SourceProbe is not null) _radioProbeCache.Store(result.SourceProbe);
            if (result.TargetProbe is not null) _radioProbeCache.Store(result.TargetProbe);
            RestoreRadioProbeForSelectedRadio();
            RadioPairingStatus = result.DisplayText;
            AddLog($"SiK pairing {result.Code}: {result.Message}");
        }
        catch (Exception ex)
        {
            RadioPairingStatus = $"PAIRING_FAILED: {ex.Message}";
            _logger.LogWarning(ex, "SiK radio pairing failed");
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    private bool CanApplyRadio()
        => CanProbeRadio() && RadioSettings.Count > 0 && RadioSettings.All(item => item.IsValid);

    private async Task ApplyRadioAsync(bool includeRemote, CancellationToken cancellationToken)
    {
        try
        {
            var device = await ResolveSelectedSerialDeviceAsync(cancellationToken);
            var local = RadioSettings.ToDictionary(item => item.Id, item => item.DesiredLocalValue);
            Dictionary<int, int>? remote = null;
            if (includeRemote)
            {
                if (RadioSettings.Any(item => item.RemoteValue is null))
                    throw new InvalidOperationException("The paired remote radio is unavailable. Use Apply local only or restore the radio link.");
                remote = RadioSettings.ToDictionary(item => item.Id, item => item.DesiredRemoteValue ?? item.DesiredLocalValue);
            }
            var changes = RadioSettings.Count(item => item.HasLocalChange || (includeRemote && item.HasRemoteChange));
            if (changes == 0)
            {
                RadioProbeStatus = "No SiK settings have changed.";
                return;
            }
            RadioProbeStatus = $"Applying and verifying {changes} SiK setting change(s)...";
            var result = await _sikRadio.ApplyAsync(new SikRadioApplyRequest(device, MavlinkBaudRate, local, remote), cancellationToken);
            _radioProbeCache.Store(result);
            PopulateRadioSettings(result);
            RadioProbeStatus = "SiK settings were saved, radios rebooted, and the local configuration was verified.";
            AddLog(RadioProbeStatus);
        }
        catch (Exception ex) { Fail("Apply SiK settings", ex); RadioProbeStatus = ex.Message; }
        finally { IsRadioApplyPending = false; RaiseCommandStates(); }
    }

    private void ReviewRadioApply(bool includeRemote)
    {
        var changed = RadioSettings.Where(item => item.HasLocalChange || (includeRemote && item.HasRemoteChange)).ToArray();
        if (changed.Length == 0)
        {
            RadioProbeStatus = "No SiK settings have changed.";
            return;
        }
        _radioApplyIncludesRemote = includeRemote;
        RadioApplySummary = $"Apply {changed.Length} setting change(s) to {(includeRemote ? "the local and paired remote radios" : "the local radio only")}. " +
                            "The radio will be rebooted and the serial link will briefly disappear.";
        IsRadioApplyPending = true;
    }

    private async Task<SerialDeviceDescriptor> ResolveSelectedSerialDeviceAsync(CancellationToken cancellationToken)
    {
        if (SelectedSerialDevice is { } selected) return selected;
        return await _serialDevices.ResolveAsync(_serialDeviceId, _serialPortName, cancellationToken)
            ?? throw new IOException($"Serial device '{_serialDeviceId ?? _serialPortName ?? Target}' is not present.");
    }

    private void PopulateRadioSettings(SikRadioProbeResult probe)
    {
        RadioSettings.Clear();
        foreach (var descriptor in SikRadioSettingRow.Descriptors)
        {
            var hasLocal = probe.Local.Settings.TryGetValue(descriptor.Id, out var local);
            var hasRemote = probe.Remote.Settings.TryGetValue(descriptor.Id, out var remote);
            var row = new SikRadioSettingRow(
                descriptor.Id,
                descriptor.Name,
                descriptor.Description,
                descriptor.IsCommon,
                hasLocal ? local : 0,
                hasRemote ? remote : null);
            row.PropertyChanged += (_, _) => RaiseCommandStates();
            RadioSettings.Add(row);
        }
        OnPropertyChanged(nameof(VisibleRadioSettings));
    }

    private void RestoreRadioProbeForSelectedRadio()
    {
        IsRadioApplyPending = false;
        _radioApplyIncludesRemote = false;
        RadioApplySummary = "";

        if (!IsSerialMavlink)
        {
            RadioSettings.Clear();
            RadioProbeStatus = "Radio setup is available for Serial / SiK MAVLink connections.";
            OnPropertyChanged(nameof(VisibleRadioSettings));
            return;
        }

        var deviceId = _selectedSerialDevice?.DeviceId ?? _serialDeviceId;
        var portName = _selectedSerialDevice?.PortName ?? _serialPortName;
        if (_radioProbeCache.TryGet(deviceId, portName, out var probe))
        {
            PopulateRadioSettings(probe);
            RadioProbeStatus = $"Cached probe for {probe.Device.PortName} from {probe.ProbedAt.LocalDateTime:g}. Probe again to refresh radio settings.";
            return;
        }

        RadioSettings.Clear();
        RadioProbeStatus = string.IsNullOrWhiteSpace(portName)
            ? "Select a serial radio, then probe it to read its settings."
            : $"No cached probe is available for {portName}. Probe this radio to read its settings.";
        OnPropertyChanged(nameof(VisibleRadioSettings));
    }

    private void UpdatePairingDefaults()
    {
        var radios = DetectedSerialDevices.Where(item => item.IsLikelySikRadio).ToArray();
        if (radios.Length < 2)
        {
            PairingSourceRadio = radios.FirstOrDefault();
            PairingTargetRadio = null;
            return;
        }

        if (PairingSourceRadio is null || !radios.Any(item => SameRadio(item, PairingSourceRadio)))
        {
            PairingSourceRadio = _selectedSerialDevice is not null && radios.Any(item => SameRadio(item, _selectedSerialDevice))
                ? radios.First(item => SameRadio(item, _selectedSerialDevice))
                : radios[0];
        }
        EnsurePairingTargetIsDistinct();
    }

    private void EnsurePairingTargetIsDistinct()
    {
        if (PairingSourceRadio is null) return;
        if (PairingTargetRadio is not null && !SameRadio(PairingSourceRadio, PairingTargetRadio) &&
            DetectedSerialDevices.Any(item => SameRadio(item, PairingTargetRadio))) return;
        PairingTargetRadio = DetectedSerialDevices.FirstOrDefault(item =>
            item.IsLikelySikRadio && !SameRadio(item, PairingSourceRadio));
    }

    private static bool SameRadio(SerialDeviceDescriptor first, SerialDeviceDescriptor second)
        => (!string.IsNullOrWhiteSpace(first.DeviceId) &&
            first.DeviceId.Equals(second.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
           first.PortName.Equals(second.PortName, StringComparison.OrdinalIgnoreCase);

    private static bool UsesSerialPort(ConnectionDefinition definition, string portName)
    {
        if (definition.Mode != ConnectionMode.Mavlink || definition.Mavlink?.Transport != MavlinkTransportKind.Serial)
        {
            return false;
        }

        var configuredPort = definition.Mavlink.LastKnownPort;
        if (string.IsNullOrWhiteSpace(configuredPort) &&
            Uri.TryCreate(definition.Target, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("serial", StringComparison.OrdinalIgnoreCase))
        {
            configuredPort = uri.Host;
        }

        return !string.IsNullOrWhiteSpace(configuredPort) &&
               configuredPort.Equals(portName, StringComparison.OrdinalIgnoreCase);
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnConnectionsChanged(sender, e));
            return;
        }

        if (_selectedConnectionId is not null)
        {
            var replacement = Connections.FirstOrDefault(item => item.Id == _selectedConnectionId);
            if (replacement is not null && !ReferenceEquals(replacement, SelectedConnection))
            {
                _replacingLiveSelection = true;
                try
                {
                    SelectedConnection = replacement;
                }
                finally
                {
                    _replacingLiveSelection = false;
                }
            }
        }
        RefreshDetails();
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsConnectionActive));
        OnPropertyChanged(nameof(ActionButtonText));
        OnPropertyChanged(nameof(IsGhostConnection));
        OnPropertyChanged(nameof(IsEditableConnection));
        OnPropertyChanged(nameof(IsMavlinkConnection));
        OnPropertyChanged(nameof(IsLinkdConnection));
        OnPropertyChanged(nameof(IsSerialMavlink));
        OnPropertyChanged(nameof(IsUdpMavlink));
        OnPropertyChanged(nameof(UsesLogosCredentials));
        OnPropertyChanged(nameof(ShowConnectionControls));
        OnPropertyChanged(nameof(ConnectionKind));
        RaiseCommandStates();
    }

    private void OnLiveDataChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnLiveDataChanged(sender, e));
            return;
        }

        RefreshDetails();
    }

    private void RefreshDetails()
    {
        Links.Clear();
        LinkDetails.Clear();
        Events.Clear();
        if (SelectedConnection is not null)
        {
            foreach (var link in _linkStore.Items.Where(item => item.ConnectionId == SelectedConnection.Id)) Links.Add(link);
            foreach (var item in _eventStore.Items.Where(item => item.ConnectionId == SelectedConnection.Id).OrderByDescending(item => item.Timestamp)) Events.Add(item);
        }
        foreach (var link in Links)
        {
            LinkDetails.Add($"{link.Name}: RSSI {FormatMetric(link.RssiDbm, " dBm")}; SNR {FormatMetric(link.SnrDb, " dB")}; quality {FormatMetric(link.Quality)}; packet loss {FormatMetric(link.PacketLoss)}; latency {FormatMetric(link.LatencyMilliseconds, " ms")}. {link.Message}");
        }
        EventLines.Clear();
        foreach (var item in Events) EventLines.Add($"{item.Timestamp.ToLocalTime():G}  {item.Severity}  {item.Source}: {item.Message}");
        OnPropertyChanged(nameof(Protocol)); OnPropertyChanged(nameof(BaudRate)); OnPropertyChanged(nameof(ConnectedAt));
        OnPropertyChanged(nameof(IsDisconnected)); OnPropertyChanged(nameof(IsConnectionActive)); OnPropertyChanged(nameof(ActionButtonText));
        OnPropertyChanged(nameof(IsGhostConnection)); OnPropertyChanged(nameof(ShowConnectionControls)); OnPropertyChanged(nameof(ConnectionKind));
        OnPropertyChanged(nameof(LastSeen)); OnPropertyChanged(nameof(LastAttempt)); OnPropertyChanged(nameof(LastConnectedAt)); OnPropertyChanged(nameof(SignalSummary)); OnPropertyChanged(nameof(SdkCallSummary));
        RefreshParameterTargets();
        OnPropertyChanged(nameof(IsPx4ParameterPanel));
        OnPropertyChanged(nameof(IsArduPilotParameterPanel));
        OnPropertyChanged(nameof(IsMavlinkParameterPanel));
        OnPropertyChanged(nameof(ParameterBackendName));
        OnPropertyChanged(nameof(ParameterPanelTitle));
        OnPropertyChanged(nameof(ParameterBackendUnit));
        ReplaceTiles();
    }

    private void RefreshParameterTargets()
    {
        var previousId = SelectedParameterTarget?.VehicleId;
        ParameterTargets.Clear();
        if (IsMavlinkParameterPanel && SelectedConnection is not null)
        {
            foreach (var vehicle in _vehicleStore.Items
                         .Where(item => !item.IsGhost && item.ConnectionIds.Contains(SelectedConnection.Id, StringComparer.Ordinal)))
            {
                ParameterTargets.Add(new Px4ParameterTarget(vehicle.Id, vehicle.Name));
            }
        }

        SelectedParameterTarget = ParameterTargets.FirstOrDefault(item => item.VehicleId == previousId) ?? ParameterTargets.FirstOrDefault();
        if (SelectedParameterTarget is null)
        {
            ParameterDiffs.Clear();
            ParameterStatus = IsMavlinkParameterPanel
                ? $"No {ParameterBackendName} MAVLink system has been discovered on this connection."
                : "Select a PX4 or ArduPilot MAVLink connection to manage parameter profiles.";
        }
    }

    private async Task RefreshParameterProfilesAsync()
    {
        if (_parameterProfiles is null) return;
        try
        {
            await _parameterProfiles.RefreshAsync();
            ReplaceParameterProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load MAVLink parameter profiles");
            ParameterStatus = $"Could not load MAVLink parameter profiles: {ex.Message}";
        }
    }

    private void ReplaceParameterProfiles()
    {
        if (_parameterProfiles is null) return;
        var selectedId = SelectedParameterProfile?.Id;
        ParameterProfiles.Clear();
        foreach (var profile in _parameterProfiles.Profiles.OrderByDescending(item => item.CreatedAt)) ParameterProfiles.Add(profile);
        SelectedParameterProfile = ParameterProfiles.FirstOrDefault(item => item.Id == selectedId) ?? ParameterProfiles.FirstOrDefault();
    }

    private bool CanManageParameters()
        => !IsParameterBusy && IsMavlinkParameterPanel && SelectedConnection is not null && SelectedParameterTarget is not null && _parameterService is not null;

    private bool CanCompareParameters()
        => CanManageParameters() && SelectedParameterProfile is not null;

    private bool CanApplyParameters()
        => CanCompareParameters() && ParameterDiffs.Any(item => item.Kind == Px4ParameterChangeKind.Changed);

    private async Task DownloadParametersAsync(CancellationToken cancellationToken)
    {
        if (!CanManageParameters() || SelectedConnection is null || SelectedParameterTarget is null || _parameterService is null || _parameterProfiles is null) return;
        IsParameterBusy = true;
        try
        {
            ParameterStatus = $"Downloading {ParameterBackendName} parameters for {SelectedParameterTarget.Name}...";
            var document = await _parameterService.DownloadAsync(SelectedConnection.Id, SelectedParameterTarget.VehicleId, cancellationToken);
            var profile = await _parameterProfiles.SaveAsync(
                $"{SelectedParameterTarget.Name}-download",
                document,
                cancellationToken: cancellationToken);
            await _parameterProfiles.RefreshAsync(cancellationToken);
            ReplaceParameterProfiles();
            SelectedParameterProfile = ParameterProfiles.FirstOrDefault(item => item.Id == profile.Id) ?? profile;
            ParameterStatus = $"Downloaded and saved {document.Parameters.Count} {ParameterBackendName} parameter(s) as {profile.Name}.";
            await CompareParametersCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ParameterStatus = $"{ParameterBackendName} parameter download failed: {ex.Message}";
            _logger.LogWarning(ex, "{Backend} parameter download failed", ParameterBackendName);
        }
        finally { IsParameterBusy = false; RaiseCommandStates(); }
    }

    private async Task CompareParametersAsync(CancellationToken cancellationToken)
    {
        if (!CanCompareParameters() || SelectedConnection is null || SelectedParameterTarget is null || SelectedParameterProfile is null || _parameterService is null) return;
        IsParameterBusy = true;
        try
        {
            await CompareParametersCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ParameterStatus = $"{ParameterBackendName} parameter comparison failed: {ex.Message}";
            _logger.LogWarning(ex, "{Backend} parameter comparison failed", ParameterBackendName);
        }
        finally { IsParameterBusy = false; RaiseCommandStates(); }
    }

    private async Task CompareParametersCoreAsync(CancellationToken cancellationToken)
    {
        if (SelectedConnection is null || SelectedParameterTarget is null || SelectedParameterProfile is null || _parameterService is null) return;
        ParameterStatus = $"Comparing {SelectedParameterProfile.Name} with {SelectedParameterTarget.Name}...";
        var diffs = await _parameterService.CompareAsync(SelectedConnection.Id, SelectedParameterTarget.VehicleId, SelectedParameterProfile, cancellationToken);
        ParameterDiffs.Clear();
        foreach (var diff in diffs) ParameterDiffs.Add(diff);
        ParameterStatus = $"Compared {diffs.Count} profile parameter(s): {diffs.Count(item => item.Kind == Px4ParameterChangeKind.Changed)} change(s).";
    }

    private async Task ApplyParametersAsync(CancellationToken cancellationToken)
    {
        if (!CanApplyParameters() || SelectedConnection is null || SelectedParameterTarget is null || SelectedParameterProfile is null || _parameterService is null) return;
        IsParameterBusy = true;
        try
        {
            ParameterStatus = $"Applying {SelectedParameterProfile.Name} to {SelectedParameterTarget.Name}; the vehicle must remain disarmed...";
            var result = await _parameterService.ApplyAsync(SelectedConnection.Id, SelectedParameterTarget.VehicleId, SelectedParameterProfile, cancellationToken);
            ParameterStatus = result.BackupProfile is { } backup
                ? $"{result.Applied} parameter(s) applied, {result.Failed} failed. Automatic backup: {backup.Name}."
                : string.Join(" ", result.Messages);
            foreach (var message in result.Messages) AddLog(message);
            await CompareParametersCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ParameterStatus = $"{ParameterBackendName} parameter apply failed: {ex.Message}";
            _logger.LogWarning(ex, "{Backend} parameter apply failed", ParameterBackendName);
        }
        finally { IsParameterBusy = false; RaiseCommandStates(); }
    }

    public async Task ImportParameterProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_parameterProfiles is null) return;
        try
        {
            var profile = await _parameterProfiles.ImportAsync(path, cancellationToken: cancellationToken);
            ReplaceParameterProfiles();
            SelectedParameterProfile = ParameterProfiles.FirstOrDefault(item => item.Id == profile.Id) ?? profile;
            ParameterStatus = $"Imported {profile.Name}. Review and compare it before applying.";
        }
        catch (Exception ex)
        {
            ParameterStatus = $"{ParameterBackendName} parameter import failed: {ex.Message}";
            _logger.LogWarning(ex, "{Backend} parameter import failed", ParameterBackendName);
        }
    }

    public async Task ExportSelectedParameterProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_parameterProfiles is null || SelectedParameterProfile is null) return;
        try
        {
            await _parameterProfiles.ExportAsync(SelectedParameterProfile.Id, path, cancellationToken);
            ParameterStatus = $"Exported {SelectedParameterProfile.Name}.";
        }
        catch (Exception ex)
        {
            ParameterStatus = $"{ParameterBackendName} parameter export failed: {ex.Message}";
            _logger.LogWarning(ex, "{Backend} parameter export failed", ParameterBackendName);
        }
    }

    private void ReplaceTiles()
    {
        Tiles.Clear();
        var connection = SelectedConnection;
        var state = connection?.State;
        Tiles.Add(new ConnectionMetric("State", state?.ToString() ?? "-", connection?.LastError ?? "", StateColor(state)));
        Tiles.Add(new ConnectionMetric("Protocol", Protocol, connection?.Target ?? ""));
        Tiles.Add(new ConnectionMetric("Connected", ConnectedAt));
        Tiles.Add(new ConnectionMetric("Links", Links.Count.ToString()));
        Tiles.Add(new ConnectionMetric("Events", Events.Count.ToString()));
    }

    private static IReadOnlyList<ConnectionMetric> EmptyTiles() => [new("State", "-"), new("Protocol", "-"), new("Connected", "-"), new("Links", "-"), new("Events", "-")];
    private static string StateColor(AvailabilityState? state) => state switch
    {
        AvailabilityState.Online => "#4ADE80",
        AvailabilityState.Offline => "#9CA3AF",
        AvailabilityState.Connecting or AvailabilityState.Reconnecting => "#FACC15",
        AvailabilityState.Faulted => "#F87171",
        AvailabilityState.Degraded => "#FB923C",
        AvailabilityState.Stale => "#FFFFFF",
        _ => "#FFFFFF"
    };
    private void RaiseCommandStates()
    {
        foreach (var command in new[] { AddCommand, SaveCommand, DeleteCommand, ConnectCommand, DisconnectCommand, RefreshCommand, ProbeRadioCommand, PairRadiosCommand, ApplyLocalRadioCommand, ApplyPairedRadioCommand, ConfirmRadioApplyCommand, DownloadParametersCommand, CompareParametersCommand, ApplyParametersCommand })
            if (command is AsyncRelayCommand async) async.RaiseCanExecuteChanged();
        foreach (var command in new[] { ToggleConnectionCommand, UseSerialDeviceCommand, ReviewLocalRadioCommand, ReviewPairedRadioCommand, CancelRadioApplyCommand })
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
    }
    private void Fail(string operation, Exception ex) { ActionStatus = $"{operation} failed: {ex.Message}"; _logger.LogWarning(ex, "{Operation} failed", operation); }
    private void AddLog(string message) { LogLines.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {message}"); while (LogLines.Count > 200) LogLines.RemoveAt(LogLines.Count - 1); }
    private static string FormatMetric(double? value, string suffix = "") => value is null ? "Not reported" : $"{value:0.##}{suffix}";
}

public sealed record Px4ParameterTarget(string VehicleId, string Name)
{
    public string DisplayName => $"{Name} ({VehicleId})";
}

public sealed class SikRadioSettingRow : ObservableObject
{
    public sealed record Descriptor(int Id, string Name, string Description, bool IsCommon);
    public static IReadOnlyList<Descriptor> Descriptors { get; } =
    [
        new(0, "Format", "EEPROM format/version", false),
        new(1, "Serial speed", "UART speed in kbaud", true),
        new(2, "Air speed", "Over-air data rate", true),
        new(3, "Network ID", "Pairing network identifier", true),
        new(4, "Transmit power", "Radio transmit power in dBm", true),
        new(5, "ECC", "Forward error correction", false),
        new(6, "MAVLink framing", "MAVLink framing and low-latency mode", false),
        new(7, "Opportunistic resend", "Use spare airtime for retransmission", false),
        new(8, "Minimum frequency", "Minimum frequency in kHz", true),
        new(9, "Maximum frequency", "Maximum frequency in kHz", true),
        new(10, "Channels", "Frequency hopping channel count", false),
        new(11, "Duty cycle", "Maximum transmit duty cycle percent", false),
        new(12, "LBT RSSI", "Listen-before-talk threshold", false),
        new(13, "Manchester", "Manchester encoding", false),
        new(14, "RTS/CTS", "Hardware flow control", false)
    ];

    private int _desiredLocalValue;
    private int? _desiredRemoteValue;

    public SikRadioSettingRow(int id, string name, string description, bool isCommon, int localValue, int? remoteValue)
    {
        Id = id;
        Name = name;
        Description = description;
        IsCommon = isCommon;
        LocalValue = localValue;
        RemoteValue = remoteValue;
        _desiredLocalValue = localValue;
        _desiredRemoteValue = remoteValue;
    }

    public int Id { get; }
    public string Parameter => $"S{Id}";
    public string Name { get; }
    public string Description { get; }
    public bool IsCommon { get; }
    public int LocalValue { get; }
    public int? RemoteValue { get; }
    public bool HasRemote => RemoteValue is not null;
    public bool CanEditLocal => Id != 0;
    public bool CanEditRemote => Id != 0 && HasRemote;
    public int DesiredLocalValue
    {
        get => _desiredLocalValue;
        set
        {
            if (!SetProperty(ref _desiredLocalValue, value)) return;
            OnPropertyChanged(nameof(HasLocalChange));
            OnPropertyChanged(nameof(IsValid));
        }
    }
    public int? DesiredRemoteValue
    {
        get => _desiredRemoteValue;
        set
        {
            if (!SetProperty(ref _desiredRemoteValue, value)) return;
            OnPropertyChanged(nameof(HasRemoteChange));
            OnPropertyChanged(nameof(IsValid));
        }
    }
    public bool HasLocalChange => DesiredLocalValue != LocalValue;
    public bool HasRemoteChange => RemoteValue is not null && DesiredRemoteValue != RemoteValue;
    public bool IsValid => IsValueValid(Id, DesiredLocalValue) &&
                           (DesiredRemoteValue is null || IsValueValid(Id, DesiredRemoteValue.Value));

    private static bool IsValueValid(int id, int value) => id switch
    {
        0 => value is >= 0 and <= 255,
        1 => value is >= 1 and <= 921,
        2 => value is >= 2 and <= 250,
        3 => value is >= 0 and <= 499,
        4 => value is >= 0 and <= 30,
        5 or 7 or 13 or 14 => value is >= 0 and <= 1,
        6 => value is >= 0 and <= 2,
        8 or 9 => value is >= 100000 and <= 1000000,
        10 => value is >= 1 and <= 50,
        11 => value is >= 0 and <= 100,
        12 => value is >= 0 and <= 255,
        _ => false
    };
}
