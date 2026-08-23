using System.Net;
using System.Net.Sockets;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotCommand.Models;
using RobotCommand.Sdk.Team.V1;

namespace RobotCommand.Services.Team;

public interface ILanTeamServer
{
    bool IsRunning { get; }
    string Status { get; }
    string? LastError { get; }
    string Endpoint { get; }
    string CertificateFingerprint { get; }
    event EventHandler? Changed;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class LanTeamServer : ILanTeamServer, IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly ITeamServerSettingsService _settings;
    private readonly ITeamCertificateService _certificates;
    private readonly ITeamAccessCoordinator _access;
    private readonly ITeamPairingService _pairing;
    private readonly ITeamPassphraseService _passphrase;
    private readonly IRobotCommandSnapshotProjector _snapshots;
    private readonly ILogger<LanTeamServer> _logger;
    private WebApplication? _application;
    private string _status = "Stopped";
    private string? _lastError;

    public LanTeamServer(
        ITeamServerSettingsService settings,
        ITeamCertificateService certificates,
        ITeamAccessCoordinator access,
        ITeamPairingService pairing,
        IRobotCommandSnapshotProjector snapshots,
        ILogger<LanTeamServer> logger)
        : this(settings, certificates, access, pairing, new TeamPassphraseService(), snapshots, logger)
    {
    }

    public LanTeamServer(
        ITeamServerSettingsService settings,
        ITeamCertificateService certificates,
        ITeamAccessCoordinator access,
        ITeamPairingService pairing,
        ITeamPassphraseService passphrase,
        IRobotCommandSnapshotProjector snapshots,
        ILogger<LanTeamServer> logger)
    {
        _settings = settings;
        _certificates = certificates;
        _access = access;
        _pairing = pairing;
        _passphrase = passphrase;
        _snapshots = snapshots;
        _logger = logger;
    }

    public bool IsRunning { get { lock (_gate) return _application is not null; } }
    public string Status { get { lock (_gate) return _status; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public string Endpoint => $"https://{Environment.MachineName}:{_settings.Current.Port}";
    public string CertificateFingerprint => _certificates.GetOrCreate().Sha256Fingerprint;
    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_application is not null) return;
            _status = "Starting";
            _lastError = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            var certificate = _certificates.GetOrCreate();
            var settings = _settings.Current;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(LanTeamServer).Assembly.GetName().Name,
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(settings.Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    listen.UseHttps(certificate.Certificate);
                });
            });
            builder.Services.AddGrpc(options => options.EnableDetailedErrors = false);
            builder.Services.AddSingleton(_settings);
            builder.Services.AddSingleton(_access);
            builder.Services.AddSingleton(_passphrase);
            builder.Services.AddSingleton(_snapshots);
            builder.Services.AddSingleton(certificate);
            builder.Services.AddSingleton<RobotCommandServerInfoGrpcService>();
            builder.Services.AddSingleton<RobotCommandAccessGrpcService>();
            builder.Services.AddSingleton<RobotCommandObserverGrpcService>();

            var application = builder.Build();
            application.Use(async (context, next) =>
            {
                var remote = context.Connection.RemoteIpAddress;
                if (remote is null || !LanAddressPolicy.IsAllowed(remote))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next();
            });
            application.MapGrpcService<RobotCommandServerInfoGrpcService>();
            application.MapGrpcService<RobotCommandAccessGrpcService>();
            application.MapGrpcService<RobotCommandObserverGrpcService>();
            await application.StartAsync(cancellationToken);
            lock (_gate)
            {
                _application = application;
                _status = "Running";
            }
            _logger.LogInformation("Robot Command Team API started on port {Port}.", settings.Port);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _application = null;
                _status = "Faulted";
                _lastError = FriendlyServerError(exception);
            }
            _logger.LogError(exception, "Could not start the Robot Command Team API.");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WebApplication? application;
        lock (_gate)
        {
            application = _application;
            _application = null;
            _status = "Stopping";
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _access.Clear();
        _pairing.Clear();
        _passphrase.Clear();
        if (application is not null)
        {
            try { await application.StopAsync(cancellationToken); }
            finally { await application.DisposeAsync(); }
        }
        lock (_gate) _status = "Stopped";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    private static string FriendlyServerError(Exception exception)
    {
        var socket = exception as SocketException ?? exception.InnerException as SocketException;
        if (socket?.SocketErrorCode == SocketError.AddressAlreadyInUse)
            return "The configured Team API port is already in use. Choose another port or close the other application.";
        if (exception is UnauthorizedAccessException)
            return "Robot Command was not permitted to open the Team API listener or certificate.";
        return exception.Message;
    }

    public void Dispose()
    {
        if (_application is not null)
            StopAsync().GetAwaiter().GetResult();
    }
}

public static class LanAddressPolicy
{
    public static bool IsAllowed(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
        return false;
    }
}

internal sealed class RobotCommandServerInfoGrpcService(
    ITeamServerSettingsService settings,
    TeamServerCertificate certificate,
    ITeamPassphraseService passphrase)
    : ServerInfoService.ServerInfoServiceBase
{
    public override Task<ServerInfoResponse> GetServerInfo(ServerInfoRequest request, ServerCallContext context)
    {
        var response = new ServerInfoResponse
        {
            InstanceId = certificate.InstanceId,
            DisplayName = settings.Current.DisplayName,
            ApiVersion = "v1",
            ServerTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            TlsSha256Fingerprint = certificate.Sha256Fingerprint,
            MinimumSdkVersion = "0.1.0-alpha.3",
            RequiresPassphrase = passphrase.IsConfigured
        };
        response.Capabilities.AddRange(["observer.units", "observer.map", "observer.diagnostics", "observer.links"]);
        return Task.FromResult(response);
    }
}

