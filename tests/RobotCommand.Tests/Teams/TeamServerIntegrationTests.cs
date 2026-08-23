using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Sdk;
using RobotCommand.Sdk.Team.V1;
using RobotCommand.Services.Team;
using Xunit;

namespace RobotCommand.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TeamServerIntegrationCollection
{
    public const string Name = "Team server integration";
}

[Collection(TeamServerIntegrationCollection.Name)]
public sealed class TeamServerIntegrationTests
{
    [Fact]
    public async Task PinnedSdk_CanBeApprovedObserveSnapshotAndBeRevoked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var port = AvailablePort();
        var settings = new MemorySettings(new TeamServerSettings("Integration console", port, 2));
        using var certificate = CreateCertificate();
        var certificateInfo = new TeamServerCertificate(
            certificate,
            Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            "integration-instance");
        var pairing = new TeamPairingService();
        using var access = new TeamAccessCoordinator(settings, pairing);
        var snapshots = new MemorySnapshots(new RobotCommandSnapshot
        {
            Revision = 1,
            Map = new MapSnapshot { StyleId = "standard", StyleName = "Standard" }
        });
        using var server = new LanTeamServer(
            settings,
            new MemoryCertificateService(certificateInfo),
            access,
            pairing,
            snapshots,
            NullLogger<LanTeamServer>.Instance);

        await server.StartAsync(cancellationToken);
        Assert.True(server.IsRunning, server.LastError);
        var endpoint = new Uri($"https://127.0.0.1:{port}");
        var probe = await RobotCommandLanClient.ProbeAsync(endpoint, cancellationToken);
        Assert.Equal("Integration console", probe.ServerInfo.DisplayName);
        Assert.Equal(certificateInfo.Sha256Fingerprint, probe.ObservedCertificateFingerprint);

        var sessionTask = RobotCommandLanClient.RequestAccessAsync(
            endpoint,
            probe.ObservedCertificateFingerprint,
            new RobotCommandClientIdentity("Observer", "Integration test", "1.0", "test-client"),
            cancellationToken: cancellationToken);
        await WaitUntilAsync(() => access.PendingRequests.Count == 1, cancellationToken);
        Assert.True(access.Approve(access.PendingRequests.Single().Id));

        await using var session = await sessionTask;
        Assert.NotNull(session.CurrentSnapshot);
        Assert.Equal(1UL, session.CurrentSnapshot!.Revision);
        Assert.Equal("standard", session.CurrentSnapshot.Map.StyleId);
        await WaitUntilAsync(() => access.ConnectedClients.Count == 1, cancellationToken);

        Assert.True(access.Disconnect(access.ConnectedClients.Single().SessionId));
        await WaitUntilAsync(() => access.ConnectedClients.Count == 0, cancellationToken);
        await server.StopAsync(cancellationToken);
        Assert.False(server.IsRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for Team API state.");
            await Task.Delay(25, cancellationToken);
        }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Robot Command Team API integration test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    private sealed class MemorySettings(TeamServerSettings value) : ITeamServerSettingsService
    {
        public TeamServerSettings Current { get; private set; } = value;
        public event EventHandler? Changed;
        public Task SaveAsync(TeamServerSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCertificateService(TeamServerCertificate certificate) : ITeamCertificateService
    {
        public TeamServerCertificate GetOrCreate() => certificate;
    }

    private sealed class MemorySnapshots(RobotCommandSnapshot snapshot) : IRobotCommandSnapshotProjector
    {
        public RobotCommandSnapshot Current { get; private set; } = snapshot;
        public bool ShareOperatorLocation { get; set; }
        public event EventHandler<RobotCommandSnapshot>? SnapshotChanged;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish(RobotCommandSnapshot next)
        {
            Current = next;
            SnapshotChanged?.Invoke(this, next);
        }
    }
}
