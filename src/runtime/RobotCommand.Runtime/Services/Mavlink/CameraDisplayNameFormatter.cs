namespace RobotCommand.Services.Mavlink;

public static class CameraDisplayNameFormatter
{
    public static string Format(string? vendor, string? model, byte systemId, byte componentId)
    {
        var parts = new[] { vendor, model }.Select(Sanitize).ToArray();
        if (parts.Any(item => item is null)) return Fallback(systemId, componentId);
        var name = string.Join(" ", parts.Where(item => !string.IsNullOrWhiteSpace(item)));
        return name.Length == 0 ? Fallback(systemId, componentId) : name;
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.EnumerateRunes().Any(rune => rune.Value == 0xFFFD)) return null;
        var cleaned = new string(value.Where(character => character != '\0' && !char.IsControl(character)).ToArray()).Trim();
        return cleaned;
    }

    private static string Fallback(byte systemId, byte componentId) => $"MAVLink camera {systemId}/{componentId}";
}
