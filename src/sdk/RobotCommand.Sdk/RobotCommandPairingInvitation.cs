using System.Globalization;

namespace RobotCommand.Sdk;

/// <summary>
/// A short-lived, human-verifiable invitation encoded in a Robot Command pairing QR code.
/// It contains no observer session credential and remains subject to explicit host approval.
/// </summary>
public sealed record RobotCommandPairingInvitation(
    Uri Endpoint,
    string CertificateFingerprint,
    string PairingId,
    string ShortCode,
    DateTimeOffset ExpiresAt,
    string Passphrase = "")
{
    /// <summary>The URI scheme carried by the QR code.</summary>
    public const string Scheme = "logos-robot-command";

    /// <summary>Returns the portable pairing URI for a QR code or deep-link handler.</summary>
    public Uri ToUri()
    {
        var query = string.Join("&", new[]
        {
            $"endpoint={Uri.EscapeDataString(Endpoint.ToString().TrimEnd('/'))}",
            $"fingerprint={Uri.EscapeDataString(RobotCommandLanClient.NormalizeFingerprint(CertificateFingerprint))}",
            $"pairing_id={Uri.EscapeDataString(PairingId)}",
            $"code={Uri.EscapeDataString(ShortCode)}",
            $"phrase={Uri.EscapeDataString(Passphrase)}",
            $"expires={ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}"
        });
        return new Uri($"{Scheme}://pair/v1?{query}", UriKind.Absolute);
    }

    /// <summary>Parses and validates a QR pairing URI without contacting its server.</summary>
    public static RobotCommandPairingInvitation Parse(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath.Trim('/'), "v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Enter a valid Robot Command pairing link.", nameof(value));
        }

        var values = ParseQuery(uri.Query);
        if (!values.TryGetValue("endpoint", out var endpointText) ||
            !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The pairing link does not contain a valid HTTPS endpoint.", nameof(value));
        }

        if (!values.TryGetValue("fingerprint", out var fingerprint) ||
            !values.TryGetValue("pairing_id", out var pairingId) ||
            !values.TryGetValue("code", out var shortCode) ||
            !values.TryGetValue("expires", out var expiresText) ||
            !long.TryParse(expiresText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds) ||
            string.IsNullOrWhiteSpace(pairingId) ||
            shortCode.Length != 6 || !shortCode.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The pairing link is incomplete or invalid.", nameof(value));
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("The pairing link has expired. Create a new QR code on the host Robot Command.", nameof(value));
        }

        return new RobotCommandPairingInvitation(
            endpoint,
            RobotCommandLanClient.NormalizeFingerprint(fingerprint),
            pairingId,
            shortCode,
            expiresAt,
            values.TryGetValue("phrase", out var passphrase) ? passphrase : string.Empty);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0) continue;
            result[Uri.UnescapeDataString(segment[..separator])] = Uri.UnescapeDataString(segment[(separator + 1)..]);
        }
        return result;
    }
}
