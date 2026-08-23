using System.Net;
using MavLinkSharp.Enums;
using Microsoft.Extensions.Logging;
using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Connections;
using RobotCommand.Services.Serial;
using RobotCommand.State;

namespace RobotCommand.Services.Mavlink;

public sealed class MavlinkConnectionProvider : IConnectionProvider
{
    private readonly IMavlinkCodec _codec;
    private readonly IReadOnlyList<IMavlinkAutopilotAdapter> _adapters;
    private readonly IReadOnlyList<IVehicleDiagnosticsProvider> _diagnosticsProviders;
    private readonly MavlinkConnectionRegistry _registry;
    private readonly IEntityStore<string, OperationalCommandRecord> _commands;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISerialDeviceDiscovery _serialDevices;
    private readonly ISerialByteTransportFactory _serialTransports;

    public MavlinkConnectionProvider(
        IMavlinkCodec codec,
        IEnumerable<IMavlinkAutopilotAdapter> adapters,
        IEnumerable<IVehicleDiagnosticsProvider> diagnosticsProviders,
        MavlinkConnectionRegistry registry,
        IEntityStore<string, OperationalCommandRecord> commands,
        IUiDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        ISerialDeviceDiscovery serialDevices,
        ISerialByteTransportFactory serialTransports)
    {
        _codec = codec;
        _adapters = adapters.ToArray();
        _diagnosticsProviders = diagnosticsProviders.ToArray();
        _registry = registry;
        _commands = commands;
        _dispatcher = dispatcher;
        _loggerFactory = loggerFactory;
        _serialDevices = serialDevices;
        _serialTransports = serialTransports;
    }

    public bool Supports(ConnectionMode mode) => mode == ConnectionMode.Mavlink;

    public IManagedConnection Create(ConnectionDefinition definition)
    {
        var options = definition.Mavlink ?? new MavlinkConnectionOptions();
        // MavLinkSharp's Common dialect intentionally does not include the
        // ArduPilotMega extension messages. Decode each ArduPilot connection
        // with the matching dialect so valid extension frames are not counted
        // as decode failures and then misreported as packet loss.
        var codec = options.Autopilot == MavlinkAutopilotProfile.ArduPilot && _codec is MavlinkSharpCodec
            ? new MavlinkSharpCodec(DialectType.Ardupilotmega)
            : _codec;
        MavlinkTransportRoute? bootstrapRoute = null;
        IMavlinkTransport transport = options.Transport switch
        {
            MavlinkTransportKind.UdpListener => CreateUdpTransport(definition.Target, out bootstrapRoute),
            MavlinkTransportKind.Serial => new SerialMavlinkTransport(
                _serialDevices,
                _serialTransports,
                options.SerialDeviceId,
                options.LastKnownPort ?? ParseSerialPort(definition.Target),
                options.EffectiveBaudRate,
                $"MAVLink connection {definition.Name}"),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Transport))
        };
        var connection = new MavlinkConnection(
            definition with { Mavlink = options },
            transport,
            codec,
            _adapters,
            _diagnosticsProviders,
            _commands,
            _dispatcher,
            _registry,
            _loggerFactory.CreateLogger<MavlinkConnection>(),
            bootstrapRoute: bootstrapRoute);
        _registry.Register(connection);
        return connection;
    }

    public static string ParseSerialPort(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("serial", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"MAVLink serial target must use serial://COM-port. Got '{target}'.", nameof(target));
        }
        var port = string.IsNullOrWhiteSpace(uri.Host) ? uri.AbsolutePath.Trim('/') : uri.Host;
        if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(port.AsSpan(3), out var number) || number <= 0)
        {
            throw new ArgumentException($"MAVLink serial target contains an invalid COM port: '{target}'.", nameof(target));
        }
        return port.ToUpperInvariant();
    }

    public static IPEndPoint ParseUdpListener(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("udp-listen", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"MAVLink UDP target must use udp-listen://address:port. Got '{target}'.",
                nameof(target));
        }

        if (uri.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("MAVLink UDP listener port is invalid.", nameof(target));
        }

        var address = uri.Host switch
        {
            "*" or "+" or "0.0.0.0" => IPAddress.Any,
            "localhost" => IPAddress.Loopback,
            _ when IPAddress.TryParse(uri.Host, out var parsed) => parsed,
            _ => Dns.GetHostAddresses(uri.Host)
                .First(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        };
        return new IPEndPoint(address, uri.Port);
    }

    /// <summary>
    /// Parses the optional bootstrap peer on a UDP listener target. A bootstrap
    /// peer is useful for containerized SITL where PX4 is waiting for the GCS
    /// to send the first heartbeat, for example:
    /// udp-listen://0.0.0.0:14550?bootstrap=192.168.0.106:18572
    /// </summary>
    public static IPEndPoint? ParseUdpBootstrapPeer(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("udp-listen", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        var value = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(part => part.Length == 2 && string.Equals(part[0], "bootstrap", StringComparison.OrdinalIgnoreCase))?
            .ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = Uri.UnescapeDataString(value);
        if (!TryParseEndpoint(value, out var endpoint))
        {
            throw new ArgumentException(
                "MAVLink UDP bootstrap endpoint must use host:port, for example 127.0.0.1:18571.",
                nameof(target));
        }
        return endpoint;
    }

    private static UdpMavlinkTransport CreateUdpTransport(string target, out MavlinkTransportRoute? bootstrapRoute)
    {
        var listener = ParseUdpListener(target);
        var bootstrap = ParseUdpBootstrapPeer(target);
        bootstrapRoute = bootstrap is null ? null : new MavlinkTransportRoute(bootstrap.ToString(), bootstrap);
        return new UdpMavlinkTransport(listener);
    }

    private static bool TryParseEndpoint(string value, out IPEndPoint endpoint)
    {
        endpoint = default!;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !int.TryParse(value[(separator + 1)..], out var port) || port is <= 0 or > 65535)
        {
            return false;
        }

        var host = value[..separator].Trim('[', ']');
        try
        {
            var address = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : Dns.GetHostAddresses(host).First(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            endpoint = new IPEndPoint(address, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
