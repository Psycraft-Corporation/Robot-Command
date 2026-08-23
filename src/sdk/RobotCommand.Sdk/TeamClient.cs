using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using RobotCommand.Sdk.Team.V1;

namespace RobotCommand.Sdk;

/// <summary>Information obtained before a server certificate is trusted.</summary>
public sealed record RobotCommandServerProbe(
    Uri Endpoint,
    ServerInfoResponse ServerInfo,
    string ObservedCertificateFingerprint);

/// <summary>Identity supplied while requesting access to a Robot Command observer session.</summary>
public sealed record RobotCommandClientIdentity(
    string DisplayName,
    string ApplicationName,
    string ApplicationVersion,
    string ClientInstanceId);

/// <summary>Explains why a live observer session was closed by its Robot Command host.</summary>
public sealed record RobotCommandObserverDisconnect(
    ObserverDisconnectReason Reason,
    string Message,
    DateTimeOffset OccurredAt);

/// <summary>Creates certificate-pinned observer sessions.</summary>
public static class RobotCommandLanClient
{
    /// <summary>The Team API version implemented by this SDK.</summary>
    public const string ApiVersion = "v1";

    /// <summary>The package version supplied during access negotiation.</summary>
    public const string SdkVersion = "0.1.0-alpha.3";

    /// <summary>
    /// Probes a server without trusting it and returns the certificate fingerprint that the
    /// application must present to an operator for explicit pinning.
    /// </summary>
    public static async Task<RobotCommandServerProbe> ProbeAsync(
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        string? fingerprint = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                {
                    return false;
                }

                fingerprint = Fingerprint(certificate);
                return true;
            }
        };
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler });
        var client = new ServerInfoService.ServerInfoServiceClient(channel);
        var info = await client.GetServerInfoAsync(new ServerInfoRequest(), cancellationToken: cancellationToken);
        if (fingerprint is null)
        {
            throw new InvalidOperationException("The server did not present a TLS certificate.");
        }

        return new RobotCommandServerProbe(endpoint, info, fingerprint);
    }

    /// <summary>
    /// Requests an approved observer session using an explicitly pinned server certificate.
    /// </summary>
    public static async Task<RobotCommandObserverSession> RequestAccessAsync(
        Uri endpoint,
        string expectedFingerprint,
        RobotCommandClientIdentity identity,
        string pairingPassphrase = "",
        RobotCommandPairingInvitation? pairing = null,
        Action<AccessStatus>? statusChanged = null,
        CancellationToken cancellationToken = default)
    {
        var channel = CreatePinnedChannel(endpoint, expectedFingerprint);
        try
        {
            var access = new AccessService.AccessServiceClient(channel);
            if (pairing is not null &&
                (!Uri.Compare(endpoint, pairing.Endpoint, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase).Equals(0) ||
                 !string.Equals(NormalizeFingerprint(expectedFingerprint), pairing.CertificateFingerprint, StringComparison.Ordinal)))
            {
                throw new ArgumentException("The pairing invitation does not match the selected Robot Command endpoint or certificate.", nameof(pairing));
            }

            using var call = access.RequestAccess(new AccessRequest
            {
                DisplayName = identity.DisplayName,
                ApplicationName = identity.ApplicationName,
                ApplicationVersion = identity.ApplicationVersion,
                SdkVersion = SdkVersion,
                ApiVersion = ApiVersion,
                ClientInstanceId = identity.ClientInstanceId,
                RequestNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                PairingId = pairing?.PairingId ?? string.Empty,
                PairingCode = pairing?.ShortCode ?? string.Empty,
                PairingPhrase = string.IsNullOrWhiteSpace(pairingPassphrase) ? pairing?.Passphrase ?? string.Empty : pairingPassphrase
            }, cancellationToken: cancellationToken);

            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                var status = call.ResponseStream.Current;
                statusChanged?.Invoke(status);
                if (status.State == AccessState.Approved && !string.IsNullOrWhiteSpace(status.SessionToken))
                {
                    var session = new RobotCommandObserverSession(channel, status.SessionToken);
                    await session.StartAsync(cancellationToken);
                    return session;
                }

                if (status.State is AccessState.Rejected or AccessState.Expired or AccessState.Incompatible)
                {
                    throw new RpcException(new Status(StatusCode.PermissionDenied, status.Message));
                }
            }

            throw new RpcException(new Status(StatusCode.PermissionDenied, "The access request ended without approval."));
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Requests an observer session with a legacy QR invitation. New callers should use the
    /// passphrase overload; this preserves source compatibility for alpha.2 clients.
    /// </summary>
    public static Task<RobotCommandObserverSession> RequestAccessAsync(
        Uri endpoint,
        string expectedFingerprint,
        RobotCommandClientIdentity identity,
        RobotCommandPairingInvitation? pairing,
        Action<AccessStatus>? statusChanged = null,
        CancellationToken cancellationToken = default)
        => RequestAccessAsync(endpoint, expectedFingerprint, identity, string.Empty, pairing, statusChanged, cancellationToken);

    private static GrpcChannel CreatePinnedChannel(Uri endpoint, string expectedFingerprint)
    {
        var normalizedExpected = NormalizeFingerprint(expectedFingerprint);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(normalizedExpected),
                    Convert.FromHexString(Fingerprint(certificate)))
        };
        return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler });
    }

    internal static string Fingerprint(X509Certificate certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>Normalizes and validates a SHA-256 certificate fingerprint.</summary>
    public static string NormalizeFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 certificate fingerprint must contain 64 hexadecimal characters.", nameof(value));
        }

        return normalized;
    }
}

