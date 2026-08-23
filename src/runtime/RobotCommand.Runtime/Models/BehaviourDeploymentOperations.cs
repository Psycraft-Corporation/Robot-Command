namespace RobotCommand.Models;

public enum BehaviourPackageOperationKind
{
    Validate,
    Install,
    Update,
    Remove
}

public enum BehaviourPackageCommandState
{
    Accepted,
    AcceptedUnverified,
    Rejected,
    Conflict,
    Failed
}

/// <summary>
/// Frozen preconditions carried from an operator-visible assessment into a
/// later command. Hashes are optional because older Logos runtimes may not
/// expose a package-wide SHA, but when present they are enforced.
/// </summary>
public sealed record BehaviourPackageOperationRequest(
    string ConnectionId,
    BehaviourPackageIdentity Identity,
    string? ExpectedLocalSha256 = null,
    string? ExpectedRemoteSha256 = null,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null)
{
    public static BehaviourPackageOperationRequest From(
        BehaviourPackageOperationAssessment assessment,
        string? requestId = null,
        string? correlationId = null,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return new BehaviourPackageOperationRequest(
            assessment.ConnectionId,
            assessment.Identity,
            assessment.Local?.ContentSha256,
            assessment.Remote?.ContentSha256,
            requestId,
            correlationId,
            idempotencyKey);
    }
}

public sealed record BehaviourPackageOperationAssessment(
    BehaviourPackageOperationKind Operation,
    string ConnectionId,
    BehaviourPackageIdentity Identity,
    bool Allowed,
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    LocalBehaviourPackageRecord? Local,
    RemoteBehaviourPackageRecord? Remote,
    DateTimeOffset AssessedAt)
{
    public bool RequiresConfirmation => Operation is
        BehaviourPackageOperationKind.Install or
        BehaviourPackageOperationKind.Update or
        BehaviourPackageOperationKind.Remove;

    public BehaviourPackageOperationRequest CreateRequest(
        string? requestId = null,
        string? correlationId = null,
        string? idempotencyKey = null)
        => BehaviourPackageOperationRequest.From(
            this,
            requestId,
            correlationId,
            idempotencyKey);
}

public sealed record BehaviourPackageGatewayRequest(
    string ConnectionId,
    LocalBehaviourPackageRecord Package,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record BehaviourPackageDeleteGatewayRequest(
    string ConnectionId,
    BehaviourPackageIdentity Identity,
    string? RequestId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

public sealed record BehaviourPackageGatewayMutationResult(
    bool Accepted,
    BehaviourPackageCommandState State,
    string Message,
    BehaviourPackageValidationResult? Validation = null,
    string? OperationId = null);

public sealed record BehaviourPackageCommandResult(
    BehaviourPackageOperationKind Operation,
    string ConnectionId,
    BehaviourPackageIdentity Identity,
    bool Accepted,
    BehaviourPackageCommandState State,
    string Message,
    BehaviourPackageValidationResult? Validation,
    RemoteBehaviourPackageRecord? Remote,
    bool Verified,
    string? ExpectedLocalSha256,
    string? ObservedRemoteSha256,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CompletedAt,
    string? OperationId = null)
{
    public bool NeedsRefresh => Accepted && !Verified;
}
