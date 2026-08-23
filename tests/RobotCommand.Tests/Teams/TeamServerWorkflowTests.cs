using Microsoft.Extensions.DependencyInjection;
using RobotCommand.Bootstrap;
using RobotCommand.Cli;
using RobotCommand.Models;
using RobotCommand.Services.Maps;
using RobotCommand.Services.Team;
using Xunit;

namespace RobotCommand.Tests;

public sealed class TeamServerWorkflowTests
{
    [Fact]
    public void DefaultServerNameIsGeneric()
    {
        Assert.Equal("Robot Command", TeamServerSettings.Default.DisplayName);
    }

    [Fact]
    public void HeadlessRuntime_ResolvesSharedTeamAndMapServicesWithoutAvalonia()
    {
        using var host = RobotCommandRuntimeHost.Build(AppContext.BaseDirectory, RobotCommandRuntimeMode.Headless);

        Assert.IsType<TeamServerWorkflow>(host.Services.GetRequiredService<ITeamServerWorkflow>());
        Assert.IsType<MapPresentationState>(host.Services.GetRequiredService<IMapPresentationState>());
    }

    [Fact]
    public async Task StartAsync_ConfiguresSessionAuthenticationAndPersistsServerSettings()
    {
        var settings = new MemorySettings(TeamServerSettings.Default);
        var passphrase = new TeamPassphraseService();
        var server = new MemoryServer(settings);
        var access = new MemoryAccessCoordinator();
        var workflow = new TeamServerWorkflow(server, access, passphrase, settings);

        var status = await workflow.StartAsync(new TeamServerStartRequest(
            "CLI test server", 17443, 3, false, "Repair-Spindle"),
            TestContext.Current.CancellationToken);

        Assert.True(status.IsRunning);
        Assert.Equal("CLI test server", settings.Current.DisplayName);
        Assert.Equal(17443, settings.Current.Port);
        Assert.Equal(3, settings.Current.MaximumClients);
        Assert.True(settings.Current.RequirePassphrase);
        Assert.True(passphrase.Verify("repair spindle"));
    }

    [Fact]
    public async Task AuthenticationChanges_ReauthenticateExistingObserversAndOpenAccessClearsPassphrase()
    {
        var settings = new MemorySettings(TeamServerSettings.Default);
        var passphrase = new TeamPassphraseService();
        var server = new MemoryServer(settings);
        var access = new MemoryAccessCoordinator();
        var workflow = new TeamServerWorkflow(server, access, passphrase, settings);

        await workflow.SetAuthenticationAsync(true, true, "Handball-Precise", TestContext.Current.CancellationToken);

        Assert.True(passphrase.Verify("handball precise"));
        Assert.Equal(TeamDisconnectReason.AuthenticationRequired, access.LastDisconnectReason);
        Assert.True(settings.Current.RequirePassphrase);

        await workflow.SetAuthenticationAsync(false, false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(passphrase.IsOpenAccess);
        Assert.False(settings.Current.RequirePassphrase);
    }

    [Fact]
    public void CliOptions_ParseConfiguredServerRunArguments()
    {
        Assert.True(ServerRunOptions.TryParse(["--port", "7443", "--max-clients", "4", "--json"], out var options, out var error), error);
        Assert.NotNull(options);
    }

    [Fact]
    public void CliOptions_ParseOpenAccessServerRunArguments()
    {
        Assert.True(ServerRunOptions.TryParse(["--open-access", "--name", "Field Node"], out var options, out var error), error);
        Assert.True(options.OpenAccess);
    }

    [Fact]
    public void CliOptions_RejectsPassphraseWithOpenAccess()
    {
        Assert.False(ServerRunOptions.TryParse(["--open-access", "--passphrase", "Repair-Spindle"], out _, out var error));
        Assert.Contains("either --open-access or --passphrase", error);
    }

    private sealed class MemorySettings(TeamServerSettings initial) : ITeamServerSettingsService
    {
        public TeamServerSettings Current { get; private set; } = initial;
        public event EventHandler? Changed;
        public Task SaveAsync(TeamServerSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryServer(MemorySettings settings) : ILanTeamServer
    {
        public bool IsRunning { get; private set; }
        public string Status { get; private set; } = "Stopped";
        public string? LastError => null;
        public string Endpoint => $"https://test:{settings.Current.Port}";
        public string CertificateFingerprint => "TEST";
        public event EventHandler? Changed;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            Status = "Running";
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            Status = "Stopped";
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryAccessCoordinator : ITeamAccessCoordinator
    {
        public IReadOnlyList<TeamAccessRequestRecord> PendingRequests => [];
        public IReadOnlyList<TeamConnectedClientRecord> ConnectedClients => [];
        public TeamDisconnectReason? LastDisconnectReason { get; private set; }
        public event EventHandler? Changed;
        public TeamAccessRegistration RequestAccess(string displayName, string applicationName, string applicationVersion, string sdkVersion, string apiVersion, string clientInstanceId, string requestNonce, string pairingId, string pairingCode, string pairingPassphrase, string remoteAddress) => throw new NotSupportedException();
        public bool Approve(string requestId) => false;
        public bool Reject(string requestId, string message = "The Robot Command operator rejected this request.") => false;
        public TeamSessionLease BeginObserverSession(string token, string remoteAddress) => throw new NotSupportedException();
        public bool ValidateConnectedToken(string token, string remoteAddress) => false;
        public void Touch(string token) { }
        public void EndObserverSession(string token) { }
        public bool Disconnect(string sessionId) => false;
        public bool Disconnect(string sessionId, string message) => false;
        public int DisconnectAll(TeamDisconnectReason reason, string message)
        {
            LastDisconnectReason = reason;
            Changed?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        public void Clear() { }
    }
}
