using RobotCommand.Services.Serial;
using Xunit;

namespace RobotCommand.Tests;

public sealed class SikRadioPairingServiceTests
{
    [Fact]
    public async Task PairCopiesOnlyPairCriticalAirLinkSettingsToTarget()
    {
        var source = Device("source", "COM3");
        var target = Device("target", "COM4");
        var targetSettings = new Dictionary<int, int>(Settings(networkId: 25, airSpeed: 64, ecc: 0, framing: 1, transmitPower: 20))
        {
            // RFD firmware may report extension settings outside the supported
            // S0-S14 configuration contract. Pairing must leave them untouched.
            [15] = 131
        };
        var configuration = new FakeConfigurationService(
            Probe(source, Settings(networkId: 73, airSpeed: 32, ecc: 1, framing: 0, transmitPower: 1)),
            Probe(target, targetSettings));

        var result = await new SikRadioPairingService(configuration).PairAsync(source, target);

        Assert.True(result.Succeeded);
        Assert.Equal("PAIRED", result.Code);
        var saved = Assert.Single(configuration.Applied);
        Assert.Equal(73, saved.LocalSettings[3]);
        Assert.Equal(32, saved.LocalSettings[2]);
        Assert.Equal(1, saved.LocalSettings[5]);
        Assert.Equal(0, saved.LocalSettings[6]);
        Assert.DoesNotContain(0, saved.LocalSettings.Keys);
        Assert.DoesNotContain(1, saved.LocalSettings.Keys);
        Assert.DoesNotContain(4, saved.LocalSettings.Keys);
        Assert.DoesNotContain(15, saved.LocalSettings.Keys);
    }

    [Fact]
    public async Task PairReturnsHumanReadableFailureWhenSourceCannotBeProbed()
    {
        var source = Device("source", "COM3");
        var target = Device("target", "COM4");
        var configuration = new FakeConfigurationService(
            Probe(source, available: false, error: "COM3 did not respond."),
            Probe(target, Settings()));

        var result = await new SikRadioPairingService(configuration).PairAsync(source, target);

        Assert.False(result.Succeeded);
        Assert.Equal("SOURCE_PROBE_FAILED", result.Code);
        Assert.Contains("COM3 did not respond", result.DisplayText);
        Assert.Empty(configuration.Applied);
    }

    [Fact]
    public async Task PairReturnsActionableResultWhenTargetPortIsOwnedByAnotherProgram()
    {
        var source = Device("source", "COM3");
        var target = Device("target", "COM4");
        var configuration = new AccessDeniedOnTargetConfigurationService(source, target);

        var result = await new SikRadioPairingService(configuration).PairAsync(source, target);

        Assert.False(result.Succeeded);
        Assert.Equal("PORT_ACCESS_DENIED", result.Code);
        Assert.Contains("COM4", result.DisplayText);
        Assert.Contains("QGroundControl", result.DisplayText);
    }

    private static SerialDeviceDescriptor Device(string id, string port)
        => new(id, port, $"SiK {port}", IsLikelySikRadio: true);

    private static SikRadioProbeResult Probe(
        SerialDeviceDescriptor device,
        IReadOnlyDictionary<int, int>? settings = null,
        bool available = true,
        string? error = null)
        => new(
            device,
            new SikRadioSnapshot(available, false, "RFD SiK", "HM-TRP", "915 MHz", settings ?? new Dictionary<int, int>(), "", error),
            new SikRadioSnapshot(false, true, null, null, null, new Dictionary<int, int>(), "", "No remote"),
            DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<int, int> Settings(
        int networkId = 73,
        int airSpeed = 32,
        int ecc = 1,
        int framing = 0,
        int transmitPower = 1)
        => new Dictionary<int, int>
        {
            [0] = 26,
            [1] = 57,
            [2] = airSpeed,
            [3] = networkId,
            [4] = transmitPower,
            [5] = ecc,
            [6] = framing,
            [7] = 1,
            [8] = 915000,
            [9] = 928000,
            [10] = 50,
            [11] = 10,
            [12] = 0,
            [13] = 0,
            [14] = 0
        };

    private sealed class FakeConfigurationService(params SikRadioProbeResult[] probes) : ISikRadioConfigurationService
    {
        private readonly Dictionary<string, SikRadioProbeResult> _probes = probes.ToDictionary(item => item.Device.DeviceId, StringComparer.OrdinalIgnoreCase);
        public List<SikRadioApplyRequest> Applied { get; } = [];

        public Task<SikRadioProbeResult> ProbeAsync(SerialDeviceDescriptor device, int baudRate = 57600, CancellationToken cancellationToken = default)
            => Task.FromResult(_probes[device.DeviceId]);

        public Task<SikRadioProbeResult> ApplyAsync(SikRadioApplyRequest request, CancellationToken cancellationToken = default)
        {
            Applied.Add(request);
            var original = _probes[request.Device.DeviceId];
            var verified = original with { Local = original.Local with { Settings = new Dictionary<int, int>(request.LocalSettings) } };
            _probes[request.Device.DeviceId] = verified;
            return Task.FromResult(verified);
        }

        public IReadOnlyList<string> Validate(IReadOnlyDictionary<int, int> settings) => [];
    }

    private sealed class AccessDeniedOnTargetConfigurationService(
        SerialDeviceDescriptor source,
        SerialDeviceDescriptor target) : ISikRadioConfigurationService
    {
        public Task<SikRadioProbeResult> ProbeAsync(SerialDeviceDescriptor device, int baudRate = 57600, CancellationToken cancellationToken = default)
            => Task.FromResult(Probe(source, Settings()));

        public Task<SikRadioProbeResult> ApplyAsync(SikRadioApplyRequest request, CancellationToken cancellationToken = default)
            => request.Device.DeviceId == target.DeviceId
                ? Task.FromException<SikRadioProbeResult>(new UnauthorizedAccessException("Access to the path 'COM4' is denied."))
                : throw new NotSupportedException();

        public IReadOnlyList<string> Validate(IReadOnlyDictionary<int, int> settings) => [];
    }
}
