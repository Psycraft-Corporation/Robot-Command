using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Behaviours;

/// <summary>
/// Coordinates operator-visible assessment, Logos validation, mutation, and
/// post-command verification. Robot Command never bypasses Logos validation and
/// never force-removes active behaviour packages.
/// </summary>
public sealed class BehaviourPackageDeploymentService : IBehaviourPackageDeploymentService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IBehaviourPackageStore _local;
    private readonly IBehaviourWorkspaceService _workspace;
    private readonly IBehaviourPackageGateway _gateway;
    private readonly ILogger<BehaviourPackageDeploymentService> _logger;
    private BehaviourPackageCommandResult? _lastResult;
    private int _disposed;

    public BehaviourPackageDeploymentService(
        IBehaviourPackageStore local,
        IBehaviourWorkspaceService workspace,
        IBehaviourPackageGateway gateway,
        ILogger<BehaviourPackageDeploymentService> logger)
    {
        _local = local;
        _workspace = workspace;
        _gateway = gateway;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public BehaviourPackageCommandResult? LastResult => _lastResult;

    public async Task<BehaviourPackageOperationAssessment> AssessAsync(
        BehaviourPackageOperationKind operation,
        string connectionId,
        BehaviourPackageIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        var normalizedConnectionId = NormalizeConnectionId(connectionId);
        var snapshot = await _workspace.RefreshAsync(
            normalizedConnectionId,
            refreshLocal: true,
            refreshRemote: true,
            cancellationToken: cancellationToken);
        return BuildAssessment(operation, snapshot, identity);
    }

    public Task<BehaviourPackageCommandResult> ValidateAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(BehaviourPackageOperationKind.Validate, request, cancellationToken);

    public Task<BehaviourPackageCommandResult> InstallAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(BehaviourPackageOperationKind.Install, request, cancellationToken);

    public Task<BehaviourPackageCommandResult> UpdateAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(BehaviourPackageOperationKind.Update, request, cancellationToken);

    public Task<BehaviourPackageCommandResult> RemoveAsync(
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(BehaviourPackageOperationKind.Remove, request, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Dispose();
    }

    private async Task<BehaviourPackageCommandResult> ExecuteAsync(
        BehaviourPackageOperationKind operation,
        BehaviourPackageOperationRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var assessment = await AssessAsync(
                operation,
                request.ConnectionId,
                request.Identity,
                cancellationToken);
            if (!assessment.Allowed)
            {
                return Publish(Rejected(operation, request, assessment));
            }

            var stalePrecondition = CheckFrozenPreconditions(request, assessment);
            if (stalePrecondition is not null)
            {
                return Publish(new BehaviourPackageCommandResult(
                    operation,
                    assessment.ConnectionId,
                    assessment.Identity,
                    false,
                    BehaviourPackageCommandState.Conflict,
                    stalePrecondition,
                    assessment.Local?.LogosValidation,
                    assessment.Remote,
                    false,
                    request.ExpectedLocalSha256,
                    assessment.Remote?.ContentSha256,
                    assessment.Warnings,
                    DateTimeOffset.UtcNow));
            }

            return operation switch
            {
                BehaviourPackageOperationKind.Validate => Publish(
                    await ValidateCoreAsync(request, assessment, cancellationToken)),
                BehaviourPackageOperationKind.Install => Publish(
                    await InstallOrUpdateCoreAsync(
                        request,
                        assessment,
                        update: false,
                        cancellationToken)),
                BehaviourPackageOperationKind.Update => Publish(
                    await InstallOrUpdateCoreAsync(
                        request,
                        assessment,
                        update: true,
                        cancellationToken)),
                BehaviourPackageOperationKind.Remove => Publish(
                    await RemoveCoreAsync(request, assessment, cancellationToken)),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BehaviourPackageCommandResult> ValidateCoreAsync(
        BehaviourPackageOperationRequest request,
        BehaviourPackageOperationAssessment assessment,
        CancellationToken cancellationToken)
    {
        var local = assessment.Local!;
        var validation = await _gateway.ValidateAsync(
            GatewayRequest(request, local),
            cancellationToken);
        await TryPersistValidationAsync(local.Identity, validation, cancellationToken);
        return new BehaviourPackageCommandResult(
            BehaviourPackageOperationKind.Validate,
            assessment.ConnectionId,
            local.Identity,
            validation.Accepted,
            validation.Accepted
                ? BehaviourPackageCommandState.Accepted
                : validation.State == BehaviourPackageValidationState.Unavailable
                    ? BehaviourPackageCommandState.Failed
                    : BehaviourPackageCommandState.Rejected,
            validation.Summary,
            validation,
            assessment.Remote,
            validation.Accepted,
            local.ContentSha256,
            assessment.Remote?.ContentSha256,
            assessment.Warnings,
            DateTimeOffset.UtcNow);
    }

    private async Task<BehaviourPackageCommandResult> InstallOrUpdateCoreAsync(
        BehaviourPackageOperationRequest request,
        BehaviourPackageOperationAssessment assessment,
        bool update,
        CancellationToken cancellationToken)
    {
        var operation = update
            ? BehaviourPackageOperationKind.Update
            : BehaviourPackageOperationKind.Install;
        var local = assessment.Local!;
        var validation = await _gateway.ValidateAsync(
            GatewayRequest(request, local),
            cancellationToken);
        await TryPersistValidationAsync(local.Identity, validation, cancellationToken);
        if (!validation.Accepted)
        {
            return new BehaviourPackageCommandResult(
                operation,
                assessment.ConnectionId,
                local.Identity,
                false,
                validation.State == BehaviourPackageValidationState.Unavailable
                    ? BehaviourPackageCommandState.Failed
                    : BehaviourPackageCommandState.Rejected,
                validation.Summary,
                validation,
                assessment.Remote,
                false,
                local.ContentSha256,
                assessment.Remote?.ContentSha256,
                assessment.Warnings,
                DateTimeOffset.UtcNow);
        }

        var mutation = update
            ? await _gateway.UpdateAsync(GatewayRequest(request, local), cancellationToken)
            : await _gateway.CreateAsync(GatewayRequest(request, local), cancellationToken);
        if (!mutation.Accepted)
        {
            return new BehaviourPackageCommandResult(
                operation,
                assessment.ConnectionId,
                local.Identity,
                false,
                mutation.State,
                mutation.Message,
                mutation.Validation ?? validation,
                assessment.Remote,
                false,
                local.ContentSha256,
                assessment.Remote?.ContentSha256,
                assessment.Warnings,
                DateTimeOffset.UtcNow,
                mutation.OperationId);
        }

        var refreshed = await RefreshAfterMutationAsync(assessment.ConnectionId, cancellationToken);
        var remote = ResolveRemote(refreshed, local.Identity);
        if (!refreshed.RemoteInventory.Available || refreshed.RemoteInventory.Stale)
        {
            var unverifiedWarnings = assessment.Warnings
                .Append("The installed inventory could not be refreshed after Logos accepted the package command.")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new BehaviourPackageCommandResult(
                operation,
                assessment.ConnectionId,
                local.Identity,
                true,
                BehaviourPackageCommandState.AcceptedUnverified,
                $"{mutation.Message} The installed inventory was unavailable for post-command verification.",
                mutation.Validation ?? validation,
                remote,
                false,
                local.ContentSha256,
                remote?.ContentSha256,
                unverifiedWarnings,
                DateTimeOffset.UtcNow,
                mutation.OperationId);
        }

        if (remote is null)
        {
            return new BehaviourPackageCommandResult(
                operation,
                assessment.ConnectionId,
                local.Identity,
                false,
                BehaviourPackageCommandState.Failed,
                $"Logos accepted the {operation.ToString().ToLowerInvariant()}, but the package was not present in the refreshed installed inventory.",
                mutation.Validation ?? validation,
                null,
                false,
                local.ContentSha256,
                null,
                assessment.Warnings,
                DateTimeOffset.UtcNow,
                mutation.OperationId);
        }

        var localHash = NormalizeHash(local.ContentSha256);
        var remoteHash = NormalizeHash(remote.ContentSha256);
        var verified = localHash is not null &&
                       remoteHash is not null &&
                       string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase);
        if (verified)
        {
            await _local.SetRemoteBaselineAsync(local.Identity, remoteHash, cancellationToken);
        }

        var warnings = assessment.Warnings.ToList();
        if (remoteHash is not null && !verified)
        {
            warnings.Add("The installed package SHA-256 does not match the local package after the Logos command completed.");
            return new BehaviourPackageCommandResult(
                operation,
                assessment.ConnectionId,
                local.Identity,
                false,
                BehaviourPackageCommandState.Failed,
                $"{mutation.Message} Post-command verification failed because the installed package hash does not match the local package.",
                mutation.Validation ?? validation,
                remote,
                false,
                localHash,
                remoteHash,
                warnings.Distinct(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow,
                mutation.OperationId);
        }

        if (!verified)
        {
            warnings.Add("The selected Logos runtime did not expose a package-wide SHA-256, so Robot Command could not cryptographically verify the installed copy.");
        }

        return new BehaviourPackageCommandResult(
            operation,
            assessment.ConnectionId,
            local.Identity,
            true,
            verified
                ? BehaviourPackageCommandState.Accepted
                : BehaviourPackageCommandState.AcceptedUnverified,
            verified
                ? mutation.Message
                : $"{mutation.Message} The installed package is present, but the runtime did not expose a hash for cryptographic verification.",
            mutation.Validation ?? validation,
            remote,
            verified,
            localHash,
            remoteHash,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow,
            mutation.OperationId);
    }

    private async Task<BehaviourPackageCommandResult> RemoveCoreAsync(
        BehaviourPackageOperationRequest request,
        BehaviourPackageOperationAssessment assessment,
        CancellationToken cancellationToken)
    {
        var mutation = await _gateway.DeleteAsync(
            new BehaviourPackageDeleteGatewayRequest(
                assessment.ConnectionId,
                assessment.Identity,
                request.RequestId,
                request.CorrelationId,
                request.IdempotencyKey),
            cancellationToken);
        if (!mutation.Accepted)
        {
            return new BehaviourPackageCommandResult(
                BehaviourPackageOperationKind.Remove,
                assessment.ConnectionId,
                assessment.Identity,
                false,
                mutation.State,
                mutation.Message,
                mutation.Validation,
                assessment.Remote,
                false,
                assessment.Local?.ContentSha256,
                assessment.Remote?.ContentSha256,
                assessment.Warnings,
                DateTimeOffset.UtcNow,
                mutation.OperationId);
        }

        var refreshed = await RefreshAfterMutationAsync(assessment.ConnectionId, cancellationToken);
        var stillInstalled = refreshed.RemoteInventory.Packages.Any(item =>
            string.Equals(
                item.Identity.BehaviourId,
                assessment.Identity.BehaviourId,
                StringComparison.Ordinal));
        var verified = refreshed.RemoteInventory.Available && !stillInstalled;
        if (verified && assessment.Local is not null)
        {
            await _local.SetRemoteBaselineAsync(assessment.Local.Identity, null, cancellationToken);
        }

        var warnings = assessment.Warnings.ToList();
        if (refreshed.RemoteInventory.Available && stillInstalled)
        {
            warnings.Add("The behaviour ID remained in the installed inventory after Logos accepted the removal.");
            return new BehaviourPackageCommandResult(
                BehaviourPackageOperationKind.Remove,
                assessment.ConnectionId,
                assessment.Identity,
                false,
                BehaviourPackageCommandState.Failed,
                $"{mutation.Message} Post-command verification failed because the behaviour remains installed.",
                mutation.Validation,
                assessment.Remote,
                false,
                assessment.Local?.ContentSha256,
                assessment.Remote?.ContentSha256,
                warnings.Distinct(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow,
                mutation.OperationId);
        }

        if (!verified)
        {
            warnings.Add("The installed inventory could not be refreshed after Logos accepted the removal.");
        }

        return new BehaviourPackageCommandResult(
            BehaviourPackageOperationKind.Remove,
            assessment.ConnectionId,
            assessment.Identity,
            true,
            verified
                ? BehaviourPackageCommandState.Accepted
                : BehaviourPackageCommandState.AcceptedUnverified,
            verified
                ? mutation.Message
                : $"{mutation.Message} Removal was accepted, but the installed inventory was unavailable for verification.",
            mutation.Validation,
            verified ? null : assessment.Remote,
            verified,
            assessment.Local?.ContentSha256,
            verified ? null : assessment.Remote?.ContentSha256,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow,
            mutation.OperationId);
    }

    private static BehaviourPackageOperationAssessment BuildAssessment(
        BehaviourPackageOperationKind operation,
        BehaviourWorkspaceSnapshot snapshot,
        BehaviourPackageIdentity identity)
    {
        var warnings = new List<string>();
        var blockers = new List<string>();
        var exactEntry = snapshot.Entries.FirstOrDefault(item => item.Identity == identity);
        var local = exactEntry?.Local ??
                    snapshot.LocalPackages.FirstOrDefault(item => item.Identity == identity);
        var remoteById = snapshot.RemoteInventory.Packages
            .Where(item => string.Equals(
                item.Identity.BehaviourId,
                identity.BehaviourId,
                StringComparison.Ordinal))
            .ToArray();
        var remote = exactEntry?.Remote ??
                     (remoteById.Length == 1 ? remoteById[0] : null);

        if (!snapshot.RemoteInventory.Available || snapshot.RemoteInventory.Stale)
        {
            if (operation == BehaviourPackageOperationKind.Validate)
            {
                warnings.Add($"Installed inventory unavailable during validation assessment: {snapshot.RemoteInventory.Summary}");
            }
            else
            {
                blockers.Add(snapshot.RemoteInventory.Summary);
            }
        }

        if (operation is not BehaviourPackageOperationKind.Remove)
        {
            if (local is null)
            {
                blockers.Add($"Local behaviour package '{identity.Key}' was not found.");
            }
            else
            {
                if (!local.CanSubmitToLogos)
                {
                    blockers.Add("The local package must be imported, unchanged, and pass Robot Command structural preflight before it can be submitted to Logos.");
                }

                if (string.IsNullOrWhiteSpace(local.Layout.GeometryFile))
                {
                    blockers.Add("The current Logos behaviour package API requires a geometry_json payload, but the local manifest does not declare a geometry file.");
                }
            }
        }

        switch (operation)
        {
            case BehaviourPackageOperationKind.Validate:
                break;

            case BehaviourPackageOperationKind.Install:
                if (remoteById.Length > 0)
                {
                    blockers.Add(
                        $"Logos already has a package for behaviour ID '{identity.BehaviourId}'. Use Update instead of Install.");
                }
                break;

            case BehaviourPackageOperationKind.Update:
                if (remoteById.Length == 0)
                {
                    blockers.Add(
                        $"Logos does not have a package for behaviour ID '{identity.BehaviourId}'. Use Install instead of Update.");
                }
                else if (remoteById.Length > 1)
                {
                    blockers.Add(
                        "The current Logos update API identifies packages by behaviour ID, but multiple installed versions use that ID. Robot Command will not choose one implicitly.");
                }

                if (remote?.Active == true)
                {
                    blockers.Add("The installed behaviour package is active and cannot be updated through this workflow.");
                }
                if (remote?.InUse == true)
                {
                    blockers.Add("The installed behaviour package is referenced by an active execution and cannot be updated.");
                }
                if (exactEntry?.Deployment.Status == BehaviourDeploymentStatus.Conflict)
                {
                    blockers.Add("Local and installed packages both changed since the last confirmed deployment. Resolve the conflict before updating.");
                }
                if (remote?.ContentSha256 is null)
                {
                    warnings.Add("The runtime does not expose a package-wide SHA-256. Robot Command can re-read the inventory, but cannot enforce a cryptographic remote precondition.");
                }
                break;

            case BehaviourPackageOperationKind.Remove:
                if (remoteById.Length == 0)
                {
                    blockers.Add(
                        $"No installed package exists for behaviour ID '{identity.BehaviourId}'.");
                }
                else if (remoteById.Length > 1)
                {
                    blockers.Add(
                        "The current Logos removal API deletes by behaviour ID rather than an independently targetable version. Multiple installed versions make this removal ambiguous.");
                }

                if (remote?.Active == true)
                {
                    blockers.Add("The installed behaviour package is active. Robot Command never requests allow_delete_active.");
                }
                if (remote?.InUse == true)
                {
                    blockers.Add("The installed behaviour package is referenced by an active execution and cannot be removed.");
                }
                if (identity.Version is not null)
                {
                    warnings.Add("The current Logos removal API targets the behaviour ID. The version is retained here for operator context, not sent as an independent deletion selector.");
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        var allowed = blockers.Count == 0;
        var summary = allowed
            ? operation switch
            {
                BehaviourPackageOperationKind.Validate => "The local package is ready to submit to Logos validation.",
                BehaviourPackageOperationKind.Install => "The local package is ready to install on the selected Logos runtime.",
                BehaviourPackageOperationKind.Update => "The installed package is ready to update from the selected local copy.",
                BehaviourPackageOperationKind.Remove => "The installed package is ready to remove from the selected Logos runtime.",
                _ => "The operation is ready."
            }
            : string.Join(" ", blockers);
        return new BehaviourPackageOperationAssessment(
            operation,
            snapshot.ConnectionId,
            identity,
            allowed,
            summary,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            blockers.Distinct(StringComparer.Ordinal).ToArray(),
            local,
            remote,
            DateTimeOffset.UtcNow);
    }

    private async Task<BehaviourWorkspaceSnapshot> RefreshAfterMutationAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _workspace.RefreshAsync(
                connectionId,
                refreshLocal: true,
                refreshRemote: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh behaviour workspace after a package command on {ConnectionId}", connectionId);
            return _workspace.GetSnapshot(connectionId);
        }
    }

    private async Task TryPersistValidationAsync(
        BehaviourPackageIdentity identity,
        BehaviourPackageValidationResult validation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _local.SetLogosValidationAsync(identity, validation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not persist Logos validation for {Identity}", identity.Key);
        }
    }

    private static BehaviourPackageGatewayRequest GatewayRequest(
        BehaviourPackageOperationRequest request,
        LocalBehaviourPackageRecord local)
        => new(
            request.ConnectionId,
            local,
            request.RequestId,
            request.CorrelationId,
            request.IdempotencyKey);

    private static string? CheckFrozenPreconditions(
        BehaviourPackageOperationRequest request,
        BehaviourPackageOperationAssessment assessment)
    {
        if (!HashesMatchWhenExpected(request.ExpectedLocalSha256, assessment.Local?.ContentSha256))
        {
            return "The local behaviour package changed after the operation was assessed. Review and prepare the command again.";
        }

        if (!HashesMatchWhenExpected(request.ExpectedRemoteSha256, assessment.Remote?.ContentSha256))
        {
            return "The installed behaviour package changed after the operation was assessed. Refresh before continuing.";
        }

        return null;
    }

    private static bool HashesMatchWhenExpected(string? expected, string? current)
        => string.IsNullOrWhiteSpace(expected) ||
           string.Equals(
               expected.Trim(),
               current?.Trim(),
               StringComparison.OrdinalIgnoreCase);

    private static RemoteBehaviourPackageRecord? ResolveRemote(
        BehaviourWorkspaceSnapshot snapshot,
        BehaviourPackageIdentity identity)
    {
        var exact = snapshot.RemoteInventory.Packages.FirstOrDefault(item => item.Identity == identity);
        if (exact is not null)
        {
            return exact;
        }

        var byId = snapshot.RemoteInventory.Packages
            .Where(item => string.Equals(
                item.Identity.BehaviourId,
                identity.BehaviourId,
                StringComparison.Ordinal))
            .ToArray();
        return byId.Length == 1 ? byId[0] : null;
    }

    private BehaviourPackageCommandResult Publish(BehaviourPackageCommandResult result)
    {
        _lastResult = result;
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private static BehaviourPackageCommandResult Rejected(
        BehaviourPackageOperationKind operation,
        BehaviourPackageOperationRequest request,
        BehaviourPackageOperationAssessment assessment)
        => new(
            operation,
            assessment.ConnectionId,
            assessment.Identity,
            false,
            BehaviourPackageCommandState.Rejected,
            assessment.Summary,
            assessment.Local?.LogosValidation,
            assessment.Remote,
            false,
            request.ExpectedLocalSha256,
            assessment.Remote?.ContentSha256,
            assessment.Warnings,
            DateTimeOffset.UtcNow);

    private static string NormalizeConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }

        return connectionId.Trim();
    }

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
