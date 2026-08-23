using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using QRCoder;
using RobotCommand.Core;
using RobotCommand.Infrastructure;
using RobotCommand.Localization;
using RobotCommand.Models;
using RobotCommand.Sdk;
using RobotCommand.Services.Team;

namespace RobotCommand.ViewModels;

public sealed class MyTeamViewModel : ObservableObject
{
    private readonly ILanTeamServer _server;
    private readonly ITeamServerWorkflow _workflow;
    private readonly ITeamAccessCoordinator _access;
    private readonly ITeamPairingService _pairing;
    private readonly ITeamPassphraseService _passphrase;
    private readonly ITeamServerSettingsService _settings;
    private readonly IRobotCommandSnapshotProjector _snapshots;
    private readonly IRobotCommandObserverService _observers;
    private readonly ILanTeamDiscoveryService _discovery;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalizationService _localization;
    private readonly ITeamObserverClientWorkflow? _observerWorkflow;
    private string _serverName;
    private string _port;
    private string _maximumClients;
    private bool _shareOperatorLocation;
    private string _remoteEndpoint = string.Empty;
    private string _remoteDisplayName = "Observer";
    private string _remoteFingerprint = string.Empty;
    private string _remoteStatus = string.Empty;
    private bool _fingerprintTrusted;
    private bool _requirePassphrase = true;
    private bool _remoteRequiresPassphrase;
    private string _serverPassphrase = TeamPassphraseService.CreateSuggestedPassphrase();
    private string _remotePassphrase = string.Empty;
    private string _pairingLink = string.Empty;
    private string _remotePairingLink = string.Empty;
    private RobotCommandPairingInvitation? _remotePairing;
    private Bitmap? _pairingQrCode;
    private TeamConnectedClientRecord? _selectedConnectedClient;

