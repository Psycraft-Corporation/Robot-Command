using System.Security.Cryptography;
using System.Text;

namespace RobotCommand.Services.Team;

/// <summary>Owns the session-only human passphrase used to request Team API access.</summary>
public interface ITeamPassphraseService
{
    bool IsConfigured { get; }
    bool IsOpenAccess { get; }
    event EventHandler? Changed;
    void Configure(string passphrase);
    void ConfigureOpenAccess();
    bool Verify(string passphrase);
    void Clear();
}

public sealed class TeamPassphraseService : ITeamPassphraseService
{
    private readonly object _gate = new();
    private byte[]? _hash;
    private bool _openAccess;

    public bool IsConfigured { get { lock (_gate) return _hash is not null; } }
    public bool IsOpenAccess { get { lock (_gate) return _openAccess; } }
    public event EventHandler? Changed;

    public void Configure(string passphrase)
    {
        var normalized = Normalize(passphrase);
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("Enter a Team server passphrase before starting the server.", nameof(passphrase));

        lock (_gate)
        {
            _hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            _openAccess = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ConfigureOpenAccess()
    {
        lock (_gate)
        {
            if (_hash is not null) CryptographicOperations.ZeroMemory(_hash);
            _hash = null;
            _openAccess = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Verify(string passphrase)
    {
        var normalized = Normalize(passphrase);
        byte[]? expected;
        lock (_gate) expected = _hash;
        return expected is not null &&
               CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_hash is not null) CryptographicOperations.ZeroMemory(_hash);
            _hash = null;
            _openAccess = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Creates an easily read session phrase. The server still requires explicit approval.</summary>
    public static string CreateSuggestedPassphrase()
        => Normalize($"{Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)]}-{Nouns[RandomNumberGenerator.GetInt32(Nouns.Length)]}");

    public static string Normalize(string? value)
        => string.Join('-', (value ?? string.Empty).Trim().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();

    private static readonly string[] Adjectives =
    [
        "Agile", "Amber", "Arctic", "Atlas", "Bright", "Calm", "Cedar", "Clear", "Cobalt", "Copper", "Coral", "Crimson",
        "Daring", "Delta", "Ember", "Fabled", "Fleet", "Forest", "Golden", "Harbor", "Hidden", "Iris", "Ivory", "Jade",
        "Keen", "Kindle", "Lively", "Lucky", "Maple", "Meadow", "Misty", "Nimble", "North", "Nova", "Oak", "Opal",
        "Orchid", "Pacific", "Prairie", "Quiet", "Rapid", "Ready", "River", "Royal", "Sable", "Scarlet", "Silver", "Solar",
        "Steady", "Summit", "Swift", "Tundra", "Velvet", "Verdant", "Vivid", "Willow", "Winter", "Wise", "Yellow", "Zephyr"
    ];

    private static readonly string[] Nouns =
    [
        "Anchor", "Arrow", "Beacon", "Birch", "Bridge", "Canyon", "Cedar", "Circuit", "Comet", "Compass", "Crown", "Drift",
        "Eagle", "Falcon", "Field", "Flame", "Forge", "Grove", "Harbor", "Horizon", "Juniper", "Kestrel", "Lantern", "Maple",
        "Meadow", "Meridian", "Northstar", "Orchid", "Orbit", "Pine", "Quartz", "Raven", "Ridge", "River", "Sail", "Signal",
        "Skyline", "Solstice", "Spruce", "Summit", "Thistle", "Trail", "Valley", "Venture", "Willow", "Wind", "Wing", "Wolf",
        "Yarrow", "Zenith", "Spindle", "Handball", "Cope", "Flatten", "Repair", "Precise", "Vector", "Waypoint", "Workshop", "Voyage"
    ];
}
