using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryDocumentCodec
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions(writeIndented: true);
    private static readonly JsonSerializerOptions CompactJsonOptions = CreateOptions(writeIndented: false);

    public GeometryDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Geometry document was empty.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<GeometryDocument>(json, JsonOptions)
                           ?? throw new InvalidDataException("Geometry document was empty.");
            var normalized = Normalize(document);
            if (string.Equals(normalized.SchemaVersion, GeometryDocument.LegacyLogosSchemaVersion, StringComparison.Ordinal))
            {
                var upgraded = normalized with
                {
                    SchemaVersion = GeometryDocument.CurrentSchemaVersion,
                    Origin = GeometryDocumentOrigin.Imported,
                    // The legacy hash deliberately cannot be reused because the
                    // Robot Command canonical form includes altitude reference.
                    ContentSha256 = string.Empty,
                    SourceSummary = string.IsNullOrWhiteSpace(normalized.SourceSummary)
                        ? "Imported from legacy Logos geometry document."
                        : normalized.SourceSummary
                };
                normalized = upgraded with { ContentSha256 = ComputeContentSha256(upgraded) };
            }
            return normalized with { IsDirty = false };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Geometry document is not valid JSON: {ex.Message}", ex);
        }
    }

    public string Serialize(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(Normalize(document), JsonOptions);
    }

    public GeometryDocument PrepareForSave(
        GeometryDocument document,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Normalize(document);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var createdAt = normalized.CreatedAt == default ? timestamp : normalized.CreatedAt;
        var withTimestamps = normalized with
        {
            CreatedAt = createdAt,
            UpdatedAt = timestamp
        };
        return withTimestamps with
        {
            ContentSha256 = ComputeContentSha256(withTimestamps),
            IsDirty = false
        };
    }

    public string ComputeContentSha256(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = Normalize(document);
        var content = new CanonicalGeometryContent(
            normalized.SchemaVersion,
            normalized.GeometryId,
            normalized.DisplayName,
            normalized.Description,
            normalized.Kind,
            normalized.Frame,
            normalized.AltitudeReference,
            normalized.Points,
            normalized.Rings.Select(item => item.Points).ToArray(),
            new CanonicalPolicyContent(
                normalized.Policy.Kind,
                normalized.Policy.Constraint,
                normalized.Policy.Operations,
                normalized.Policy.Decision,
                normalized.Policy.Code,
                normalized.Policy.RecommendedAction,
                normalized.Policy.MinimumAltitudeMetres,
                normalized.Policy.MaximumAltitudeMetres,
                normalized.Policy.Tags,
                normalized.Policy.Attributes),
            normalized.Attributes);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content, CompactJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public GeometryDocument Normalize(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var policy = document.Policy ?? GeometryPolicyAnnotation.None;
        return document with
        {
            SchemaVersion = NormalizeRequired(document.SchemaVersion),
            GeometryId = NormalizeRequired(document.GeometryId),
            DisplayName = NormalizeRequired(document.DisplayName),
            Description = document.Description?.Trim() ?? string.Empty,
            AltitudeReference = document.AltitudeReference == GeometryAltitudeReference.Unknown
                ? GeometryAltitudeReference.AboveGroundLevel
                : document.AltitudeReference,
            Points = (document.Points ?? []).ToArray(),
            Rings = (document.Rings ?? [])
                .Where(item => item is not null)
                .Select(item => new GeometryDocumentRing
                {
                    Points = (item.Points ?? []).ToArray()
                })
                .ToArray(),
            Policy = policy with
            {
                Kind = NormalizeOptionalToken(policy.Kind, "none"),
                Constraint = NormalizeOptionalToken(policy.Constraint, "none"),
                Operations = NormalizeSet(policy.Operations),
                Decision = NormalizeOptionalToken(policy.Decision, "none"),
                Code = policy.Code?.Trim() ?? string.Empty,
                RecommendedAction = NormalizeOptionalToken(policy.RecommendedAction, "none"),
                Tags = NormalizeSet(policy.Tags),
                Attributes = NormalizeAttributes(policy.Attributes)
            },
            Attributes = NormalizeAttributes(document.Attributes),
            SourceConnectionId = EmptyToNull(document.SourceConnectionId),
            SourceRevision = EmptyToNull(document.SourceRevision),
            SourceSha256 = EmptyToNull(document.SourceSha256)?.ToLowerInvariant(),
            ContentSha256 = document.ContentSha256?.Trim().ToLowerInvariant() ?? string.Empty
        };
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
        => new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() }
        };

    private static string NormalizeRequired(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeOptionalToken(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeSet(IEnumerable<string>? values)
        => (values ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyDictionary<string, string> NormalizeAttributes(
        IReadOnlyDictionary<string, string>? values)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values ?? new Dictionary<string, string>())
        {
            var key = pair.Key?.Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = pair.Value?.Trim() ?? string.Empty;
            }
        }
        return result;
    }

    private sealed record CanonicalGeometryContent(
        string SchemaVersion,
        string GeometryId,
        string DisplayName,
        string Description,
        GeometryDocumentKind Kind,
        GeometryCoordinateFrame Frame,
        GeometryAltitudeReference AltitudeReference,
        IReadOnlyList<GeometryDocumentPoint> Points,
        IReadOnlyList<IReadOnlyList<GeometryDocumentPoint>> Rings,
        CanonicalPolicyContent Policy,
        IReadOnlyDictionary<string, string> Attributes);

    private sealed record CanonicalPolicyContent(
        string Kind,
        string Constraint,
        IReadOnlyList<string> Operations,
        string Decision,
        string Code,
        string RecommendedAction,
        double? MinimumAltitudeMetres,
        double? MaximumAltitudeMetres,
        IReadOnlyList<string> Tags,
        IReadOnlyDictionary<string, string> Attributes);
}