    public MyTeamViewModel(
        ILanTeamServer server,
        ITeamServerWorkflow workflow,
        ITeamAccessCoordinator access,
        ITeamPairingService pairing,
        ITeamPassphraseService passphrase,
        ITeamServerSettingsService settings,
        IRobotCommandSnapshotProjector snapshots,
        IRobotCommandObserverService observers,
        ILanTeamDiscoveryService discovery,
        IUiDispatcher dispatcher,
        ILocalizationService localization,
        ITeamObserverClientWorkflow? observerWorkflow = null)
    {
        _server = server;
        _workflow = workflow;
        _access = access;
        _pairing = pairing;
        _passphrase = passphrase;
        _settings = settings;
        _snapshots = snapshots;
        _observers = observers;
        _discovery = discovery;
        _dispatcher = dispatcher;
        _localization = localization;
        _observerWorkflow = observerWorkflow;
        _serverName = settings.Current.DisplayName;
        _port = settings.Current.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _maximumClients = settings.Current.MaximumClients.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _requirePassphrase = settings.Current.RequirePassphrase;
        PendingRequests = [];
        ConnectedClients = [];
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);
        ApproveCommand = new RelayCommand(value => { if (value is TeamAccessRequestRecord item) _workflow.Approve(item.Id); });
        RejectCommand = new RelayCommand(value => { if (value is TeamAccessRequestRecord item) _workflow.Reject(item.Id); });
        DisconnectCommand = new RelayCommand(value => { if (value is TeamConnectedClientRecord item) _workflow.Disconnect(item.SessionId); });
        ClearSelectedConnectedClientCommand = new RelayCommand(_ => ClearSelectedConnectedClient());
        ProbeRemoteCommand = new AsyncRelayCommand(ProbeRemoteAsync, () => !string.IsNullOrWhiteSpace(RemoteEndpoint));
        ObserveRemoteCommand = new AsyncRelayCommand(ObserveRemoteAsync, () => !string.IsNullOrWhiteSpace(RemoteEndpoint));
        DisconnectRemoteCommand = new AsyncRelayCommand(DisconnectRemoteAsync, () => SelectedRemoteObserver is not null);
        RefreshNearbyCommand = new AsyncRelayCommand(cancellationToken => _discovery.RefreshAsync(cancellationToken));
        CreatePairingCommand = new RelayCommand(_ => CreatePairing(), _ => IsRunning);
        RegeneratePassphraseCommand = new RelayCommand(_ => ServerPassphrase = TeamPassphraseService.CreateSuggestedPassphrase(), _ => IsStopped);
        EnableAuthenticationCommand = new AsyncRelayCommand(cancellationToken => SetAuthenticationAsync(true, false, cancellationToken), () => IsRunning && !IsAuthenticationEnabled);
        EnableAuthenticationAndReauthenticateCommand = new AsyncRelayCommand(cancellationToken => SetAuthenticationAsync(true, true, cancellationToken), () => IsRunning && !IsAuthenticationEnabled);
        DisableAuthenticationCommand = new AsyncRelayCommand(cancellationToken => SetAuthenticationAsync(false, false, cancellationToken), () => IsRunning && IsAuthenticationEnabled);
        RequireReauthenticationCommand = new AsyncRelayCommand(cancellationToken => SetAuthenticationAsync(true, true, cancellationToken), () => IsRunning && IsAuthenticationEnabled);
        UsePairingLinkCommand = new AsyncRelayCommand(UsePairingLinkAsync, () => !string.IsNullOrWhiteSpace(RemotePairingLink));
        _server.Changed += OnStateChanged;
        _workflow.Changed += OnStateChanged;
        _access.Changed += OnStateChanged;
        _pairing.Changed += OnStateChanged;
        _observers.Changed += OnStateChanged;
        _discovery.Changed += OnStateChanged;
        if (_observerWorkflow is not null) _observerWorkflow.Changed += OnStateChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshLists();
    }

    public ObservableCollection<TeamAccessRequestRecord> PendingRequests { get; }
    public ObservableCollection<TeamConnectedClientRecord> ConnectedClients { get; }
    public ObservableCollection<RobotCommandObserverRecord> RemoteObservers { get; } = [];
    public ObservableCollection<DiscoveredRobotCommandServer> NearbyServers { get; } = [];
    public RobotCommandObserverRecord? SelectedRemoteObserver { get; set; }
    public TeamConnectedClientRecord? SelectedConnectedClient
    {
        get => _selectedConnectedClient;
        set
        {
            if (!SetProperty(ref _selectedConnectedClient, value)) return;
            OnPropertyChanged(nameof(HasSelectedConnectedClient));
        }
    }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ProbeRemoteCommand { get; }
    public ICommand ObserveRemoteCommand { get; }
    public ICommand DisconnectRemoteCommand { get; }
    public ICommand RefreshNearbyCommand { get; }
    public ICommand CreatePairingCommand { get; }
    public ICommand UsePairingLinkCommand { get; }
    public ICommand RegeneratePassphraseCommand { get; }
    public ICommand EnableAuthenticationCommand { get; }
    public ICommand EnableAuthenticationAndReauthenticateCommand { get; }
    public ICommand DisableAuthenticationCommand { get; }
    public ICommand RequireReauthenticationCommand { get; }
    public ICommand ClearSelectedConnectedClientCommand { get; }
    public string RemoteEndpoint
    {
        get => _remoteEndpoint;
        set
        {
            if (!SetProperty(ref _remoteEndpoint, value)) return;
            _remotePairing = null;
            (ProbeRemoteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string RemoteDisplayName { get => _remoteDisplayName; set => SetProperty(ref _remoteDisplayName, value); }
    public string RemotePassphrase
    {
        get => _remotePassphrase;
        set => SetProperty(ref _remotePassphrase, value?.ToLowerInvariant() ?? string.Empty);
    }

    public string ServerPassphrase
    {
        get => _serverPassphrase;
        set => SetProperty(ref _serverPassphrase, value?.ToLowerInvariant() ?? string.Empty);
    }
    public bool RequirePassphrase { get => _requirePassphrase; set => SetProperty(ref _requirePassphrase, value); }
    public bool IsAuthenticationEnabled => _passphrase.IsConfigured && !_passphrase.IsOpenAccess;
    public bool RemoteRequiresPassphrase { get => _remoteRequiresPassphrase; private set => SetProperty(ref _remoteRequiresPassphrase, value); }
    public string RemoteFingerprint { get => _remoteFingerprint; private set { if (SetProperty(ref _remoteFingerprint, value)) (ObserveRemoteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string RemoteStatus { get => _remoteStatus; private set => SetProperty(ref _remoteStatus, value); }
    public bool FingerprintTrusted { get => _fingerprintTrusted; set { if (SetProperty(ref _fingerprintTrusted, value)) (ObserveRemoteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public string PairingLink { get => _pairingLink; private set => SetProperty(ref _pairingLink, value); }
    public string RemotePairingLink { get => _remotePairingLink; set { if (SetProperty(ref _remotePairingLink, value)) (UsePairingLinkCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public Bitmap? PairingQrCode { get => _pairingQrCode; private set => SetProperty(ref _pairingQrCode, value); }
    public string PairingShortCode => _pairing.CurrentInvitation?.ShortCode ?? string.Empty;
    public string PairingExpires => _pairing.CurrentInvitation is { } invitation
        ? $"Expires {invitation.ExpiresAt.LocalDateTime:t}"
        : string.Empty;
    public bool HasPairingInvitation => _pairing.CurrentInvitation is not null;

    public string Title => L("MyTeam");
    public bool IsRunning => _server.IsRunning;
    public bool IsStopped => !IsRunning;
    public string Status => L($"MyTeamStatus{_server.Status}");
    public string Endpoint => _server.Endpoint;
    public string CertificateFingerprint => FormatFingerprint(_server.CertificateFingerprint);
    public string Error => _server.LastError ?? string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasPendingRequests => PendingRequests.Count > 0;
    public bool HasConnectedClients => ConnectedClients.Count > 0;
    public bool HasSelectedConnectedClient => SelectedConnectedClient is not null;
    public bool HasNearbyServers => NearbyServers.Count > 0;
    public string DiscoveryStatus => LocalizeDiscoveryStatus(_discovery.Status);
    public string FirewallNotice => IsRunning ? L("MyTeamFirewallNotice") : string.Empty;

    public string ServerName
    {
        get => _serverName;
        set => SetProperty(ref _serverName, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string MaximumClients
    {
        get => _maximumClients;
        set => SetProperty(ref _maximumClients, value);
    }

    public bool ShareOperatorLocation
    {
        get => _shareOperatorLocation;
        set
        {
            if (!SetProperty(ref _shareOperatorLocation, value)) return;
            _snapshots.ShareOperatorLocation = value;
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(Port, out var port) || port is < 1024 or > 65535)
        {
            Port = "7443";
            port = 7443;
        }
        if (!int.TryParse(MaximumClients, out var maximumClients)) maximumClients = 8;
        if (RequirePassphrase && string.IsNullOrWhiteSpace(ServerPassphrase))
        {
            RemoteStatus = L("MyTeamPassphraseRequired");
            return;
        }
        await _workflow.StartAsync(new TeamServerStartRequest(
            ServerName,
            port,
            maximumClients,
            OpenAccess: !RequirePassphrase,
            Passphrase: RequirePassphrase ? ServerPassphrase : null), cancellationToken);
        if (_server.IsRunning) CreatePairing();
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        await _workflow.StopAsync(cancellationToken);
        PairingQrCode?.Dispose();
        PairingQrCode = null;
        PairingLink = string.Empty;
        ServerPassphrase = TeamPassphraseService.CreateSuggestedPassphrase();
        RemoteStatus = L("MyTeamStopped");
        OnPropertyChanged(nameof(HasPairingInvitation));
    }

    private async Task SetAuthenticationAsync(bool enabled, bool reauthenticate, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(ServerPassphrase))
            {
                RemoteStatus = L("MyTeamEnterPassphrase");
                return;
            }

            await _workflow.SetAuthenticationAsync(true, reauthenticate, ServerPassphrase, cancellationToken);
            RequirePassphrase = true;
            RemoteStatus = L("MyTeamAuthenticationEnabled");
        }
        else
        {
            await _workflow.SetAuthenticationAsync(false, false, cancellationToken: cancellationToken);
            RequirePassphrase = false;
            RemoteStatus = L("MyTeamAuthenticationDisabled");
        }
        Refresh();
    }

    private int ParsePort()
        => int.TryParse(Port, out var value) ? Math.Clamp(value, 1024, 65535) : 7443;

    private int ParseMaximumClients()
        => int.TryParse(MaximumClients, out var value) ? Math.Clamp(value, 1, 64) : 8;

    private async Task ProbeRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            RemoteStatus = L("MyTeamChecking");
            if (_observerWorkflow is not null)
            {
                await _observerWorkflow.ProbeAsync(RemoteEndpoint, cancellationToken);
            }
            var probe = await _observers.ProbeAsync(RemoteEndpoint, cancellationToken);
            RemoteEndpoint = probe.Endpoint.ToString();
            _remotePairing = null;
            RemoteFingerprint = probe.ObservedCertificateFingerprint;
            FingerprintTrusted = false;
            RemoteStatus = $"Found {probe.ServerInfo.DisplayName}.";
        }
        catch (Exception ex) { RemoteStatus = ex.Message; }
    }

    private async Task ObserveRemoteAsync(CancellationToken cancellationToken)
    {
        try
        {
            RemoteStatus = L("MyTeamConnecting");
            var probe = await _observers.ProbeAsync(RemoteEndpoint, cancellationToken);
            RemoteRequiresPassphrase = probe.ServerInfo.RequiresPassphrase;
            if (RemoteRequiresPassphrase && string.IsNullOrWhiteSpace(RemotePassphrase))
            {
                RemoteStatus = L("MyTeamEnterPassphrase");
                return;
            }
            if (_observerWorkflow is not null && _remotePairing is null)
                await _observerWorkflow.ConnectAsync(probe.Endpoint.ToString(), RemoteDisplayName, RemotePassphrase, cancellationToken);
            else
                await _observers.ConnectAsync(probe.Endpoint.ToString(), probe.ObservedCertificateFingerprint, RemoteDisplayName, RemotePassphrase, _remotePairing, cancellationToken);
            RemoteFingerprint = probe.ObservedCertificateFingerprint;
            FingerprintTrusted = true;
            RemoteRequiresPassphrase = false;
            RemoteStatus = L("MyTeamConnected");
        }
        catch (Exception ex) { RemoteStatus = ex.Message; }
    }

    private async Task DisconnectRemoteAsync(CancellationToken cancellationToken)
    {
        if (SelectedRemoteObserver is null) return;
        if (_observerWorkflow is not null) await _observerWorkflow.DisconnectAsync(SelectedRemoteObserver.Id, null, cancellationToken);
        else await _observers.DisconnectAsync(SelectedRemoteObserver.Id, cancellationToken);
        SelectedRemoteObserver = null;
        (DisconnectRemoteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void SelectRemoteObserver(RobotCommandObserverRecord? observer)
    {
        SelectedRemoteObserver = observer;
        OnPropertyChanged(nameof(SelectedRemoteObserver));
        (DisconnectRemoteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public void UseNearbyServer(DiscoveredRobotCommandServer? server)
    {
        if (server is null) return;
        RemoteEndpoint = server.Endpoint.ToString().TrimEnd('/');
        _remotePairing = null;
        RemoteRequiresPassphrase = false;
        RemoteFingerprint = string.Empty;
        FingerprintTrusted = false;
        RemoteStatus = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            L("MyTeamNearbySelected"),
            server.DisplayName);
    }

    private void CreatePairing()
    {
        try
        {
            var invitation = _pairing.Create(new Uri(_server.Endpoint), _server.CertificateFingerprint, RequirePassphrase ? ServerPassphrase : string.Empty);
            PairingLink = invitation.ToUri().ToString();
            PairingQrCode?.Dispose();
            PairingQrCode = CreateQrCode(PairingLink);
            RemoteStatus = "Pairing ready.";
            OnPropertyChanged(nameof(PairingShortCode));
            OnPropertyChanged(nameof(PairingExpires));
            OnPropertyChanged(nameof(HasPairingInvitation));
        }
        catch (Exception exception)
        {
            RemoteStatus = exception.Message;
        }
    }

    private async Task UsePairingLinkAsync(CancellationToken cancellationToken)
    {
        try
        {
            var invitation = RobotCommandPairingInvitation.Parse(RemotePairingLink);
            RemoteEndpoint = invitation.Endpoint.ToString().TrimEnd('/');
            RemoteFingerprint = invitation.CertificateFingerprint;
            RemotePassphrase = invitation.Passphrase;
            _remotePairing = invitation;
            FingerprintTrusted = true;
            await ObserveRemoteAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _remotePairing = null;
            FingerprintTrusted = false;
            RemoteStatus = exception.Message;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
        => _ = _dispatcher.InvokeAsync(Refresh);

    private void Refresh()
    {
        RefreshLists();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(CertificateFingerprint));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(FirewallNotice));
        OnPropertyChanged(nameof(DiscoveryStatus));
        OnPropertyChanged(nameof(PairingShortCode));
        OnPropertyChanged(nameof(PairingExpires));
        OnPropertyChanged(nameof(HasPairingInvitation));
        OnPropertyChanged(nameof(IsAuthenticationEnabled));
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreatePairingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RegeneratePassphraseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (EnableAuthenticationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (EnableAuthenticationAndReauthenticateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisableAuthenticationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RequireReauthenticationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RefreshLists()
    {
        var selectedSessionId = SelectedConnectedClient?.SessionId;
        PendingRequests.Clear();
        foreach (var item in _access.PendingRequests) PendingRequests.Add(item);
        ConnectedClients.Clear();
        foreach (var item in _access.ConnectedClients) ConnectedClients.Add(item);
        SelectedConnectedClient = selectedSessionId is null
            ? null
            : ConnectedClients.FirstOrDefault(item => string.Equals(item.SessionId, selectedSessionId, StringComparison.Ordinal));
        RemoteObservers.Clear();
        foreach (var item in _observers.Observers) RemoteObservers.Add(item);
        var authenticationRequired = _observers.Observers
            .Where(item => string.Equals(item.DisconnectReason, "AuthenticationRequired", StringComparison.Ordinal))
            .OrderByDescending(item => item.ConnectedAt)
            .FirstOrDefault(item => !_observers.Observers.Any(other =>
                other.Id != item.Id &&
                string.Equals(other.Endpoint, item.Endpoint, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.Status, "Observing", StringComparison.OrdinalIgnoreCase)));
        if (authenticationRequired is not null && !RemoteRequiresPassphrase)
        {
            RemoteEndpoint = authenticationRequired.Endpoint;
            RemoteDisplayName = authenticationRequired.DisplayName;
            RemotePassphrase = string.Empty;
            RemoteRequiresPassphrase = true;
            RemoteStatus = L("MyTeamEnterPassphrase");
        }
        NearbyServers.Clear();
        foreach (var item in _discovery.Servers) NearbyServers.Add(item);
        OnPropertyChanged(nameof(HasPendingRequests));
        OnPropertyChanged(nameof(HasConnectedClients));
        OnPropertyChanged(nameof(HasSelectedConnectedClient));
        OnPropertyChanged(nameof(HasNearbyServers));
        OnPropertyChanged(nameof(SelectedRemoteObserver));
        (DisconnectRemoteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ClearSelectedConnectedClient()
        => SelectedConnectedClient = null;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(FirewallNotice));
        OnPropertyChanged(nameof(DiscoveryStatus));
    }

    private string L(string key) => _localization.Get(key);
    private string LocalizeDiscoveryStatus(string value)
        => value switch
        {
            "Ready" => L("MyTeamReady"),
            "None found." => L("MyTeamNoneFound"),
            _ => value
        };
    private static string FormatFingerprint(string value)
        => string.Join(":", Enumerable.Range(0, value.Length / 2).Select(index => value.Substring(index * 2, 2)));

    private static Bitmap CreateQrCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(8);
        return new Bitmap(new MemoryStream(bytes));
    }
}
