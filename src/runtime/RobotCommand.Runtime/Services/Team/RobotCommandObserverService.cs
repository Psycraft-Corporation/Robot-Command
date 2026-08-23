using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Sdk;
using RobotCommand.State;
using TeamApi = RobotCommand.Sdk.Team.V1;

namespace RobotCommand.Services.Team;

/// <summary>Read-only, session-scoped observations of other Robot Command instances.</summary>
public interface IRobotCommandObserverService
{
    IReadOnlyList<RobotCommandObserverRecord> Observers { get; }
    event EventHandler? Changed;
    Task<RobotCommandServerProbe> ProbeAsync(string endpoint, CancellationToken cancellationToken = default);
    Task ConnectAsync(string endpoint, string fingerprint, string displayName, string pairingPassphrase = "", RobotCommandPairingInvitation? pairing = null, CancellationToken cancellationToken = default);
    Task<RobotCommandServerProbe> ConnectWithPassphraseAsync(string endpoint, string passphrase, string displayName, RobotCommandPairingInvitation? pairing = null, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string observerId, CancellationToken cancellationToken = default);
}

public sealed record RobotCommandObserverRecord(
    string Id,
    string Endpoint,
    string DisplayName,
    string Status,
    string Detail,
    string Fingerprint = "",
    DateTimeOffset? ConnectedAt = null,
    string DisconnectReason = "");

