using System.Net;
using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RobotCommand.Services.Team;

/// <summary>
/// Advertises a running Team observer server and discovers other Robot Command
/// Team servers on the current LAN using DNS-SD/mDNS. Discovery never grants
/// access: callers still probe, pin the TLS certificate, and request approval.
/// </summary>
public interface ILanTeamDiscoveryService
{
    IReadOnlyList<DiscoveredRobotCommandServer> Servers { get; }
    bool IsAvailable { get; }
    string Status { get; }
    event EventHandler? Changed;
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed record DiscoveredRobotCommandServer(
    string InstanceId,
    string DisplayName,
    Uri Endpoint,
    string ApiVersion,
    DateTimeOffset LastSeen);

public sealed class LanTeamDiscoveryService : ILanTeamDiscoveryService, IHostedService, IDisposable
{
    public const string ServiceType = "_logos-robotcommand._tcp";
    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly ILanTeamServer _server;
    private readonly ITeamServerSettingsService _settings;
    private readonly ITeamCertificateService _certificates;
    private readonly ILogger<LanTeamDiscoveryService> _logger;
    private readonly Dictionary<string, DiscoveredRobotCommandServer> _servers = new(StringComparer.Ordinal);
    private ServiceDiscovery? _advertiser;
    private ServiceProfile? _advertisedProfile;
    private ServiceDiscovery? _browser;
    private CancellationTokenSource? _lifetime;
    private Task? _queryLoop;
    private bool _isAvailable;
    private string _status = "Discovery unavailable";

    public LanTeamDiscoveryService(
        ILanTeamServer server,
        ITeamServerSettingsService settings,
        ITeamCertificateService certificates,
        ILogger<LanTeamDiscoveryService> logger)
    {
        _server = server;
        _settings = settings;
        _certificates = certificates;
        _logger = logger;
    }

    public IReadOnlyList<DiscoveredRobotCommandServer> Servers
    {
        get { lock (_gate) return _servers.Values.OrderBy(server => server.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(); }
    }

    public bool IsAvailable { get { lock (_gate) return _isAvailable; } }
    public string Status { get { lock (_gate) return _status; } }
    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _browser = new ServiceDiscovery();
            _browser.Mdns.AnswerReceived += OnAnswerReceived;
            _advertiser = new ServiceDiscovery();
            _server.Changed += OnServerChanged;
            _settings.Changed += OnSettingsChanged;
            lock (_gate)
            {
                _isAvailable = true;
                _status = "Ready";
            }
            SynchronizeAdvertisement();
            _queryLoop = QueryLoopAsync(_lifetime.Token);
            _ = RefreshAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "LAN discovery could not be started. Manual Team endpoints remain available.");
            lock (_gate)
            {
                _isAvailable = false;
                _status = "Discovery unavailable";
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _server.Changed -= OnServerChanged;
        _settings.Changed -= OnSettingsChanged;
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }

