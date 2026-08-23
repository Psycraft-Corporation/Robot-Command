using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace RobotCommand.Services.Serial;

public sealed record SikRadioSnapshot(
    bool Available,
    bool IsRemote,
    string? FirmwareVersion,
    string? BoardType,
    string? FrequencyBand,
    IReadOnlyDictionary<int, int> Settings,
    string RawResponse,
    string? Error = null)
{
    public int? Setting(int id) => Settings.TryGetValue(id, out var value) ? value : null;
}

public sealed record SikRadioProbeResult(
    SerialDeviceDescriptor Device,
    SikRadioSnapshot Local,
    SikRadioSnapshot Remote,
    DateTimeOffset ProbedAt);

public sealed record SikRadioApplyRequest(
    SerialDeviceDescriptor Device,
    int BaudRate,
    IReadOnlyDictionary<int, int> LocalSettings,
    IReadOnlyDictionary<int, int>? RemoteSettings = null);

public interface ISikRadioConfigurationService
{
    Task<SikRadioProbeResult> ProbeAsync(
        SerialDeviceDescriptor device,
        int baudRate = 57600,
        CancellationToken cancellationToken = default);
    Task<SikRadioProbeResult> ApplyAsync(
        SikRadioApplyRequest request,
        CancellationToken cancellationToken = default);
    IReadOnlyList<string> Validate(IReadOnlyDictionary<int, int> settings);
}

