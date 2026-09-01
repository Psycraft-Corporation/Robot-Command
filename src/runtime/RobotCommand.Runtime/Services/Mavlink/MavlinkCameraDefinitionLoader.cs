using System.Globalization;
using System.Xml.Linq;
using RobotCommand.Models;

namespace RobotCommand.Services.Mavlink;

public interface IMavlinkCameraDefinitionLoader
{
    Task<IReadOnlyList<MavlinkCameraSettingRecord>> LoadAsync(string definitionUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the XML camera definition advertised by CAMERA_INFORMATION. QGC
/// definitions are intentionally reduced to the portable setting fields used
/// by Robot Command. Only HTTP(S) definitions are fetched automatically.
/// </summary>
public sealed class MavlinkCameraDefinitionLoader : IMavlinkCameraDefinitionLoader, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public MavlinkCameraDefinitionLoader(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _ownsClient = client is null;
    }

    public async Task<IReadOnlyList<MavlinkCameraSettingRecord>> LoadAsync(
        string definitionUri,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(definitionUri, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return [];
        }

        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        return Parse(document);
    }

    public static IReadOnlyList<MavlinkCameraSettingRecord> Parse(XDocument document)
    {
        var settings = new List<MavlinkCameraSettingRecord>();
        foreach (var parameter in document.Descendants()
                     .Where(item => item.Name.LocalName.Equals("parameter", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Attribute(parameter, "name", "id");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var label = Attribute(parameter, "humanName", "label", "displayName") ??
                ChildText(parameter, "description", "shortDescription") ?? name;
            var type = Attribute(parameter, "type") ?? "string";
            var units = Attribute(parameter, "units", "unit") ?? string.Empty;
            var defaultValue = Attribute(parameter, "default", "defaultValue");
            var minimum = Number(parameter, "min", "minimum");
            var maximum = Number(parameter, "max", "maximum");
            var increment = Number(parameter, "increment", "step");
            var options = parameter.Descendants()
                .Where(item => item.Name.LocalName is "value" or "element" or "option")
                .Select(item => new MavlinkCameraSettingOption(
                    Attribute(item, "code", "value", "id") ?? string.Empty,
                    Attribute(item, "name", "label", "humanName") ?? item.Value.Trim()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value) || !string.IsNullOrWhiteSpace(item.Label))
                .ToArray();

            settings.Add(new(name, label, type, units, DefaultValue: defaultValue,
                Minimum: minimum, Maximum: maximum, Increment: increment,
                Options: options.Length == 0 ? null : options));
        }
        return settings;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static string? Attribute(XElement element, params string[] names)
        => names.Select(name => element.Attribute(name)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ChildText(XElement element, params string[] names)
        => element.Elements()
            .Where(child => names.Contains(child.Name.LocalName, StringComparer.OrdinalIgnoreCase))
            .Select(child => child.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static double? Number(XElement element, params string[] names)
        => double.TryParse(Attribute(element, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
