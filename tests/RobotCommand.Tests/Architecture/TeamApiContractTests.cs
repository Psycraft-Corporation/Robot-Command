using System.Net;
using Google.Protobuf.Reflection;
using Makaretu.Dns;
using RobotCommand.Models;
using RobotCommand.Sdk;
using RobotCommand.Sdk.Team.V1;
using RobotCommand.Services.Team;
using Xunit;

namespace RobotCommand.Tests;

public sealed class TeamApiContractTests
{
    [Fact]
    public void PublicApi_IsVersionedAndReadOnly()
    {
        var descriptor = TeamReflection.Descriptor;
        Assert.Equal("psycraft.logos.robotcommand.team.v1", descriptor.Package);

        var services = descriptor.Services.ToDictionary(service => service.Name, StringComparer.Ordinal);
        Assert.Equal(["AccessService", "ObserverService", "ServerInfoService"], services.Keys.Order().ToArray());
        Assert.Equal(["GetServerInfo"], services["ServerInfoService"].Methods.Select(method => method.Name).ToArray());
        Assert.Equal(["RequestAccess"], services["AccessService"].Methods.Select(method => method.Name).ToArray());
        Assert.Equal(["GetSnapshot", "WatchSnapshots"], services["ObserverService"].Methods.Select(method => method.Name).ToArray());
        Assert.Contains("ObserverDisconnectNotice", descriptor.MessageTypes.Select(message => message.Name));

        var forbidden = new[] { "Command", "Control", "Upload", "Download", "File", "Video", "Delete", "Set" };
        Assert.DoesNotContain(
            descriptor.Services.SelectMany(service => service.Methods).Select(method => method.Name),
            name => forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.4.5.6", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.20.4", true)]
    [InlineData("169.254.10.2", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    public void LanPolicy_AllowsOnlyConfiguredLocalAddressClasses(string address, bool expected)
        => Assert.Equal(expected, LanAddressPolicy.IsAllowed(IPAddress.Parse(address)));

    [Fact]
    public void Proto_DoesNotExposeSecretsOrLocalFiles()
    {
        var fields = AllMessages(TeamReflection.Descriptor)
            .SelectMany(message => message.Fields.InDeclarationOrder())
            .Select(field => field.Name)
            .ToArray();

        Assert.DoesNotContain(fields, field => field.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => field.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => field.Contains("bearer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => field.Contains("local_path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, field => field.Contains("video", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SdkProject_IsPackableAndIncludesPublicProto()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "sdk", "RobotCommand.Sdk", "RobotCommand.Sdk.csproj"));
        Assert.Contains("<PackageId>RobotCommand.Sdk</PackageId>", project, StringComparison.Ordinal);
        Assert.Contains("<Version>0.1.0-alpha.3</Version>", project, StringComparison.Ordinal);
        Assert.Contains("PackagePath=\"contentFiles/any/any/protos/", project, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamSnapshotProjection_DoesNotSynchronouslyCaptureTheUiDuringHostStartup()
    {
        var root = FindRepositoryRoot();
        var projector = File.ReadAllText(Path.Combine(
            root,
            "src",
            "runtime",
            "RobotCommand.Runtime",
            "Services",
            "Team",
            "RobotCommandSnapshotProjector.cs"));
        var application = File.ReadAllText(Path.Combine(
            root,
            "src",
            "app",
            "RobotCommand",
            "App.axaml.cs"));

        Assert.Contains("public Task StartAsync(CancellationToken cancellationToken)", projector, StringComparison.Ordinal);
        Assert.Contains("ScheduleRefresh();", projector, StringComparison.Ordinal);
        Assert.DoesNotContain("StartAsync(CancellationToken cancellationToken) => RefreshAsync", projector, StringComparison.Ordinal);
        Assert.Contains("private async void OnMainWindowOpened", application, StringComparison.Ordinal);
        Assert.Contains("await host.StartAsync();", application, StringComparison.Ordinal);
        Assert.Contains("await host.StopAsync(stopCancellation.Token)", application, StringComparison.Ordinal);
        Assert.DoesNotContain("host.StopAsync(stopCancellation.Token).GetAwaiter().GetResult()", application, StringComparison.Ordinal);
    }

    private static IEnumerable<MessageDescriptor> AllMessages(FileDescriptor file)
    {
        foreach (var message in file.MessageTypes)
        {
            yield return message;
            foreach (var nested in AllMessages(message)) yield return nested;
        }
    }

    private static IEnumerable<MessageDescriptor> AllMessages(MessageDescriptor message)
    {
        foreach (var nested in message.NestedTypes)
        {
            yield return nested;
            foreach (var descendant in AllMessages(nested)) yield return descendant;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

public sealed class TeamServerSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"robot-command-team-{Guid.NewGuid():N}");

    [Fact]
    public void MissingConfiguration_UsesSafeDefaults()
    {
        Directory.CreateDirectory(_directory);
        var service = new TeamServerSettingsService(_directory);
        Assert.Equal(7443, service.Current.Port);
        Assert.Equal(8, service.Current.MaximumClients);
        Assert.True(service.Current.RequirePassphrase);
        Assert.False(string.IsNullOrWhiteSpace(service.Current.DisplayName));
    }

    [Fact]
    public async Task Save_PreservesConnectionsAndUnrelatedConfiguration()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "appsettings.local.json"),
            """
            {
              "refreshSeconds": 2,
              "connections": [{ "name": "Local", "target": "http://localhost:19000" }],
              "ui": { "language": "fr" }
            }
            """, TestContext.Current.CancellationToken);
        var service = new TeamServerSettingsService(_directory);
        await service.SaveAsync(
            new TeamServerSettings("Flight Team", 8443, 12),
            TestContext.Current.CancellationToken);

        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(_directory, "appsettings.local.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal(2, document.RootElement.GetProperty("refreshSeconds").GetInt32());
        Assert.Equal("Local", document.RootElement.GetProperty("connections")[0].GetProperty("name").GetString());
        Assert.Equal("fr", document.RootElement.GetProperty("ui").GetProperty("language").GetString());
        Assert.Equal("Flight Team", document.RootElement.GetProperty("teamServer").GetProperty("displayName").GetString());
        Assert.Equal(8443, new TeamServerSettingsService(_directory).Current.Port);
    }

    [Fact]
    public async Task Save_PersistsOpenAccessPreference()
    {
        Directory.CreateDirectory(_directory);
        var service = new TeamServerSettingsService(_directory);
        await service.SaveAsync(new TeamServerSettings("Open Team", 7443, 8, RequirePassphrase: false), TestContext.Current.CancellationToken);

        var reloaded = new TeamServerSettingsService(_directory);
        Assert.False(reloaded.Current.RequirePassphrase);
        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_directory, "appsettings.local.json"), TestContext.Current.CancellationToken));
        Assert.False(document.RootElement.GetProperty("teamServer").GetProperty("requirePassphrase").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

public sealed class TeamAccessCoordinatorTests
{
    [Fact]
    public async Task ApprovalToken_IsSessionOnlyBoundToAddressAndRevocable()
    {
        var settings = new MemoryTeamSettings(new TeamServerSettings("Test", 7443, 2));
        using var coordinator = new TeamAccessCoordinator(settings, new TeamPairingService());
        var registration = coordinator.RequestAccess(
            "Observer", "Test client", "1.0", "0.1.0-alpha.2", "v1", "client-1", "nonce-1", "", "", "192.168.1.20");

        Assert.Single(coordinator.PendingRequests);
        Assert.True(coordinator.Approve(registration.Request.Id));
        var decision = await registration.Decision;
        Assert.Equal(TeamAccessRequestState.Approved, decision.State);
        Assert.False(string.IsNullOrWhiteSpace(decision.SessionToken));

        Assert.Throws<UnauthorizedAccessException>(() =>
            coordinator.BeginObserverSession(decision.SessionToken!, "192.168.1.21"));

        var second = coordinator.RequestAccess(
            "Observer", "Test client", "1.0", "0.1.0-alpha.2", "v1", "client-1", "nonce-2", "", "", "192.168.1.20");
        coordinator.Approve(second.Request.Id);
        var secondDecision = await second.Decision;
        var lease = coordinator.BeginObserverSession(secondDecision.SessionToken!, "192.168.1.20");
        Assert.True(coordinator.ValidateConnectedToken(secondDecision.SessionToken!, "192.168.1.20"));
        Assert.Single(coordinator.ConnectedClients);

        Assert.True(coordinator.Disconnect(lease.SessionId));
        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.ValidateConnectedToken(secondDecision.SessionToken!, "192.168.1.20"));
        Assert.Empty(coordinator.ConnectedClients);
    }

    [Fact]
    public async Task IncompatibleClient_IsRejectedWithoutPendingApproval()
    {
        using var coordinator = new TeamAccessCoordinator(
            new MemoryTeamSettings(new TeamServerSettings("Test", 7443, 8)), new TeamPairingService());
        var registration = coordinator.RequestAccess(
            "Observer", "Old client", "1.0", "0.0", "v0", "client", "nonce", "", "", "127.0.0.1");
        var decision = await registration.Decision;
        Assert.Equal(TeamAccessRequestState.Incompatible, decision.State);
        Assert.Empty(coordinator.PendingRequests);
    }

    [Fact]
    public async Task QrPairing_RequiresTheCurrentShortCodeAndIsConsumedOnApproval()
    {
        var settings = new MemoryTeamSettings(new TeamServerSettings("Test", 7443, 8));
        var pairing = new TeamPairingService();
        using var coordinator = new TeamAccessCoordinator(settings, pairing);
        var invitation = pairing.Create(new Uri("https://host.local:7443"), new string('A', 64));

        var rejected = coordinator.RequestAccess(
            "Observer", "Test client", "1.0", "0.1.0-alpha.2", "v1", "client-1", "nonce-1",
            invitation.PairingId, "000000", "192.168.1.20");
        Assert.Equal(TeamAccessRequestState.Rejected, (await rejected.Decision).State);

        var registration = coordinator.RequestAccess(
            "Observer", "Test client", "1.0", "0.1.0-alpha.2", "v1", "client-1", "nonce-2",
            invitation.PairingId, invitation.ShortCode, "192.168.1.20");
        var pending = Assert.Single(coordinator.PendingRequests);
        Assert.True(pending.PairingVerified);
        Assert.Equal(invitation.ShortCode, pending.PairingShortCode);
        Assert.True(coordinator.Approve(registration.Request.Id));
        Assert.Equal(TeamAccessRequestState.Approved, (await registration.Decision).State);
        Assert.Null(pairing.CurrentInvitation);
    }

    [Fact]
    public async Task SessionPassphrase_AuthenticatesWithoutManualFingerprintTrustAndRemainsAvailableForMoreObservers()
    {
        var settings = new MemoryTeamSettings(new TeamServerSettings("Test", 7443, 8));
        var passphrase = new TeamPassphraseService();
        passphrase.Configure("Repair-Spindle");
        using var coordinator = new TeamAccessCoordinator(settings, new TeamPairingService(), passphrase);

        var wrong = coordinator.RequestAccess("Observer", "Test", "1.0", "0.1.0-alpha.3", "v1", "client", "nonce-1", "", "", "Wrong-Phrase", "192.168.1.20");
        Assert.Equal(TeamAccessRequestState.Rejected, (await wrong.Decision).State);

        var requested = coordinator.RequestAccess("Observer", "Test", "1.0", "0.1.0-alpha.3", "v1", "client", "nonce-2", "", "", "repair spindle", "192.168.1.20");
        var pending = Assert.Single(coordinator.PendingRequests);
        Assert.True(pending.PassphraseVerified);
        Assert.Contains("passphrase verified", pending.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.Approve(requested.Request.Id));
        Assert.Equal(TeamAccessRequestState.Approved, (await requested.Decision).State);
        Assert.True(passphrase.IsConfigured);
    }

    [Fact]
    public async Task OpenAccess_AutoApprovesCompatibleObserversWithoutPassphrase()
    {
        var settings = new MemoryTeamSettings(new TeamServerSettings("Open", 7443, 8, RequirePassphrase: false));
        var passphrase = new TeamPassphraseService();
        passphrase.ConfigureOpenAccess();
        using var coordinator = new TeamAccessCoordinator(settings, new TeamPairingService(), passphrase);

        var registration = coordinator.RequestAccess(
            "Observer", "Test", "1.0", "0.1.0-alpha.3", "v1", "client", "nonce", "", "", "", "192.168.1.20");

        var decision = await registration.Decision;
        Assert.Equal(TeamAccessRequestState.Approved, decision.State);
        Assert.False(registration.Request.PassphraseVerified);
        Assert.Empty(coordinator.PendingRequests);
        Assert.False(string.IsNullOrWhiteSpace(decision.SessionToken));
    }

    [Fact]
    public async Task ManualDisconnect_SendsAReasonBeforeRevokingTheSession()
    {
        var settings = new MemoryTeamSettings(new TeamServerSettings("Test", 7443, 8));
        using var coordinator = new TeamAccessCoordinator(settings, new TeamPairingService());
        var request = coordinator.RequestAccess("Observer", "Test", "1.0", "0.1.0-alpha.3", "v1", "client", "nonce", "", "", "", "192.168.1.20");
        coordinator.Approve(request.Request.Id);
        var decision = await request.Decision;
        var lease = coordinator.BeginObserverSession(decision.SessionToken!, "192.168.1.20");

        Assert.True(coordinator.Disconnect(lease.SessionId));
        var notice = await lease.DisconnectNotifications.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TeamDisconnectReason.OperatorDisconnected, notice.Reason);
        Assert.Contains("operator", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(coordinator.ValidateConnectedToken(lease.Token, "192.168.1.20"));
    }

    [Fact]
    public async Task AuthenticationReauthentication_DisconnectsAllClientsWithAnExplicitReason()
    {
        var settings = new MemoryTeamSettings(new TeamServerSettings("Test", 7443, 8));
        var passphrase = new TeamPassphraseService();
        passphrase.Configure("Repair-Spindle");
        using var coordinator = new TeamAccessCoordinator(settings, new TeamPairingService(), passphrase);
        var request = coordinator.RequestAccess("Observer", "Test", "1.0", "0.1.0-alpha.3", "v1", "client", "nonce", "", "", "Repair-Spindle", "192.168.1.20");
        coordinator.Approve(request.Request.Id);
        var decision = await request.Decision;
        var lease = coordinator.BeginObserverSession(decision.SessionToken!, "192.168.1.20");

        Assert.Equal(1, coordinator.DisconnectAll(TeamDisconnectReason.AuthenticationRequired, "Authentication is required again."));
        var notice = await lease.DisconnectNotifications.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TeamDisconnectReason.AuthenticationRequired, notice.Reason);
        Assert.Contains("again", notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(coordinator.ConnectedClients);
    }

    [Fact]
    public void PairingInvitation_RoundTripsWithoutSessionCredentials()
    {
        var pairing = new TeamPairingService();
        var created = pairing.Create(new Uri("https://console.local:7443"), new string('B', 64), "Repair-Spindle");
        var parsed = RobotCommandPairingInvitation.Parse(created.ToUri().ToString());

        Assert.Equal(created.Endpoint, parsed.Endpoint);
        Assert.Equal(created.CertificateFingerprint, parsed.CertificateFingerprint);
        Assert.Equal(created.PairingId, parsed.PairingId);
        Assert.Equal(created.ShortCode, parsed.ShortCode);
        Assert.Equal("Repair-Spindle", parsed.Passphrase);
        Assert.DoesNotContain("token", created.ToUri().ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MemoryTeamSettings(TeamServerSettings value) : ITeamServerSettingsService
    {
        public TeamServerSettings Current { get; private set; } = value;
        public event EventHandler? Changed;
        public Task SaveAsync(TeamServerSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings.Normalize();
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}

public sealed class MyTeamSurfaceTests
{
    [Fact]
    public void LanDiscovery_UsesTheExpectedDnsSdServiceTypeAndLocalDomain()
    {
        var profile = new ServiceProfile("Test Robot Command", LanTeamDiscoveryService.ServiceType, 7443);

        Assert.Equal("_logos-robotcommand._tcp", profile.ServiceName.ToString().TrimEnd('.'));
        Assert.Equal("_logos-robotcommand._tcp.local", profile.QualifiedServiceName.ToString().TrimEnd('.'));
    }

    [Fact]
    public void Workspace_IsRegisteredBeforeSettingsAndExposesSessionControls()
    {
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "ViewModels", "ShellViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Workspaces", "MyTeamView.axaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "ViewModels", "MyTeamViewModel.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "src", "runtime", "RobotCommand.Runtime", "Services", "Team", "TeamAccessCoordinator.cs"));
        var myTeamIndex = shell.IndexOf("\"my-team\"", StringComparison.Ordinal);
        var settingsIndex = shell.IndexOf("\"settings\"", StringComparison.Ordinal);
        Assert.True(myTeamIndex >= 0 && settingsIndex > myTeamIndex);
        Assert.Contains("ShareOperatorLocation", view, StringComparison.Ordinal);
        Assert.Contains("ApproveCommand", view, StringComparison.Ordinal);
        Assert.Contains("RejectCommand", view, StringComparison.Ordinal);
        Assert.Contains("DisconnectCommand", view, StringComparison.Ordinal);
        Assert.Contains("CertificateFingerprint", view, StringComparison.Ordinal);
        Assert.Contains("MyTeamMirror", view, StringComparison.Ordinal);
        Assert.Contains("MyTeamConnect", view, StringComparison.Ordinal);
        Assert.Contains("RemotePassphrase", view, StringComparison.Ordinal);
        Assert.Contains("MyTeamNearby", view, StringComparison.Ordinal);
        Assert.Contains("RefreshNearbyCommand", view, StringComparison.Ordinal);
        Assert.Contains("CreatePairingCommand", view, StringComparison.Ordinal);
        Assert.Contains("UsePairingLinkCommand", view, StringComparison.Ordinal);
        Assert.Contains("RequirePassphrase", view, StringComparison.Ordinal);
        Assert.Contains("RegeneratePassphraseCommand", view, StringComparison.Ordinal);
        Assert.Contains("RemoteRequiresPassphrase", view, StringComparison.Ordinal);
        Assert.Contains("EnableAuthenticationAndReauthenticateCommand", viewModel, StringComparison.Ordinal);
        Assert.Contains("DisconnectAll", coordinator, StringComparison.Ordinal);
        var sdk = File.ReadAllText(Path.Combine(root, "src", "sdk", "RobotCommand.Sdk", "TeamClient.cs"));
        Assert.Contains("Disconnected", sdk, StringComparison.Ordinal);
    }

    [Fact]
    public void ObserverProjection_IsReadOnlyAndIsNotRelayed()
    {
        var root = FindRepositoryRoot();
        var observer = File.ReadAllText(Path.Combine(root, "src", "runtime", "RobotCommand.Runtime", "Services", "Team", "RobotCommandObserverService.cs"));
        var projector = File.ReadAllText(Path.Combine(root, "src", "runtime", "RobotCommand.Runtime", "Services", "Team", "RobotCommandSnapshotProjector.cs"));
        var units = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "Views", "Shell", "UnitsPanelView.axaml"));

        Assert.Contains("ConnectionMode.TeamObserver", observer, StringComparison.Ordinal);
        Assert.Contains("read-only", observer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connection.Mode != ConnectionMode.TeamObserver", projector, StringComparison.Ordinal);
        Assert.Contains("Read-only mirror", units, StringComparison.Ordinal);
    }

    [Fact]
    public void LanDiscovery_IsDnsSdBasedAndCannotBypassTrustWorkflow()
    {
        var root = FindRepositoryRoot();
        var discovery = File.ReadAllText(Path.Combine(root, "src", "runtime", "RobotCommand.Runtime", "Services", "Team", "LanTeamDiscoveryService.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "app", "RobotCommand", "ViewModels", "MyTeamViewModel.cs"));

        Assert.Contains("_logos-robotcommand._tcp", discovery, StringComparison.Ordinal);
        Assert.Contains("QueryServiceInstances(ServiceType)", discovery, StringComparison.Ordinal);
        Assert.Contains("ServiceProfile", discovery, StringComparison.Ordinal);
        Assert.Contains("advertiser.Announce(profile)", discovery, StringComparison.Ordinal);
        Assert.Contains("var address = eventArgs.RemoteEndPoint.Address", discovery, StringComparison.Ordinal);
        Assert.Contains("RemotePassphrase", viewModel, StringComparison.Ordinal);
        Assert.Contains("MyTeamNearbySelected", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void PairingLinksRemainSessionScopedAndKeepApprovalInTheLoop()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "runtime", "RobotCommand.Runtime", "Services", "Team", "TeamPairingService.cs"));
        var coordinator = File.ReadAllText(Path.Combine(root, "src", "runtime", "RobotCommand.Runtime", "Services", "Team", "TeamAccessCoordinator.cs"));

        Assert.Contains("TimeSpan.FromMinutes(30)", service, StringComparison.Ordinal);
        Assert.Contains("_pairing.Consume", coordinator, StringComparison.Ordinal);
        Assert.Contains("Session passphrase verified", coordinator, StringComparison.Ordinal);
        Assert.Contains("pairing_phrase", File.ReadAllText(Path.Combine(root, "src", "sdk", "RobotCommand.Sdk", "Proto", "team", "v1", "team.proto")), StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RobotCommand.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
