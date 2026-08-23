namespace RobotCommand.Models;

public sealed record TeamServerSettings(
    string DisplayName,
    int Port = 7443,
    int MaximumClients = 8,
    bool RequirePassphrase = true)
{
    public static TeamServerSettings Default { get; } = new(
        "Robot Command",
        7443,
        8,
        true);

    public TeamServerSettings Normalize()
        => new(
            string.IsNullOrWhiteSpace(DisplayName) ? Default.DisplayName : DisplayName.Trim(),
            Math.Clamp(Port, 1024, 65535),
            Math.Clamp(MaximumClients, 1, 64),
            RequirePassphrase);
}

public enum TeamAccessRequestState
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Incompatible
}

public enum TeamDisconnectReason
{
    OperatorDisconnected,
    AuthenticationRequired,
    ServerStopped
}

public sealed record TeamDisconnectNotice(TeamDisconnectReason Reason, string Message);

public sealed record TeamAccessRequestRecord(
    string Id,
    string DisplayName,
    string ApplicationName,
    string ApplicationVersion,
    string SdkVersion,
    string ApiVersion,
    string ClientInstanceId,
    string RequestNonce,
    string PairingId,
    string PairingShortCode,
    bool PairingVerified,
    bool PassphraseVerified,
    string RemoteAddress,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    bool Compatible,
    TeamAccessRequestState State,
    string Message)
{
    public string PairingSummary => PassphraseVerified
        ? "Session passphrase verified"
        : PairingVerified
            ? $"QR pairing code {PairingShortCode} verified"
            : string.Empty;
}

public sealed record TeamConnectedClientRecord(
    string SessionId,
    string RequestId,
    string DisplayName,
    string ApplicationName,
    string ApplicationVersion,
    string RemoteAddress,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastActivityAt);