public sealed class RobotCommandObserverService : IRobotCommandObserverService, IHostedService
{
    private sealed class ObserverState
    {
        public required RobotCommandObserverRecord Record { get; set; }
        public RobotCommandObserverSession? Session { get; set; }
        public HashSet<string> ConnectionIds { get; } = [];
        public HashSet<string> RuntimeIds { get; } = [];
        public HashSet<string> VehicleIds { get; } = [];
        public HashSet<string> TelemetryIds { get; } = [];
        public HashSet<string> DiagnosticIds { get; } = [];
        public HashSet<string> LinkIds { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ObserverState> _observers = new(StringComparer.Ordinal);
    private readonly IEntityStore<string, ConnectionRecord> _connections;
    private readonly IEntityStore<string, RuntimeRecord> _runtimes;
    private readonly IEntityStore<string, VehicleRecord> _vehicles;
    private readonly IEntityStore<string, VehicleTelemetryRecord> _telemetry;
    private readonly IEntityStore<string, VehicleDiagnosticsSnapshot> _diagnostics;
    private readonly IEntityStore<string, LinkRecord> _links;
    private readonly IUiDispatcher _dispatcher;

    public RobotCommandObserverService(
        IEntityStore<string, ConnectionRecord> connections,
        IEntityStore<string, RuntimeRecord> runtimes,
        IEntityStore<string, VehicleRecord> vehicles,
        IEntityStore<string, VehicleTelemetryRecord> telemetry,
        IEntityStore<string, VehicleDiagnosticsSnapshot> diagnostics,
        IEntityStore<string, LinkRecord> links,
        IUiDispatcher dispatcher)
    {
        _connections = connections;
        _runtimes = runtimes;
        _vehicles = vehicles;
        _telemetry = telemetry;
        _diagnostics = diagnostics;
        _links = links;
        _dispatcher = dispatcher;
    }

    public IReadOnlyList<RobotCommandObserverRecord> Observers
    {
        get { lock (_gate) return _observers.Values.Select(item => item.Record).OrderBy(item => item.DisplayName).ToArray(); }
    }

    public event EventHandler? Changed;

    public Task<RobotCommandServerProbe> ProbeAsync(string endpoint, CancellationToken cancellationToken = default)
        => RobotCommandLanClient.ProbeAsync(NormalizeEndpoint(endpoint), cancellationToken);

    public async Task<RobotCommandServerProbe> ConnectWithPassphraseAsync(string endpoint, string passphrase, string displayName, RobotCommandPairingInvitation? pairing = null, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(endpoint, cancellationToken);
        if (pairing is not null && !string.Equals(RobotCommandLanClient.NormalizeFingerprint(probe.ObservedCertificateFingerprint), pairing.CertificateFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The Robot Command certificate does not match the pairing link.");
        await ConnectAsync(probe.Endpoint.ToString(), probe.ObservedCertificateFingerprint, displayName, passphrase, pairing, cancellationToken);
        return probe;
    }

    public async Task ConnectAsync(string endpoint, string fingerprint, string displayName, string pairingPassphrase = "", RobotCommandPairingInvitation? pairing = null, CancellationToken cancellationToken = default)
    {
        var uri = NormalizeEndpoint(endpoint);
        var id = $"team-observer-{Guid.NewGuid():N}";
        var state = new ObserverState
        {
            Record = new RobotCommandObserverRecord(id, uri.ToString(), displayName, "Requesting access", "Waiting for approval.", fingerprint)
        };
        lock (_gate) _observers.Add(id, state);
        RaiseChanged();
        try
        {
            state.Session = await RobotCommandLanClient.RequestAccessAsync(
                uri,
                fingerprint,
                new RobotCommandClientIdentity(
                    string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName.Trim(),
                    "Robot Command",
                    typeof(RobotCommandObserverService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    $"robot-command-{Environment.MachineName}-{Guid.NewGuid():N}"),
                pairingPassphrase,
                pairing,
                status => UpdateStatus(id, status.State.ToString(), status.Message),
                cancellationToken);
            state.Session.Disconnected += (_, notice) => _ = HandleRemoteDisconnectAsync(id, notice);
            state.Session.SnapshotChanged += (_, snapshot) => _ = ApplySnapshotAsync(id, snapshot);
            state.Record = state.Record with { Status = "Observing", Detail = "Read-only snapshot stream is active.", ConnectedAt = DateTimeOffset.UtcNow };
            if (state.Session.CurrentSnapshot is { } snapshot) await ApplySnapshotAsync(id, snapshot);
            RaiseChanged();
        }
        catch (Exception ex)
        {
            await DisconnectAsync(id, CancellationToken.None);
            throw new InvalidOperationException($"Could not observe {uri}: {ex.Message}", ex);
        }
    }

    private async Task HandleRemoteDisconnectAsync(string observerId, RobotCommandObserverDisconnect notice)
    {
        ObserverState? state;
        lock (_gate) _observers.TryGetValue(observerId, out state);
        if (state is null) return;

        await _dispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (_observers.TryGetValue(observerId, out var current))
                {
                    current.Session = null;
                    current.Record = current.Record with
                    {
                        Status = notice.Reason == RobotCommand.Sdk.Team.V1.ObserverDisconnectReason.AuthenticationRequired
                            ? "Authentication required"
                            : "Disconnected",
                        Detail = notice.Message,
                        DisconnectReason = notice.Reason.ToString()
                    };
                    RemoveProjection(current);
                }
            }
        });
        RaiseChanged();
    }

    public async Task DisconnectAsync(string observerId, CancellationToken cancellationToken = default)
    {
        ObserverState? state;
        lock (_gate)
        {
            if (!_observers.Remove(observerId, out state)) return;
        }
        await _dispatcher.InvokeAsync(() => RemoveProjection(state), cancellationToken);
        if (state.Session is not null) await state.Session.DisposeAsync();
        RaiseChanged();
    }

    private async Task ApplySnapshotAsync(string observerId, TeamApi.RobotCommandSnapshot snapshot)
    {
        await _dispatcher.InvokeAsync(() => ApplySnapshot(observerId, snapshot));
    }

    private void ApplySnapshot(string observerId, TeamApi.RobotCommandSnapshot snapshot)
    {
        ObserverState? state;
        lock (_gate) _observers.TryGetValue(observerId, out state);
        if (state is null) return;

        var remoteConnections = snapshot.Connections.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var nextConnections = new HashSet<string>(StringComparer.Ordinal);
        var nextRuntimes = new HashSet<string>(StringComparer.Ordinal);
        var nextVehicles = new HashSet<string>(StringComparer.Ordinal);
        var nextTelemetry = new HashSet<string>(StringComparer.Ordinal);
        var nextDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        var nextLinks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var remote in snapshot.Connections)
        {
            var id = Prefix(observerId, "connection", remote.Id); nextConnections.Add(id);
            _connections.Upsert(new ConnectionRecord(id, $"{remote.Name} · {state.Record.DisplayName}", remote.Target,
                ConnectionMode.TeamObserver, Availability(remote.State), false, remote.LogosInstanceId, "Remote observer",
                Time(remote.ConnectedAt), Time(remote.ConnectedAt), Time(remote.LastSeen), Time(remote.LastSeen), remote.LastError));
        }
        foreach (var remote in snapshot.Units)
        {
            var vehicleId = Prefix(observerId, "unit", remote.Id); nextVehicles.Add(vehicleId);
            var connectionIds = remote.ConnectionIds.Select(connectionId => Prefix(observerId, "connection", connectionId)).ToArray();
            var stateValue = Availability(remote.State);
            var diagnostics = remote.Diagnostics;
            var readiness = diagnostics?.Reported == true ? diagnostics.OverallStatus.ToString() : "Remote status not reported";
            _vehicles.Upsert(new VehicleRecord(vehicleId, remote.Name, connectionIds, remote.LogosInstanceId, observerId,
                remote.VehicleClass, remote.Domain, "team-observer", stateValue, readiness,
                remote.Telemetry?.LandedState ?? "Unknown", remote.Telemetry?.Armed == true ? "Armed" : "Disarmed",
                diagnostics?.OverallStatus.ToString() ?? "Unknown", [], Time(remote.Telemetry?.ObservedAt), remote.IsGhost));
            var runtimeId = Prefix(observerId, "runtime", remote.Id); nextRuntimes.Add(runtimeId);
            _runtimes.Upsert(new RuntimeRecord(runtimeId, remote.Name, connectionIds, stateValue, "Remote observer", remote.Backend,
                remote.Domain, remote.VehicleClass, diagnostics?.Version ?? "Not reported", diagnostics?.OverallStatus.ToString() ?? "Unknown",
                readiness, [], Time(remote.Telemetry?.ObservedAt), vehicleId, remote.Name, remote.IsGhost));
            if (remote.Telemetry?.Reported == true)
            {
                var telemetryId = Prefix(observerId, "telemetry", remote.Id); nextTelemetry.Add(telemetryId);
                var telemetry = remote.Telemetry;
                _telemetry.Upsert(new VehicleTelemetryRecord(telemetryId, vehicleId,
                    Prefix(observerId, "connection", remote.TelemetryAuthorityConnectionId.Length > 0 ? remote.TelemetryAuthorityConnectionId : remote.ConnectionIds.FirstOrDefault() ?? ""),
                    remote.LogosInstanceId, stateValue, telemetry.Armed, telemetry.LandedState, telemetry.Mode, "Remote observer",
                    diagnostics?.OverallStatus.ToString() ?? "Unknown", readiness, Optional(telemetry.HasLatitudeDegrees, telemetry.LatitudeDegrees), Optional(telemetry.HasLongitudeDegrees, telemetry.LongitudeDegrees),
                    Optional(telemetry.HasAltitudeMslMetres, telemetry.AltitudeMslMetres), Optional(telemetry.HasAltitudeAglMetres, telemetry.AltitudeAglMetres),
                    Optional(telemetry.HasLocalNorthMetres, telemetry.LocalNorthMetres), Optional(telemetry.HasLocalEastMetres, telemetry.LocalEastMetres), Optional(telemetry.HasLocalDownMetres, telemetry.LocalDownMetres),
                    Optional(telemetry.HasVelocityNorthMetresPerSecond, telemetry.VelocityNorthMetresPerSecond), Optional(telemetry.HasVelocityEastMetresPerSecond, telemetry.VelocityEastMetresPerSecond), Optional(telemetry.HasVelocityDownMetresPerSecond, telemetry.VelocityDownMetresPerSecond),
                    Optional(telemetry.HasHeadingDegrees, telemetry.HeadingDegrees), telemetry.Stale, telemetry.Code, telemetry.Message, Time(telemetry.ObservedAt), remote.IsGhost));
            }
            if (diagnostics?.Reported == true)
            {
                var diagnosticsId = Prefix(observerId, "diagnostics", remote.Id); nextDiagnostics.Add(diagnosticsId);
                _diagnostics.Upsert(new VehicleDiagnosticsSnapshot(diagnosticsId, vehicleId,
                    Prefix(observerId, "connection", remote.DiagnosticsAuthorityConnectionId.Length > 0 ? remote.DiagnosticsAuthorityConnectionId : remote.ConnectionIds.FirstOrDefault() ?? ""),
                    diagnostics.Backend, Diagnostic(diagnostics.OverallStatus), diagnostics.Summary, Diagnostic(diagnostics.ArmReadiness), diagnostics.ArmReadinessDetail,
                    Diagnostic(diagnostics.NavigationReadiness), diagnostics.NavigationReadinessDetail, Diagnostic(diagnostics.TelemetryStatus), diagnostics.TelemetryDetail,
                    diagnostics.Checks.Select(check => new VehicleDiagnosticCheck(check.Code, check.Category, check.Name, CheckState(check.State), check.Detail)).ToArray(),
                    diagnostics.RecentMessages.Select(message => new VehicleDiagnosticMessage(message.Id, Time(message.Timestamp), Severity(message.Severity), message.Text, message.Source)).ToArray(),
                    Time(diagnostics.ObservedAt), (byte?)diagnostics.SystemId, (byte?)diagnostics.ComponentId, diagnostics.Version, diagnostics.Mode,
                    remote.Telemetry?.Armed == true, remote.Telemetry?.LandedState ?? "Unknown"));
            }
            foreach (var link in remote.Links)
            {
                var linkId = Prefix(observerId, "link", $"{remote.Id}:{link.Id}"); nextLinks.Add(linkId);
                _links.Upsert(new LinkRecord(linkId, link.Id, Prefix(observerId, "connection", link.ConnectionId), remote.LogosInstanceId,
                    link.Name, link.Kind, link.Direction, link.State, link.Health, "Remote observer", link.Connected, link.Stale,
                    null, null, null, Optional(link.HasRssiDbm, link.RssiDbm), Optional(link.HasSnrDb, link.SnrDb), Optional(link.HasQuality, link.Quality),
                    Optional(link.HasPacketLoss, link.PacketLoss), Optional(link.HasLatencyMilliseconds, link.LatencyMilliseconds), link.Code, link.Message, Time(link.ObservedAt)));
            }
        }
        RemoveMissing(state.ConnectionIds, nextConnections, _connections.Remove); RemoveMissing(state.RuntimeIds, nextRuntimes, _runtimes.Remove);
        RemoveMissing(state.VehicleIds, nextVehicles, _vehicles.Remove); RemoveMissing(state.TelemetryIds, nextTelemetry, _telemetry.Remove);
        RemoveMissing(state.DiagnosticIds, nextDiagnostics, _diagnostics.Remove); RemoveMissing(state.LinkIds, nextLinks, _links.Remove);
        state.ConnectionIds.Clear(); state.ConnectionIds.UnionWith(nextConnections); state.RuntimeIds.Clear(); state.RuntimeIds.UnionWith(nextRuntimes);
        state.VehicleIds.Clear(); state.VehicleIds.UnionWith(nextVehicles); state.TelemetryIds.Clear(); state.TelemetryIds.UnionWith(nextTelemetry);
        state.DiagnosticIds.Clear(); state.DiagnosticIds.UnionWith(nextDiagnostics); state.LinkIds.Clear(); state.LinkIds.UnionWith(nextLinks);
        RaiseChanged();
    }

