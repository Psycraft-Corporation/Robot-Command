using RobotCommand.Core;
using RobotCommand.Models;
using RobotCommand.Services.Mavlink;
using RobotCommand.Services.Serial;

namespace RobotCommand.Services.Workflows;

public sealed class Px4ParameterProfileWorkflow : IPx4ParameterProfileWorkflow
{
    private readonly IPx4ParameterProfileStore _profiles;
    private readonly IPx4ParameterService _parameters;
    private readonly IMavlinkConnectionRegistry _connections;
    private readonly IConnectionManagementWorkflow _connectionDefinitions;
    private readonly ReviewedOperationWorkflow _reviewed;

    public Px4ParameterProfileWorkflow(
        IPx4ParameterProfileStore profiles,
        IPx4ParameterService parameters,
        IMavlinkConnectionRegistry connections,
        IConnectionManagementWorkflow connectionDefinitions,
        ReviewedOperationWorkflow reviewed)
    {
        _profiles = profiles;
        _parameters = parameters;
        _connections = connections;
        _connectionDefinitions = connectionDefinitions;
        _reviewed = reviewed;
    }

    public event EventHandler? Changed;
    public string LibraryPath => _profiles.LibraryPath;
    public IReadOnlyList<Px4ParameterProfileWorkflowSnapshot> Profiles => _profiles.Profiles.Select(Map).ToArray();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _profiles.RefreshAsync(cancellationToken); Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Px4ParameterProfileWorkflowSnapshot> ImportAsync(string path, string? name = null, CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.ImportAsync(path, name, cancellationToken); Changed?.Invoke(this, EventArgs.Empty); return Map(profile);
    }

    public Task ExportAsync(string profileId, string path, CancellationToken cancellationToken = default)
        => _profiles.ExportAsync(profileId, path, cancellationToken);

    public async Task<Px4ParameterProfileWorkflowSnapshot> DownloadAsync(string connectionId, string vehicleId, string name, CancellationToken cancellationToken = default)
    {
        var document = await _parameters.DownloadAsync(connectionId, vehicleId, cancellationToken);
        var profile = await _profiles.SaveAsync(name, document, cancellationToken: cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty); return Map(profile);
    }

    public async Task<IReadOnlyList<Px4ParameterDiffWorkflowSnapshot>> CompareAsync(string connectionId, string vehicleId, string profileId, CancellationToken cancellationToken = default)
    {
        var profile = Required(profileId);
        return (await _parameters.CompareAsync(connectionId, vehicleId, profile, cancellationToken))
            .Select(item => new Px4ParameterDiffWorkflowSnapshot(item.Profile.Name, item.Profile.Value, item.LiveValue, item.Kind.ToString(), item.Detail)).ToArray();
    }

    public Task<ReviewedOperationSnapshot> PlanApplyAsync(string connectionId, string vehicleId, string profileId, CancellationToken cancellationToken = default)
    {
        var profile = Required(profileId);
        var backend = ResolveBackend(connectionId, vehicleId);
        var operationKind = backend == "ArduPilot" ? ReviewedOperationKind.ArduPilotParameterApply : ReviewedOperationKind.Px4ParameterApply;
        var findings = new List<WorkflowFinding>();
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(vehicleId))
            findings.Add(new("TARGET_REQUIRED", WorkflowFindingSeverity.Blocking, $"A {backend} connection and vehicle are required."));
        var plan = _reviewed.Plan(operationKind, $"Apply {backend} parameters: {profile.Name}", findings,
            [$"Merge {profile.Document.Parameters.Count} profile parameter(s) into {vehicleId}.", "A current-vehicle backup is created before writes."],
            findings.Count == 0 ? $"Ready to apply the {backend} parameter profile." : findings[0].Message,
            [connectionId, vehicleId, profileId], async token =>
            {
                var result = await _parameters.ApplyAsync(connectionId, vehicleId, profile, token);
                Changed?.Invoke(this, EventArgs.Empty);
                return new ReviewedOperationExecutionResult("", result.Accepted ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed,
                    result.Accepted, result.Accepted ? $"Applied {result.Applied} {backend} parameter(s)." : $"{backend} parameter apply failed.", result.Messages);
            });
        return Task.FromResult(plan);
    }

    private string ResolveBackend(string connectionId, string vehicleId)
    {
        if (_connections.TryGet(connectionId, out var connection) &&
            connection is not null &&
            connection.TryGetVehicle(vehicleId, out _, out _, out _, out var adapter) &&
            adapter?.Profile == MavlinkAutopilotProfile.ArduPilot)
        {
            return "ArduPilot";
        }

        if (_connectionDefinitions.TryGet(connectionId, out var saved) &&
            saved?.Mavlink?.Autopilot == ManagedMavlinkAutopilot.ArduPilot)
        {
            return "ArduPilot";
        }

        return "PX4";
    }

    private Px4ParameterProfile Required(string id) => _profiles.TryGet(id, out var profile) && profile is not null ? profile : throw new KeyNotFoundException("MAVLink parameter profile was not found.");
    private static Px4ParameterProfileWorkflowSnapshot Map(Px4ParameterProfile item) => new(item.Id, item.Name, item.FileName, item.CreatedAt, item.Sha256, item.Document.Parameters.Count, item.FirmwareVersion, item.SourceFileName);
}

