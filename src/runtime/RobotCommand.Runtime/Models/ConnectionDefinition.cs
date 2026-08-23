using System.Security.Cryptography;
using System.Text;

namespace RobotCommand.Models;

public sealed record ConnectionDefinition(
    string Id,
    string Name,
    string Target,
    ConnectionMode Mode = ConnectionMode.Direct,
    bool AutoConnect = false,
    bool AutoReconnect = true,
    string? Description = null,
    bool IsGhost = false,
    MavlinkConnectionOptions? Mavlink = null,
    LinkdConnectionOptions? Linkd = null)
{
    public static ConnectionDefinition CreateDirect(
        string name,
        string target,
        bool autoConnect = false,
        bool autoReconnect = true,
        string? description = null)
        => new(
            ConnectionId.Create(name, target),
            name.Trim(),
            target.Trim(),
            ConnectionMode.Direct,
            autoConnect,
            autoReconnect,
            description?.Trim());
}

public enum MavlinkTransportKind
{
    UdpListener,
    Serial
}

public enum MavlinkAutopilotProfile
{
    Px4,
    ArduPilot
}

public sealed record MavlinkSystemAlias(byte SystemId, string Name);

public sealed record MavlinkConnectionOptions(
    MavlinkTransportKind Transport = MavlinkTransportKind.UdpListener,
    MavlinkAutopilotProfile Autopilot = MavlinkAutopilotProfile.Px4,
    byte SourceSystemId = 255,
    byte SourceComponentId = 190,
    IReadOnlyList<MavlinkSystemAlias>? SystemAliases = null,
    int? BaudRate = null,
    string? SerialDeviceId = null,
    string? LastKnownPort = null)
{
    public IReadOnlyList<MavlinkSystemAlias> Aliases => SystemAliases ?? [];

    public string DisplayNameFor(byte systemId)
        => Aliases.FirstOrDefault(item => item.SystemId == systemId)?.Name?.Trim() is { Length: > 0 } alias
            ? alias
            : $"{AutopilotDisplayName} System {systemId}";

    public string AutopilotDisplayName => Autopilot switch
    {
        MavlinkAutopilotProfile.Px4 => "PX4",
        MavlinkAutopilotProfile.ArduPilot => "ArduPilot",
        _ => Autopilot.ToString()
    };

    public int EffectiveBaudRate => BaudRate is > 0 ? BaudRate.Value : 57600;
}

public sealed record LinkdConnectionOptions(
    string TransportPlugin = "sik_serial",
    int BaudRate = 57600,
    string? SerialDeviceId = null,
    string? LastKnownPort = null,
    string? RadioProfileKey = null,
    string? WireProfilePath = null)
{
    public int EffectiveBaudRate => BaudRate is > 0 ? BaudRate : 57600;

    public bool HasSelectedDevice =>
        !string.IsNullOrWhiteSpace(LastKnownPort) || !string.IsNullOrWhiteSpace(SerialDeviceId);
}

public sealed record ConnectionCredentials(string? ApiKey, string? BearerToken)
{
    public static ConnectionCredentials Empty { get; } = new(null, null);

    public ConnectionCredentials Normalize()
        => new(
            string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
            string.IsNullOrWhiteSpace(BearerToken) ? null : BearerToken.Trim());
}

public static class ConnectionId
{
    public static string Create(string name, string target)
    {
        var slug = Slugify(name);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{name.Trim()}\n{target.Trim()}"));
        var suffix = Convert.ToHexString(bytes.AsSpan(0, 5)).ToLowerInvariant();
        return $"{slug}-{suffix}";
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        var previousDash = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
                continue;
            }

            if (previousDash || builder.Length == 0)
            {
                continue;
            }

            builder.Append('-');
            previousDash = true;
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "logos" : slug;
    }
}