/// <summary>An approved, live observer session.</summary>
public sealed class RobotCommandObserverSession : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly string _token;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _watchTask;
    private RobotCommandSnapshot? _current;

    internal RobotCommandObserverSession(GrpcChannel channel, string token)
    {
        _channel = channel;
        _token = token;
    }

    /// <summary>Gets a defensive copy of the most recently received snapshot.</summary>
    public RobotCommandSnapshot? CurrentSnapshot
    {
        get { lock (_gate) return _current?.Clone(); }
    }

    /// <summary>Raised when a new complete snapshot has been received.</summary>
    public event EventHandler<RobotCommandSnapshot>? SnapshotChanged;

    /// <summary>Raised when the host closes the session with an explicit reason.</summary>
    public event EventHandler<RobotCommandObserverDisconnect>? Disconnected;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watchTask = WatchAsync(ready, _lifetime.Token);
        await ready.Task.WaitAsync(cancellationToken);
    }

    private async Task WatchAsync(TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        var client = new ObserverService.ObserverServiceClient(_channel);
        var headers = new Metadata { { "authorization", $"Bearer {_token}" } };
        try
        {
            using var call = client.WatchSnapshots(new WatchSnapshotsRequest(), headers, cancellationToken: cancellationToken);
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                var envelope = call.ResponseStream.Current;
                if (envelope.PayloadCase == SnapshotEnvelope.PayloadOneofCase.Disconnect)
                {
                    var notice = envelope.Disconnect;
                    Disconnected?.Invoke(this, new RobotCommandObserverDisconnect(
                        notice.Reason,
                        notice.Message,
                        notice.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow));
                    ready.TrySetResult();
                    _lifetime.Cancel();
                    _channel.Dispose();
                    return;
                }
                if (envelope.PayloadCase != SnapshotEnvelope.PayloadOneofCase.Snapshot)
                {
                    continue;
                }

                var incoming = envelope.Snapshot;
                var previousRevision = 0UL;
                lock (_gate) previousRevision = _current?.Revision ?? 0;
                if (previousRevision > 0 && incoming.Revision > previousRevision + 1)
                {
                    incoming = await client.GetSnapshotAsync(
                        new GetSnapshotRequest { KnownRevision = previousRevision },
                        headers,
                        cancellationToken: cancellationToken);
                }

                lock (_gate) _current = incoming.Clone();
                SnapshotChanged?.Invoke(this, incoming.Clone());
                ready.TrySetResult();
            }
            ready.TrySetException(new RpcException(new Status(StatusCode.Unavailable, "Observer stream ended.")));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ready.TrySetException(exception);
        }
    }

    /// <summary>Stops observation and releases the underlying gRPC channel.</summary>
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_watchTask is not null)
        {
            try { await _watchTask; } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _channel.Dispose();
    }
}
