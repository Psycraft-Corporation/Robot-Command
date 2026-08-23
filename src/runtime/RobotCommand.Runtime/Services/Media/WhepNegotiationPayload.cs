using System.Text.Json;

namespace RobotCommand.Services.Media;

public sealed record WhepNegotiationPayload(
    string? AuthToken,
    int PayloadType,
    bool UseLinkHeaders,
    string? StunServer,
    string? TurnServer)
{
    public static WhepNegotiationPayload Default { get; } = new(null, 0, true, null, null);

    public static WhepNegotiationPayload Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.TrimStart().StartsWith('{'))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Default;
            }

            var payloadType = ReadInt(root, "videoPayloadType", "video_payload_type", "payloadType", "payload_type") ?? 0;
            return new WhepNegotiationPayload(
                ReadString(root, "authToken", "auth_token", "bearerToken", "bearer_token"),
                payloadType is >= 96 and <= 127 ? payloadType : 0,
                ReadBool(root, "useLinkHeaders", "use_link_headers") ?? true,
                ReadString(root, "stunServer", "stun_server"),
                ReadString(root, "turnServer", "turn_server"));
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static bool? ReadBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        return null;
    }
}
