using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RobotCommand.Cli;

internal static partial class CliHumanFormatter
{
    private static readonly JsonSerializerOptions SerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Format(string kind, object? value)
    {
        var document = JsonSerializer.SerializeToElement(value, SerializationOptions);
        var lines = new List<string> { FormatHeading(kind, document) };

        if (document.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            AppendValue(document, lines, 1, includeHeading: false);
        else
            lines.Add($"  {FormatScalar(document)}");

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendValue(JsonElement value, List<string> lines, int indent, bool includeHeading)
    {
        var prefix = new string(' ', indent * 2);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Null ||
                        (includeHeading && IsDisplayProperty(property.Name)))
                        continue;

                    if (IsScalar(property.Value))
                    {
                        lines.Add($"{prefix}{FormatLabel(property.Name)}: {FormatScalar(property.Value)}");
                        continue;
                    }

                    lines.Add($"{prefix}{FormatLabel(property.Name)}:");
                    AppendValue(property.Value, lines, indent + 1, includeHeading: false);
                }

                break;

            case JsonValueKind.Array:
                var items = value.EnumerateArray().ToArray();
                if (items.Length == 0)
                {
                    lines.Add($"{prefix}None");
                    break;
                }

                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    if (IsScalar(item))
                    {
                        lines.Add($"{prefix}- {FormatScalar(item)}");
                        continue;
                    }

                    var display = DisplayValue(item);
                    lines.Add($"{prefix}- {(display ?? $"Item {index + 1}")}");
                    AppendValue(item, lines, indent + 1, includeHeading: true);
                }

                break;
        }
    }

    private static string FormatHeading(string kind, JsonElement value)
    {
        var heading = SentenceCase(FormatLabel(kind.Replace('.', ' ')));
        return value.ValueKind == JsonValueKind.Array
            ? $"{heading} ({value.GetArrayLength()})"
            : heading;
    }

    private static string SentenceCase(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 1
            ? value
            : string.Join(' ', new[] { words[0] }.Concat(words.Skip(1).Select(word =>
                word.All(char.IsUpper) ? word : word.ToLowerInvariant())));
    }

    private static string? DisplayValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "displayName", "name", "title", "label", "id" })
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static bool IsDisplayProperty(string name)
        => name is "displayName" or "name" or "title" or "label";

    private static bool IsScalar(JsonElement value)
        => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    private static string FormatScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Null => "—",
            _ => value.GetRawText()
        };
    }

    private static string FormatLabel(string value)
    {
        var label = AcronymBoundaryRegex().Replace(value, "$1 $2");
        label = WordBoundaryRegex().Replace(label, "$1 $2");
        label = label.Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ', label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((word, index) => FormatWord(word, index == 0)));
    }

    private static string FormatWord(string word, bool first)
    {
        if (word.Equals("id", StringComparison.OrdinalIgnoreCase)) return "ID";
        if (word.Equals("url", StringComparison.OrdinalIgnoreCase)) return "URL";
        if (word.Equals("px4", StringComparison.OrdinalIgnoreCase)) return "PX4";
        if (word.Equals("gps", StringComparison.OrdinalIgnoreCase)) return "GPS";
        if (word.Equals("mavlink", StringComparison.OrdinalIgnoreCase)) return "MAVLink";
        if (word.Equals("agl", StringComparison.OrdinalIgnoreCase)) return "AGL";
        if (word.Equals("msl", StringComparison.OrdinalIgnoreCase)) return "MSL";

        var normalized = word.ToLowerInvariant();
        return first
            ? CultureInfo.InvariantCulture.TextInfo.ToUpper(normalized[0]) + normalized[1..]
            : normalized;
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex("([A-Z]+)([A-Z][a-z])")]
    private static partial Regex AcronymBoundaryRegex();
}