public sealed partial class SikRadioConfigurationService(
    ISerialByteTransportFactory transports) : ISikRadioConfigurationService
{
    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan SpacedEscapeCharacterDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] PortReleaseRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];

    public async Task<SikRadioProbeResult> ProbeAsync(
        SerialDeviceDescriptor device,
        int baudRate = 57600,
        CancellationToken cancellationToken = default)
    {
        await using var session = await OpenCommandSessionAsync(device, baudRate, cancellationToken);
        var local = await ReadSnapshotAsync(session, remote: false, cancellationToken);
        var remote = local.Available
            ? await ReadSnapshotAsync(session, remote: true, cancellationToken)
            : Unavailable(true, "The local radio did not enter command mode.");
        await session.ExitAsync(cancellationToken);
        return new SikRadioProbeResult(device, local, remote, DateTimeOffset.UtcNow);
    }

    public async Task<SikRadioProbeResult> ApplyAsync(
        SikRadioApplyRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = Validate(request.LocalSettings).ToList();
        if (request.RemoteSettings is not null) errors.AddRange(Validate(request.RemoteSettings));
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors.Distinct()));
        ValidatePairCompatibility(request.LocalSettings, request.RemoteSettings);

        var session = await OpenCommandSessionAsync(request.Device, request.BaudRate, cancellationToken);
        await using var sessionScope = session;
        var originalLocal = await ReadSnapshotAsync(session, false, cancellationToken);
        if (!originalLocal.Available) throw new IOException(originalLocal.Error ?? "The local SiK radio is unavailable.");
        var originalRemote = request.RemoteSettings is null
            ? Unavailable(true, "Remote configuration was not requested.")
            : await ReadSnapshotAsync(session, true, cancellationToken);
        if (request.RemoteSettings is not null && !originalRemote.Available)
        {
            throw new IOException("The paired remote SiK radio could not be contacted; no settings were changed.");
        }

        var remoteStaged = false;
        try
        {
            if (request.RemoteSettings is not null)
            {
                await StageSettingsAsync(session, request.RemoteSettings, true, cancellationToken);
                remoteStaged = true;
            }
            await StageSettingsAsync(session, request.LocalSettings, false, cancellationToken);

            if (request.RemoteSettings is not null)
            {
                await session.CommandAsync("RT&W", cancellationToken, requireOk: true);
            }
            await session.CommandAsync("AT&W", cancellationToken, requireOk: true);

            if (request.RemoteSettings is not null)
            {
                await session.CommandAsync("RTZ", cancellationToken, requireOk: false);
            }
            await session.CommandAsync("ATZ", cancellationToken, requireOk: false);
        }
        catch
        {
            if (remoteStaged && originalRemote.Available)
            {
                try
                {
                    await StageSettingsAsync(session, originalRemote.Settings, true, CancellationToken.None);
                    await session.CommandAsync("RT&W", CancellationToken.None, requireOk: true);
                }
                catch { }
            }
            throw;
        }

        // ATZ closes/restarts the local radio. Release the serial-port lease
        // before reopening it for read-back verification.
        await session.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return await ProbeAsync(request.Device, request.BaudRate, cancellationToken);
    }

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<int, int> settings)
    {
        var errors = new List<string>();
        foreach (var (id, value) in settings)
        {
            var valid = id switch
            {
                0 => value is >= 0 and <= 255,
                1 => value is >= 1 and <= 921,
                2 => value is >= 2 and <= 250,
                3 => value is >= 0 and <= 499,
                4 => value is >= 0 and <= 30,
                5 or 7 or 13 or 14 => value is >= 0 and <= 1,
                6 => value is >= 0 and <= 2,
                8 or 9 => value is >= 100000 and <= 1000000,
                10 => value is >= 1 and <= 50,
                11 => value is >= 0 and <= 100,
                12 => value is >= 0 and <= 255,
                _ => false
            };
            if (!valid) errors.Add($"SiK parameter S{id} has invalid value {value}.");
        }
        if (settings.TryGetValue(8, out var minimum) && settings.TryGetValue(9, out var maximum) && minimum >= maximum)
        {
            errors.Add("SiK minimum frequency must be below maximum frequency.");
        }
        return errors;
    }

    private async Task<CommandSession> OpenCommandSessionAsync(
        SerialDeviceDescriptor device,
        int baudRate,
        CancellationToken cancellationToken)
    {
        // A MAVLink transport has its own read loop. Although DisconnectAsync waits
        // for it to stop, Windows can briefly keep the FTDI virtual COM handle in a
        // closing state. This is especially visible when Pair radios immediately
        // follows disconnecting a saved serial connection. Retry only that transient
        // sharing violation; a persistent external owner still gets a clear error.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await OpenCommandSessionOnceAsync(device, baudRate, cancellationToken);
            }
            catch (Exception ex) when (IsPortAccessDenied(ex) && attempt < PortReleaseRetryDelays.Length)
            {
                await Task.Delay(PortReleaseRetryDelays[attempt], cancellationToken);
            }
        }
    }

    private async Task<CommandSession> OpenCommandSessionOnceAsync(
        SerialDeviceDescriptor device,
        int baudRate,
        CancellationToken cancellationToken)
    {
        var transport = transports.Create();
        try
        {
            // Some FTDI-backed RFD SiK radios gate local AT command mode on a
            // modem-control line. Keep normal MAVLink serial links unchanged, but
            // assert RTS for this short-lived configuration session.
            await transport.OpenAsync(
                new SerialPortSettings(device.PortName, baudRate, RtsEnable: true),
                "SiK radio configuration",
                cancellationToken);
            var session = new CommandSession(transport);
            if (!await EnterCommandModeAsync(session, cancellationToken))
            {
                await session.DisposeAsync();
                throw new IOException(
                    $"{device.PortName} did not respond to the SiK command-mode sequence. " +
                    "Neither the contiguous nor spaced +++ escape sequence was accepted. " +
                    "For a directly attached air radio, stop the vehicle-side TELEM/UART stream (disconnect it or power down the flight controller) before retrying, or configure it remotely through a paired ground radio.");
            }
            return session;
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> EnterCommandModeAsync(CommandSession session, CancellationToken cancellationToken)
    {
        // RFD SiK firmware revisions differ here: most accept a single "+++" write,
        // while some only recognize the escape when the characters are paced. Try
        // both without a newline, retaining the required silence on either side.
        foreach (var spaced in new[] { false, true })
        {
            await Task.Delay(GuardInterval, cancellationToken);
            if (spaced)
            {
                await session.WriteRawAsync("+", cancellationToken);
                await Task.Delay(SpacedEscapeCharacterDelay, cancellationToken);
                await session.WriteRawAsync("+", cancellationToken);
                await Task.Delay(SpacedEscapeCharacterDelay, cancellationToken);
                await session.WriteRawAsync("+", cancellationToken);
            }
            else
            {
                await session.WriteRawAsync("+++", cancellationToken);
            }

            await Task.Delay(GuardInterval, cancellationToken);
            var response = await session.ReadResponseAsync(cancellationToken);
            if (response.Contains("OK", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsPortAccessDenied(Exception exception)
        => exception is UnauthorizedAccessException ||
           exception is IOException io &&
           io.Message.Contains("access", StringComparison.OrdinalIgnoreCase) &&
           io.Message.Contains("denied", StringComparison.OrdinalIgnoreCase);

    private static async Task<SikRadioSnapshot> ReadSnapshotAsync(
        CommandSession session,
        bool remote,
        CancellationToken cancellationToken)
    {
        var prefix = remote ? "RT" : "AT";
        try
        {
            var version = await session.CommandAsync(prefix + "I0", cancellationToken);
            var board = await session.CommandAsync(prefix + "I2", cancellationToken);
            var frequency = await session.CommandAsync(prefix + "I3", cancellationToken);
            var parameters = await session.CommandAsync(prefix + "I5", cancellationToken);
            var settings = ParseSettings(parameters);
            if (settings.Count == 0)
            {
                return Unavailable(remote, remote
                    ? "No paired remote SiK radio responded."
                    : "The local device did not return SiK settings.", parameters);
            }
            return new SikRadioSnapshot(true, remote, CleanResponse(version), CleanResponse(board), CleanResponse(frequency), settings, parameters);
        }
        catch (TimeoutException)
        {
            return Unavailable(remote, remote ? "No paired remote SiK radio responded." : "The local SiK radio timed out.");
        }
    }

    private static async Task StageSettingsAsync(
        CommandSession session,
        IReadOnlyDictionary<int, int> settings,
        bool remote,
        CancellationToken cancellationToken)
    {
        var prefix = remote ? "RTS" : "ATS";
        foreach (var (id, value) in settings.OrderBy(item => item.Key))
        {
            await session.CommandAsync($"{prefix}{id}={value}", cancellationToken, requireOk: true);
            var readback = await session.CommandAsync($"{prefix}{id}?", cancellationToken);
            if (!ReadbackRegex(id).IsMatch(readback) && !readback.Contains(value.ToString(), StringComparison.Ordinal))
            {
                throw new IOException($"SiK parameter S{id} did not verify after staging.");
            }
        }
    }

    private static void ValidatePairCompatibility(
        IReadOnlyDictionary<int, int> local,
        IReadOnlyDictionary<int, int>? remote)
    {
        if (remote is null) return;
        int[] paired = [2, 3, 5, 6, 8, 9, 10, 13];
        var mismatch = paired.Where(id => local.TryGetValue(id, out var localValue) &&
                                          remote.TryGetValue(id, out var remoteValue) &&
                                          localValue != remoteValue).ToArray();
        if (mismatch.Length > 0)
        {
            throw new InvalidOperationException($"Pair-critical SiK settings must match on both radios: {string.Join(", ", mismatch.Select(id => $"S{id}"))}.");
        }
    }

    private static Dictionary<int, int> ParseSettings(string response)
    {
        var result = new Dictionary<int, int>();
        foreach (Match match in SettingRegex().Matches(response))
        {
            // Different SiK firmware revisions can expose extension parameters beyond
            // the S0-S14 set that this configuration surface understands. Keep the
            // raw response for diagnostics, but never offer an unknown parameter for
            // validation or staging: writing it back could corrupt a newer firmware's
            // extension setting and currently prevents otherwise valid pairing.
            if (int.TryParse(match.Groups[1].Value, out var id) &&
                id is >= 0 and <= 14 &&
                int.TryParse(match.Groups[2].Value, out var value))
            {
                result[id] = value;
            }
        }
        return result;
    }

    private static SikRadioSnapshot Unavailable(bool remote, string error, string raw = "")
        => new(false, remote, null, null, null, new Dictionary<int, int>(), raw, error);

    private static string CleanResponse(string response)
        => string.Join(" ", response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Equals("OK", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("AT", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("RT", StringComparison.OrdinalIgnoreCase)));

    [GeneratedRegex(@"S(\d+)\s*:\s*(?:[^=\r\n]*=\s*)?(-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SettingRegex();
    private static Regex ReadbackRegex(int id) => new($@"S{id}\s*[:=].*", RegexOptions.IgnoreCase);

    private sealed class CommandSession : IAsyncDisposable
    {
        private readonly ISerialByteTransport _transport;
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();

        public CommandSession(ISerialByteTransport transport)
        {
            _transport = transport;
            _transport.BytesReceived += OnBytesReceived;
        }

        public Task WriteRawAsync(string text, CancellationToken cancellationToken)
            => _transport.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken);

        public async Task<string> CommandAsync(string command, CancellationToken cancellationToken, bool requireOk = false)
        {
            while (_received.Reader.TryRead(out _)) { }
            await WriteRawAsync($"\r\n{command}\r\n", cancellationToken);
            var response = await ReadResponseAsync(cancellationToken);
            if (requireOk && !response.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"SiK command {command} failed: {response.Trim()}");
            }
            return response;
        }

        public async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResponseTimeout);
            var builder = new StringBuilder();
            byte[] first;
            try { first = await _received.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The SiK radio did not respond before the timeout.");
            }
            builder.Append(Encoding.ASCII.GetString(first));
            while (true)
            {
                // RFD SiK radios echo an AT command before their terminal result.
                // FTDI can deliver that echo and the subsequent OK/ERROR in separate
                // reads more than the old 250 ms apart. Do not report an empty or
                // echoed response as a failed command before a terminal result has
                // had time to arrive.
                if (HasTerminalResult(builder)) return builder.ToString();
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                quiet.CancelAfter(TimeSpan.FromMilliseconds(900));
                try
                {
                    var chunk = await _received.Reader.ReadAsync(quiet.Token);
                    builder.Append(Encoding.ASCII.GetString(chunk));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            }
            return builder.ToString();
        }

        private static bool HasTerminalResult(StringBuilder response)
            => response.ToString().Contains("OK", StringComparison.OrdinalIgnoreCase) ||
               response.ToString().Contains("ERROR", StringComparison.OrdinalIgnoreCase);

        public async Task ExitAsync(CancellationToken cancellationToken)
        {
            try { await CommandAsync("ATO", cancellationToken); }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _transport.BytesReceived -= OnBytesReceived;
            await _transport.DisposeAsync();
        }

        private void OnBytesReceived(object? sender, SerialBytesReceived e) => _received.Writer.TryWrite(e.Payload.ToArray());
    }
}
