namespace RobotCommand.Models;

public sealed record ConnectionProfile(
    string Name,
    string Target,
    string? Description = null,
    string? Id = null,
    ConnectionMode Mode = ConnectionMode.Direct,
    bool AutoConnect = false,
    bool AutoReconnect = true,
    MavlinkConnectionOptions? Mavlink = null,
    LinkdConnectionOptions? Linkd = null)
{
    public ConnectionDefinition ToDefinition()
        => new(
            string.IsNullOrWhiteSpace(Id)
                ? ConnectionId.Create(Name, Target)
                : Id.Trim(),
            Name.Trim(),
            Target.Trim(),
            Mode,
            AutoConnect,
            AutoReconnect,
            Description?.Trim(),
            Mavlink: Mavlink,
            Linkd: Linkd);
}
