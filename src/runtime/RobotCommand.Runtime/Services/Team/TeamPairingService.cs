using System.Security.Cryptography;
using RobotCommand.Sdk;

namespace RobotCommand.Services.Team;

/// <summary>Creates short-lived, QR-safe invitations for explicit observer pairing.</summary>
public interface ITeamPairingService
{
    RobotCommandPairingInvitation? CurrentInvitation { get; }
    event EventHandler? Changed;
    RobotCommandPairingInvitation Create(Uri endpoint, string certificateFingerprint, string passphrase = "");
    bool IsValid(string pairingId, string shortCode);
    bool Consume(string pairingId, string shortCode);
    void Clear();
}

public sealed class TeamPairingService : ITeamPairingService
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private RobotCommandPairingInvitation? _current;

    public RobotCommandPairingInvitation? CurrentInvitation
    {
        get
        {
            lock (_gate)
            {
                if (_current is not null && _current.ExpiresAt <= DateTimeOffset.UtcNow) _current = null;
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public RobotCommandPairingInvitation Create(Uri endpoint, string certificateFingerprint, string passphrase = "")
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pairing requires an HTTPS Team API endpoint.", nameof(endpoint));

        var invitation = new RobotCommandPairingInvitation(
            endpoint,
            RobotCommandLanClient.NormalizeFingerprint(certificateFingerprint),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow.Add(InvitationLifetime),
            passphrase);
        lock (_gate) _current = invitation;
        Changed?.Invoke(this, EventArgs.Empty);
        return invitation;
    }

    public bool IsValid(string pairingId, string shortCode)
    {
        lock (_gate)
        {
            var invitation = CurrentUnsafe();
            return invitation is not null &&
                   CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(invitation.PairingId), System.Text.Encoding.UTF8.GetBytes(pairingId ?? string.Empty)) &&
                   CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(invitation.ShortCode), System.Text.Encoding.UTF8.GetBytes(shortCode ?? string.Empty));
        }
    }

    public bool Consume(string pairingId, string shortCode)
    {
        lock (_gate)
        {
            var invitation = CurrentUnsafe();
            if (invitation is null ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(invitation.PairingId), System.Text.Encoding.UTF8.GetBytes(pairingId ?? string.Empty)) ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(invitation.ShortCode), System.Text.Encoding.UTF8.GetBytes(shortCode ?? string.Empty)))
            {
                return false;
            }
            _current = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        lock (_gate) _current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private RobotCommandPairingInvitation? CurrentUnsafe()
    {
        if (_current is not null && _current.ExpiresAt <= DateTimeOffset.UtcNow) _current = null;
        return _current;
    }
}