public sealed class SikRadioWorkflow : ISikRadioWorkflow
{
    private readonly ISerialDeviceDiscovery _devices;
    private readonly ISikRadioConfigurationService _radio;
    private readonly ISikRadioPairingService _pairing;
    private readonly ReviewedOperationWorkflow _reviewed;
    private readonly Dictionary<string, SerialDeviceDescriptor> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SikRadioProbeWorkflowSnapshot> _probes = new(StringComparer.OrdinalIgnoreCase);

    public SikRadioWorkflow(ISerialDeviceDiscovery devices, ISikRadioConfigurationService radio, ISikRadioPairingService pairing, ReviewedOperationWorkflow reviewed)
    { _devices = devices; _radio = radio; _pairing = pairing; _reviewed = reviewed; }

    public event EventHandler? Changed;
    public IReadOnlyList<SikRadioDeviceWorkflowSnapshot> Devices => _known.Values.Select(Map).OrderBy(item => item.PortName, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        var found = await _devices.DiscoverAsync(cancellationToken);
        _known.Clear();
        foreach (var item in found) { _known[item.DeviceId] = item; _known[item.PortName] = item; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SikRadioProbeWorkflowSnapshot> ProbeAsync(string deviceIdOrPort, CancellationToken cancellationToken = default)
    {
        var device = await ResolveAsync(deviceIdOrPort, cancellationToken);
        var probe = await _radio.ProbeAsync(device, cancellationToken: cancellationToken);
        var snapshot = Map(probe);
        _probes[device.DeviceId] = snapshot; _probes[device.PortName] = snapshot;
        Changed?.Invoke(this, EventArgs.Empty); return snapshot;
    }

    public bool TryGetProbe(string deviceIdOrPort, out SikRadioProbeWorkflowSnapshot? probe) => _probes.TryGetValue(deviceIdOrPort, out probe);

    public async Task<ReviewedOperationSnapshot> PlanConfigureAsync(string deviceIdOrPort, IReadOnlyDictionary<int, int> settings, CancellationToken cancellationToken = default)
    {
        var device = await ResolveAsync(deviceIdOrPort, cancellationToken);
        var errors = _radio.Validate(settings);
        var findings = errors.Select(error => new WorkflowFinding("INVALID_SETTING", WorkflowFindingSeverity.Blocking, error)).ToArray();
        return _reviewed.Plan(ReviewedOperationKind.SikConfigure, $"Configure SiK radio {device.PortName}", findings,
            settings.OrderBy(item => item.Key).Select(item => $"S{item.Key}={item.Value}").ToArray(),
            findings.Length == 0 ? "Review and execute to write settings to the directly attached radio." : findings[0].Message,
            [device.DeviceId], async token =>
            {
                var probe = await _radio.ApplyAsync(new SikRadioApplyRequest(device, 57600, settings), token);
                var mapped = Map(probe); _probes[device.DeviceId] = mapped; _probes[device.PortName] = mapped; Changed?.Invoke(this, EventArgs.Empty);
                return new ReviewedOperationExecutionResult("", ReviewedOperationState.Succeeded, true, "SiK radio settings applied and verified.", []);
            });
    }

    public async Task<ReviewedOperationSnapshot> PlanPairAsync(string sourceDeviceIdOrPort, string targetDeviceIdOrPort, CancellationToken cancellationToken = default)
    {
        var source = await ResolveAsync(sourceDeviceIdOrPort, cancellationToken);
        var target = await ResolveAsync(targetDeviceIdOrPort, cancellationToken);
        var findings = source.DeviceId.Equals(target.DeviceId, StringComparison.OrdinalIgnoreCase)
            ? new[] { new WorkflowFinding("SAME_RADIO", WorkflowFindingSeverity.Blocking, "Choose two different directly attached SiK radios.") } : [];
        return _reviewed.Plan(ReviewedOperationKind.SikPair, $"Pair SiK radios {source.PortName} → {target.PortName}", findings,
            ["Copies air-link settings only; UART speed and transmit power remain unchanged."],
            findings.Length == 0 ? "Review and execute to pair the attached radios." : findings[0].Message,
            [source.DeviceId, target.DeviceId], async token =>
            {
                var result = await _pairing.PairAsync(source, target, cancellationToken: token);
                return new ReviewedOperationExecutionResult("", result.Succeeded ? ReviewedOperationState.Succeeded : ReviewedOperationState.Failed, result.Succeeded, result.Message, result.Details);
            });
    }

    private async Task<SerialDeviceDescriptor> ResolveAsync(string value, CancellationToken token)
    {
        if (_known.TryGetValue(value, out var known)) return known;
        var resolved = await _devices.ResolveAsync(value, value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? value : null, token);
        return resolved ?? throw new KeyNotFoundException($"Serial device '{value}' was not found.");
    }
    private static SikRadioDeviceWorkflowSnapshot Map(SerialDeviceDescriptor item) => new(item.DeviceId, item.DisplayName, item.PortName, item.Manufacturer ?? "Not reported", item.HardwareSummary, item.IsPresent ? "Available" : "Missing", item.IsLikelySikRadio ? "Likely SiK radio" : "Serial device");
    private static SikRadioProbeWorkflowSnapshot Map(SikRadioProbeResult item) => new(item.Device.DeviceId, item.Device.PortName, item.Local.Available, string.Join(" ", new[] { item.Local.FirmwareVersion, item.Local.BoardType }.Where(value => !string.IsNullOrWhiteSpace(value))), item.Local.Error ?? (item.Remote.Available ? "Local and remote SiK radios responded." : "Local SiK radio responded; remote radio unavailable."), item.Local.Settings, item.Remote.Settings, item.ProbedAt);
}