internal sealed class RobotCommandAccessGrpcService(ITeamAccessCoordinator access)
    : AccessService.AccessServiceBase
{
    public override async Task RequestAccess(
        AccessRequest request,
        IServerStreamWriter<AccessStatus> responseStream,
        ServerCallContext context)
    {
        var remote = RemoteAddress(context);
        var registration = access.RequestAccess(
            request.DisplayName,
            request.ApplicationName,
            request.ApplicationVersion,
            request.SdkVersion,
            request.ApiVersion,
            request.ClientInstanceId,
            request.RequestNonce,
            request.PairingId,
            request.PairingCode,
            request.PairingPhrase,
            remote);
        await responseStream.WriteAsync(ToStatus(registration.Request));
        var decision = await registration.Decision.WaitAsync(context.CancellationToken);
        await responseStream.WriteAsync(new AccessStatus
        {
            RequestId = registration.Request.Id,
            State = ToAccessState(decision.State),
            Message = decision.Message,
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(decision.ExpiresAt),
            SessionToken = decision.SessionToken ?? string.Empty
        });
    }

    private static AccessStatus ToStatus(Models.TeamAccessRequestRecord request)
        => new()
        {
            RequestId = request.Id,
            State = ToAccessState(request.State),
            Message = request.Message,
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(request.ExpiresAt)
        };

    private static AccessState ToAccessState(Models.TeamAccessRequestState state)
        => state switch
        {
            Models.TeamAccessRequestState.Pending => AccessState.Pending,
            Models.TeamAccessRequestState.Approved => AccessState.Approved,
            Models.TeamAccessRequestState.Rejected => AccessState.Rejected,
            Models.TeamAccessRequestState.Expired => AccessState.Expired,
            Models.TeamAccessRequestState.Incompatible => AccessState.Incompatible,
            _ => AccessState.Unspecified
        };

    private static string RemoteAddress(ServerCallContext context)
        => context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

internal sealed class RobotCommandObserverGrpcService(
    ITeamAccessCoordinator access,
    IRobotCommandSnapshotProjector snapshots)
    : ObserverService.ObserverServiceBase
{
    public override Task<RobotCommandSnapshot> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        var token = Token(context);
        if (!access.ValidateConnectedToken(token, RemoteAddress(context)))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "An active approved observer session is required."));
        access.Touch(token);
        return Task.FromResult(snapshots.Current);
    }

    public override async Task WatchSnapshots(
        WatchSnapshotsRequest request,
        IServerStreamWriter<SnapshotEnvelope> responseStream,
        ServerCallContext context)
    {
        var token = Token(context);
        TeamSessionLease lease;
        try { lease = access.BeginObserverSession(token, RemoteAddress(context)); }
        catch (UnauthorizedAccessException exception)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, exception.Message));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var channel = System.Threading.Channels.Channel.CreateBounded<RobotCommandSnapshot>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        void OnSnapshot(object? sender, RobotCommandSnapshot snapshot) => channel.Writer.TryWrite(snapshot.Clone());
        snapshots.SnapshotChanged += OnSnapshot;
        channel.Writer.TryWrite(snapshots.Current);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var read = channel.Reader.WaitToReadAsync(linked.Token).AsTask();
                var disconnect = lease.DisconnectNotifications.WaitToReadAsync(linked.Token).AsTask();
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
                var completed = await Task.WhenAny(read, disconnect, heartbeat);
                if (completed == disconnect && await disconnect)
                {
                    TeamDisconnectNotice? notice = null;
                    while (lease.DisconnectNotifications.TryRead(out var available)) notice = available;
                    if (notice is not null)
                    {
                        await responseStream.WriteAsync(new SnapshotEnvelope
                        {
                            Disconnect = new ObserverDisconnectNotice
                            {
                                Reason = ToDisconnectReason(notice.Reason),
                                Message = notice.Message,
                                OccurredAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                            }
                        });
                    }
                    return;
                }
                if (completed == read && await read)
                {
                    RobotCommandSnapshot? latest = null;
                    while (channel.Reader.TryRead(out var available)) latest = available;
                    if (latest is not null)
                    {
                        await responseStream.WriteAsync(new SnapshotEnvelope { Snapshot = latest });
                        access.Touch(token);
                    }
                }
                else
                {
                    await responseStream.WriteAsync(new SnapshotEnvelope
                    {
                        Heartbeat = new SnapshotHeartbeat
                        {
                            CurrentRevision = snapshots.Current.Revision,
                            ServerTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                        }
                    });
                    access.Touch(token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            snapshots.SnapshotChanged -= OnSnapshot;
            access.EndObserverSession(token);
        }
    }

    private static string Token(ServerCallContext context)
    {
        var authorization = context.RequestHeaders.FirstOrDefault(item => item.Key == "authorization")?.Value;
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A bearer session token is required."));
        return authorization[7..].Trim();
    }

    private static string RemoteAddress(ServerCallContext context)
        => context.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static ObserverDisconnectReason ToDisconnectReason(TeamDisconnectReason reason)
        => reason switch
        {
            TeamDisconnectReason.OperatorDisconnected => ObserverDisconnectReason.OperatorDisconnected,
            TeamDisconnectReason.AuthenticationRequired => ObserverDisconnectReason.AuthenticationRequired,
            TeamDisconnectReason.ServerStopped => ObserverDisconnectReason.ServerStopped,
            _ => ObserverDisconnectReason.Unspecified
        };
}