        var queryLoop = Interlocked.Exchange(ref _queryLoop, null);
        if (queryLoop is not null)
        {
            try { await queryLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        if (_advertiser is not null)
        {
            try { _advertiser.Unadvertise(); }
            catch (Exception exception) { _logger.LogDebug(exception, "Could not withdraw the Team discovery advertisement."); }
            _advertiser.Dispose();
            _advertiser = null;
        }

        if (_browser is not null)
        {
            _browser.Mdns.AnswerReceived -= OnAnswerReceived;
            _browser.Dispose();
            _browser = null;
        }

        lock (_gate)
        {
            _servers.Clear();
            _advertisedProfile = null;
            _isAvailable = false;
            _status = "Stopped";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var browser = _browser;
        if (browser is null || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        try
        {
            // Use the DNS-SD browser rather than a raw mDNS query. It sends
            // the correct PTR request for this service type and follows the
            // service-instance records advertised by ServiceDiscovery.
            browser.QueryServiceInstances(ServiceType);
            PruneExpiredServers();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "LAN Team discovery query failed.");
            lock (_gate) _status = "Discovery temporarily unavailable";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    private async Task QueryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(QueryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private void OnServerChanged(object? sender, EventArgs eventArgs) => SynchronizeAdvertisement();
    private void OnSettingsChanged(object? sender, EventArgs eventArgs) => SynchronizeAdvertisement();

    private void SynchronizeAdvertisement()
    {
        var advertiser = _advertiser;
        if (advertiser is null) return;
        try
        {
            if (_advertisedProfile is not null)
            {
                advertiser.Unadvertise(_advertisedProfile);
                _advertisedProfile = null;
            }

            if (_server.IsRunning)
            {
                var certificate = _certificates.GetOrCreate();
                var profile = new ServiceProfile(
                    $"{_settings.Current.DisplayName} ({certificate.InstanceId[..Math.Min(8, certificate.InstanceId.Length)]})",
                    ServiceType,
                    checked((ushort)_settings.Current.Port));
                profile.AddProperty("api", "v1");
                profile.AddProperty("instance-id", certificate.InstanceId);
                profile.AddProperty("display-name", _settings.Current.DisplayName);
                advertiser.Advertise(profile);
                // A server that starts after its peers have already queried
                // the LAN should still be visible without waiting for the
                // next periodic browse request.
                advertiser.Announce(profile);
                _advertisedProfile = profile;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not update the Robot Command LAN discovery advertisement.");
            lock (_gate) _status = "Advertisement unavailable";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs eventArgs)
    {
        try
        {
            var records = eventArgs.Message.Answers.Concat(eventArgs.Message.AdditionalRecords).ToArray();
            var instances = records.OfType<PTRRecord>()
                .Where(record => IsTeamServiceName(record.Name))
                .Select(record => record.DomainName)
                .Distinct()
                .ToArray();
            if (instances.Length == 0) return;

            foreach (var instance in instances)
            {
                var service = records.OfType<SRVRecord>().FirstOrDefault(record => NameEquals(record.Name, instance));
                if (service is null) continue;
                var properties = records.OfType<TXTRecord>()
                    .FirstOrDefault(record => NameEquals(record.Name, instance))?
                    .Strings
                    .Select(value => value.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!properties.TryGetValue("api", out var apiVersion) || !string.Equals(apiVersion, "v1", StringComparison.OrdinalIgnoreCase))
                    continue;

                // A ServiceProfile includes every address visible to Windows,
                // including WSL, Docker, and Hyper-V adapters. The source of
                // the mDNS reply is the address that actually reached this
                // machine over the current LAN, so it is the only reliable
                // endpoint to prefer. Advertised A records remain a fallback
                // for responders that return a packet without a usable source.
                var address = eventArgs.RemoteEndPoint.Address;
                if (!LanAddressPolicy.IsAllowed(address))
                {
                    address = records.OfType<ARecord>()
                        .FirstOrDefault(record => NameEquals(record.Name, service.Target))?.Address;
                }
                if (address is null || !LanAddressPolicy.IsAllowed(address)) continue;
                var instanceId = properties.GetValueOrDefault("instance-id") ?? instance.ToString();
                if (string.Equals(instanceId, _certificates.GetOrCreate().InstanceId, StringComparison.Ordinal)) continue;
                var displayName = properties.GetValueOrDefault("display-name") ?? instance.ToString().Split('.', 2)[0];
                var endpoint = new UriBuilder(Uri.UriSchemeHttps, address.ToString(), service.Port).Uri;
                var discovered = new DiscoveredRobotCommandServer(instanceId, displayName, endpoint, "v1", DateTimeOffset.UtcNow);
                lock (_gate)
                {
                    _servers[instanceId] = discovered;
                    _status = "Ready";
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not process a LAN Team discovery response.");
        }
    }

    private void PruneExpiredServers()
    {
        var cutoff = DateTimeOffset.UtcNow - DiscoveryTtl;
        bool changed;
        lock (_gate)
        {
            changed = _servers.RemoveAll(pair => pair.Value.LastSeen < cutoff) > 0;
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool NameEquals(DomainName? name, string expected)
        => name is not null && string.Equals(name.ToString().TrimEnd('.'), expected.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsTeamServiceName(DomainName? name)
        => NameEquals(name, ServiceType) || NameEquals(name, ServiceType + ".local");

    private static bool NameEquals(DomainName? left, DomainName? right)
        => left is not null && right is not null && NameEquals(left, right.ToString());

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}

internal static class DictionaryExtensions
{
    public static int RemoveAll<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, Func<KeyValuePair<TKey, TValue>, bool> predicate)
        where TKey : notnull
    {
        var keys = dictionary.Where(predicate).Select(pair => pair.Key).ToArray();
        foreach (var key in keys) dictionary.Remove(key);
        return keys.Length;
    }
}
