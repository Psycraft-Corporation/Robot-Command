namespace RobotCommand.Models;

public enum GeometryDeploymentStatus
{
    Unknown,
    LocalOnly,
    RemoteOnly,
    Matching,
    LocalModified,
    RemoteModified,
    Conflict,
    MissingRemote,
    Invalid,
    InUse
}

public sealed record GeometryDeploymentRecord(
    string GeometryId,
    string ConnectionId,
    GeometryDeploymentStatus Status,
    string? LocalSha256,
    string? RemoteSha256,
    string? RemoteRevision,
    DateTimeOffset? RemoteUpdatedAt,
    string Detail,
    bool Referenced = false);