    private void UpdateStatus(string id, string status, string detail)
    {
        lock (_gate)
            if (_observers.TryGetValue(id, out var state)) state.Record = state.Record with { Status = status, Detail = detail };
        RaiseChanged();
    }
    private void RemoveProjection(ObserverState state)
    {
        foreach (var id in state.ConnectionIds) _connections.Remove(id);
        foreach (var id in state.RuntimeIds) _runtimes.Remove(id);
        foreach (var id in state.VehicleIds) _vehicles.Remove(id);
        foreach (var id in state.TelemetryIds) _telemetry.Remove(id);
        foreach (var id in state.DiagnosticIds) _diagnostics.Remove(id);
        foreach (var id in state.LinkIds) _links.Remove(id);
    }
    private static Uri NormalizeEndpoint(string value)
    {
        var text = value.Trim(); if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : throw new ArgumentException("Enter a valid Robot Command LAN endpoint.", nameof(value));
    }
    private static string Prefix(string observerId, string type, string id) => $"{observerId}:{type}:{id}";
    private static DateTimeOffset Time(Timestamp? value) => value is null || value == Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch) ? DateTimeOffset.UtcNow : value.ToDateTimeOffset();
    private static double? Optional(bool reported, double value) => reported ? value : null;
    private static AvailabilityState Availability(TeamApi.Availability value) => System.Enum.IsDefined(typeof(AvailabilityState), (int)value - 1) ? (AvailabilityState)((int)value - 1) : AvailabilityState.Unknown;
    private static VehicleDiagnosticStatus Diagnostic(TeamApi.DiagnosticStatus value) => System.Enum.IsDefined(typeof(VehicleDiagnosticStatus), (int)value - 1) ? (VehicleDiagnosticStatus)((int)value - 1) : VehicleDiagnosticStatus.Unknown;
    private static VehicleDiagnosticCheckState CheckState(TeamApi.DiagnosticCheckState value) => System.Enum.IsDefined(typeof(VehicleDiagnosticCheckState), (int)value - 1) ? (VehicleDiagnosticCheckState)((int)value - 1) : VehicleDiagnosticCheckState.Unknown;
    private static VehicleDiagnosticSeverity Severity(string value) => System.Enum.TryParse<VehicleDiagnosticSeverity>(value, true, out var result) ? result : VehicleDiagnosticSeverity.Warning;
    private static void RemoveMissing(HashSet<string> previous, HashSet<string> current, Func<string, bool> remove) { foreach (var id in previous.Where(id => !current.Contains(id)).ToArray()) remove(id); }
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Shutdown runs while Avalonia may already be tearing down its dispatcher.
        // The stores disappear with the process, so only revoke live network sessions here.
        ObserverState[] states;
        lock (_gate)
        {
            states = _observers.Values.ToArray();
            _observers.Clear();
        }
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Session is not null) await state.Session.DisposeAsync();
        }
    }
}
