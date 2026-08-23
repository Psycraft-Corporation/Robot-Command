using Microsoft.Extensions.Logging;
using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryWorkspaceService : IGeometryWorkspaceService, IGeometryRegistryStateSink, IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IGeometryDocumentStore _documents;
    private readonly IGeometryGateway _gateway;
    private readonly GeometryDocumentValidator _validator;
    private readonly ILogger<GeometryWorkspaceService> _logger;
    private readonly Dictionary<string, RemoteGeometryRecord[]> _remoteByConnection =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeometryRegistrySnapshot> _registryByConnection =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeometryRegistryWatchState> _watchByConnection =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedRemoteConnections = new(StringComparer.Ordinal);
    private GeometryDeploymentRecord[] _deployments = [];
    private int _disposed;

    public GeometryWorkspaceService(
        IGeometryDocumentStore documents,
        IGeometryGateway gateway,
        GeometryDocumentValidator validator,
        ILogger<GeometryWorkspaceService> logger)
    {
        _documents = documents;
        _gateway = gateway;
        _validator = validator;
        _logger = logger;
        _documents.Changed += OnDocumentsChanged;
        RecalculateDeployments();
    }

    public event EventHandler? Changed;

    public bool GatewayAvailable => _gateway.IsAvailable;

    public string GatewayStatus => _gateway.AvailabilityMessage;

    public string LocalLibraryPath => _documents.RootPath;

    public IReadOnlyList<GeometryDocument> LocalDocuments => _documents.Documents;

    public IReadOnlyList<GeometryLibraryIssue> LibraryIssues => _documents.Issues;

    public IReadOnlyList<RemoteGeometryRecord> RemoteRecords
    {
        get
        {
            lock (_stateGate)
            {
                return _remoteByConnection.Values
                    .SelectMany(item => item)
                    .OrderBy(item => item.ConnectionId, StringComparer.Ordinal)
                    .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<GeometryRegistrySnapshot> RegistrySnapshots
    {
        get
        {
            lock (_stateGate)
            {
                return _registryByConnection.Values
                    .OrderBy(item => item.ConnectionId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<GeometryRegistryWatchState> RegistryWatchStates
    {
        get
        {
            lock (_stateGate)
            {
                return _watchByConnection.Values
                    .OrderBy(item => item.ConnectionId, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<GeometryDeploymentRecord> Deployments
    {
        get
        {
            lock (_stateGate)
            {
                return _deployments.ToArray();
            }
        }
    }

    public async Task RefreshAsync(
        string? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await _documents.RefreshAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                await RefreshRemoteCoreAsync(connectionId.Trim(), cancellationToken);
            }

            RecalculateDeployments();
        }
        finally
        {
            _operationGate.Release();
        }

        RaiseChanged();
    }

    public async Task<GeometryDocument> CreateLocalDraftAsync(
        GeometryDocumentKind kind,
        string? geometryId = null,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (kind is not GeometryDocumentKind.PointOfInterest and
            not GeometryDocumentKind.WaypointSequence and
            not GeometryDocumentKind.Zone)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A supported geometry kind is required.");
        }

        var prefix = kind switch
        {
            GeometryDocumentKind.PointOfInterest => "poi",
            GeometryDocumentKind.WaypointSequence => "route",
            GeometryDocumentKind.Zone => "zone",
            _ => "geometry"
        };
        var title = kind switch
        {
            GeometryDocumentKind.PointOfInterest => "New point of interest",
            GeometryDocumentKind.WaypointSequence => "New waypoint sequence",
            GeometryDocumentKind.Zone => "New zone",
            _ => "New geometry"
        };
        var generatedId = $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var id = string.IsNullOrWhiteSpace(geometryId)
            ? (generatedId.Length <= 128 ? generatedId : generatedId[..128])
            : geometryId.Trim();
        var document = GeometryDocument.Create(
            id,
            string.IsNullOrWhiteSpace(displayName) ? title : displayName.Trim(),
            kind);
        // A draft is deliberately not written to the managed library. The
        // map/editor must finish its geometry review and explicitly save it;
        // otherwise Escape would leave incomplete geometry behind.
        return document;
    }

    public async Task<GeometryDocument> ImportLocalAsync(
        string path,
        bool allowReplace = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var document = await _documents.ImportAsync(path, allowReplace, cancellationToken);
        RecalculateDeployments();
        RaiseChanged();
        return document;
    }

    public Task ExportLocalAsync(
        string geometryId,
        string path,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _documents.ExportAsync(geometryId, path, cancellationToken);
    }

    public async Task<GeometryDocument> SaveLocalAsync(
        GeometryDocument document,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var saved = await _documents.UpsertAsync(document, cancellationToken);
        RecalculateDeployments();
        RaiseChanged();
        return saved;
    }

    public async Task<GeometryDocument> DuplicateLocalAsync(
        string geometryId,
        string newGeometryId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var sourceId = RequireGeometryIdValue(geometryId);
        var targetId = RequireGeometryIdValue(newGeometryId);

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_documents.TryGet(targetId, out _))
            {
                throw new InvalidOperationException(
                    $"Geometry '{targetId}' already exists in the local library.");
            }

            var source = GetLocalRequired(sourceId);
            var now = DateTimeOffset.UtcNow;
            var copy = source with
            {
                GeometryId = targetId,
                DisplayName = $"{source.DisplayName} copy",
                Origin = GeometryDocumentOrigin.LocalDraft,
                SourceConnectionId = null,
                SourceRevision = null,
                SourceSha256 = null,
                ContentSha256 = string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
                IsDirty = true
            };
            var saved = await _documents.UpsertAsync(copy, cancellationToken);
            RecalculateDeployments();
            RaiseChanged();
            return saved;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GeometryDocument> PullAsync(
        string connectionId,
        string geometryId,
        bool replaceLocal = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireConnectionId(connectionId);
        RequireGeometryId(geometryId);

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_documents.TryGet(geometryId, out _) && !replaceLocal)
            {
                throw new InvalidOperationException(
                    $"Geometry '{geometryId}' already exists locally. Enable replacement or choose a different local ID.");
            }

            var remote = await _gateway.GetAsync(
                connectionId.Trim(),
                geometryId.Trim(),
                refresh: true,
                cancellationToken);
            if (remote is null)
            {
                throw new KeyNotFoundException(
                    $"Geometry '{geometryId}' was not found on connection '{connectionId}'.");
            }

            var pulled = remote.Document with
            {
                Origin = GeometryDocumentOrigin.PulledFromLogos,
                SourceConnectionId = connectionId.Trim(),
                SourceRevision = remote.Record.Revision,
                SourceSha256 = EmptyToNull(remote.Record.Sha256) ?? EmptyToNull(remote.Document.ContentSha256),
                IsDirty = false
            };
            var saved = await _documents.UpsertAsync(pulled, cancellationToken);
            UpsertRemote(remote.Record);
            RecalculateDeployments();
            RaiseChanged();
            return saved;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GeometryDocument> PullAsLocalCopyAsync(
        string connectionId,
        string geometryId,
        string newGeometryId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedConnectionId = RequireConnectionIdValue(connectionId);
        var sourceId = RequireGeometryIdValue(geometryId);
        var targetId = RequireGeometryIdValue(newGeometryId);

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_documents.TryGet(targetId, out _))
            {
                throw new InvalidOperationException(
                    $"Geometry '{targetId}' already exists in the local library.");
            }

            var remote = await _gateway.GetAsync(
                normalizedConnectionId,
                sourceId,
                refresh: true,
                cancellationToken);
            if (remote is null)
            {
                throw new KeyNotFoundException(
                    $"Geometry '{sourceId}' was not found on connection '{normalizedConnectionId}'.");
            }

            UpsertRemote(remote.Record);
            var copy = remote.Document with
            {
                GeometryId = targetId,
                DisplayName = $"{remote.Document.DisplayName} remote copy",
                Origin = GeometryDocumentOrigin.LocalDraft,
                SourceConnectionId = null,
                SourceRevision = null,
                SourceSha256 = null,
                ContentSha256 = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDirty = true
            };
            var saved = await _documents.UpsertAsync(copy, cancellationToken);
            RecalculateDeployments();
            RaiseChanged();
            return saved;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GeometryOperationAssessment> AssessRemoteOperationAsync(
        string connectionId,
        string geometryId,
        GeometryRemoteOperationKind operation,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireConnectionId(connectionId);
        RequireGeometryId(geometryId);

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await AssessRemoteOperationCoreAsync(
                connectionId.Trim(),
                geometryId.Trim(),
                operation,
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GeometryCommandResult> CreateRemoteAsync(
        string connectionId,
        string geometryId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var normalizedConnectionId = RequireConnectionIdValue(connectionId);
            var normalizedGeometryId = RequireGeometryIdValue(geometryId);
            var assessment = await AssessRemoteOperationCoreAsync(
                normalizedConnectionId,
                normalizedGeometryId,
                GeometryRemoteOperationKind.Create,
                cancellationToken);
            if (!assessment.CanExecute)
            {
                return RejectedFromAssessment(assessment);
            }

            var local = GetLocalRequired(normalizedGeometryId);
            var result = await _gateway.CreateAsync(
                new GeometryCreateRequest(normalizedConnectionId, local),
                cancellationToken);
            result = result with
            {
                Validation = MergeValidation(
                    assessment.LocalValidation,
                    assessment.LogosValidation,
                    result.Validation)
            };
            return await VerifyMutationAsync(
                normalizedConnectionId,
                local,
                result,
                "created",
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GeometryCommandResult> UpdateRemoteAsync(
        string connectionId,
        string geometryId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var normalizedConnectionId = RequireConnectionIdValue(connectionId);
            var normalizedGeometryId = RequireGeometryIdValue(geometryId);
            var assessment = await AssessRemoteOperationCoreAsync(
                normalizedConnectionId,
                normalizedGeometryId,
                GeometryRemoteOperationKind.Update,
                cancellationToken);
            if (!assessment.CanExecute)
            {
                return RejectedFromAssessment(assessment);
            }

            var local = GetLocalRequired(normalizedGeometryId);
            var result = await _gateway.UpdateAsync(
                new GeometryUpdateRequest(
                    normalizedConnectionId,
                    local,
                    assessment.ExpectedRevision),
                cancellationToken);
            result = result with
            {
                Validation = MergeValidation(
                    assessment.LocalValidation,
                    assessment.LogosValidation,
                    result.Validation)
            };
            return await VerifyMutationAsync(
                normalizedConnectionId,
                local,
                result,
                "updated",
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<GeometryCommandResult> DeleteRemoteAsync(
        string connectionId,
        string geometryId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var normalizedConnectionId = RequireConnectionIdValue(connectionId);
            var normalizedGeometryId = RequireGeometryIdValue(geometryId);
            var assessment = await AssessRemoteOperationCoreAsync(
                normalizedConnectionId,
                normalizedGeometryId,
                GeometryRemoteOperationKind.Delete,
                cancellationToken);
            if (!assessment.CanExecute)
            {
                return RejectedFromAssessment(assessment);
            }

            var result = await _gateway.DeleteAsync(
                new GeometryDeleteRequest(
                    normalizedConnectionId,
                    normalizedGeometryId,
                    AllowDeleteReferenced: false),
                cancellationToken);
            if (!result.Accepted)
            {
                return result;
            }

            RemoteGeometryObject? remaining;
            try
            {
                remaining = await _gateway.GetAsync(
                    normalizedConnectionId,
                    normalizedGeometryId,
                    refresh: true,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Logos accepted deletion of geometry {GeometryId}, but verification failed on {ConnectionId}",
                    normalizedGeometryId,
                    normalizedConnectionId);
                return new GeometryCommandResult(
                    false,
                    GeometryCommandState.Failed,
                    $"Logos accepted deletion of geometry '{normalizedGeometryId}', but Robot Command could not verify that it was removed: {ex.Message}",
                    OperationId: result.OperationId);
            }

            if (remaining is not null)
            {
                UpsertRemote(remaining.Record);
                RecalculateDeployments();
                RaiseChanged();
                return new GeometryCommandResult(
                    false,
                    GeometryCommandState.Conflict,
                    $"Logos accepted deletion of geometry '{normalizedGeometryId}', but the object is still present after verification.",
                    remaining,
                    result.Validation,
                    result.OperationId);
            }

            RemoveRemote(normalizedConnectionId, normalizedGeometryId);
            RecalculateDeployments();
            RaiseChanged();
            return result with
            {
                Message = $"{result.Message} Robot Command verified that the remote object was removed."
            };
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ReplaceRemoteSnapshot(
        string connectionId,
        IReadOnlyList<RemoteGeometryRecord> records,
        GeometryRegistrySnapshot registry)
    {
        ThrowIfDisposed();
        var normalizedConnectionId = RequireConnectionIdValue(connectionId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(registry);

        lock (_stateGate)
        {
            _remoteByConnection[normalizedConnectionId] = records
                .Where(item => !string.IsNullOrWhiteSpace(item.GeometryId))
                .Select(item => item with { ConnectionId = normalizedConnectionId })
                .GroupBy(item => item.GeometryId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
                .ToArray();
            _registryByConnection[normalizedConnectionId] = registry with
            {
                ConnectionId = normalizedConnectionId,
                CheckedAt = DateTimeOffset.UtcNow
            };
            _loadedRemoteConnections.Add(normalizedConnectionId);
        }

        RecalculateDeployments();
        RaiseChanged();
    }

    public void ApplyRegistryEvent(GeometryRegistryEvent registryEvent)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(registryEvent);
        var connectionId = RequireConnectionIdValue(registryEvent.ConnectionId);
        var remoteChanged = false;
        var notify = registryEvent.Kind != GeometryRegistryEventKind.Heartbeat;

        lock (_stateGate)
        {
            if (registryEvent.Registry is not null)
            {
                _registryByConnection[connectionId] = registryEvent.Registry with
                {
                    ConnectionId = connectionId,
                    CheckedAt = DateTimeOffset.UtcNow
                };
            }

            var record = registryEvent.Object?.Record ?? registryEvent.Record;
            switch (registryEvent.Kind)
            {
                case GeometryRegistryEventKind.Snapshot:
                case GeometryRegistryEventKind.Created:
                case GeometryRegistryEventKind.Updated:
                    if (record is not null && !string.IsNullOrWhiteSpace(record.GeometryId))
                    {
                        UpsertRemoteLocked(record with { ConnectionId = connectionId });
                        remoteChanged = true;
                    }
                    break;

                case GeometryRegistryEventKind.Deleted:
                    if (record is not null && !string.IsNullOrWhiteSpace(record.GeometryId))
                    {
                        RemoveRemoteLocked(connectionId, record.GeometryId);
                        remoteChanged = true;
                    }
                    break;
            }

            if (_watchByConnection.TryGetValue(connectionId, out var current) &&
                registryEvent.ObservedAt > (current.LastMessageAt ?? DateTimeOffset.MinValue))
            {
                _watchByConnection[connectionId] = current with
                {
                    LastMessageAt = registryEvent.ObservedAt,
                    UpdatedAt = notify ? DateTimeOffset.UtcNow : current.UpdatedAt
                };
            }
        }

        if (remoteChanged)
        {
            RecalculateDeployments();
        }

        if (notify || remoteChanged)
        {
            RaiseChanged();
        }
    }

    public void SetRegistryWatchState(
        GeometryRegistryWatchState state,
        bool notify = true)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        var connectionId = RequireConnectionIdValue(state.ConnectionId);
        var changed = false;
        lock (_stateGate)
        {
            var normalized = state with { ConnectionId = connectionId };
            if (!_watchByConnection.TryGetValue(connectionId, out var previous) ||
                WatchStateMeaningfullyChanged(previous, normalized))
            {
                _watchByConnection[connectionId] = normalized;
                changed = true;
            }
            else if (normalized.LastMessageAt > previous.LastMessageAt)
            {
                _watchByConnection[connectionId] = previous with
                {
                    LastMessageAt = normalized.LastMessageAt
                };
            }
        }

        if (notify && changed)
        {
            RaiseChanged();
        }
    }

    public void ClearRemoteConnection(
        string connectionId,
        GeometryRegistryWatchState finalState)
    {
        ThrowIfDisposed();
        var normalizedConnectionId = RequireConnectionIdValue(connectionId);
        ArgumentNullException.ThrowIfNull(finalState);
        lock (_stateGate)
        {
            _remoteByConnection.Remove(normalizedConnectionId);
            _registryByConnection.Remove(normalizedConnectionId);
            _loadedRemoteConnections.Remove(normalizedConnectionId);
            _watchByConnection[normalizedConnectionId] = finalState with
            {
                ConnectionId = normalizedConnectionId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        RecalculateDeployments();
        RaiseChanged();
    }

    public async Task RemoveLocalAsync(
        string geometryId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _documents.RemoveAsync(geometryId, cancellationToken);
        RecalculateDeployments();
        RaiseChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _documents.Changed -= OnDocumentsChanged;
        _operationGate.Dispose();
    }

    private async Task RefreshRemoteCoreAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await _gateway.ListAsync(
                connectionId,
                new GeometryQuery(IncludeObjects: false, Refresh: true),
                cancellationToken);
            var registry = await _gateway.GetRegistryStatusAsync(
                connectionId,
                includeDetails: true,
                cancellationToken);
            lock (_stateGate)
            {
                _remoteByConnection[connectionId] = records
                    .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
                    .ToArray();
                _registryByConnection[connectionId] = registry;
                _loadedRemoteConnections.Add(connectionId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_stateGate)
            {
                _registryByConnection[connectionId] = GeometryRegistrySnapshot.Unknown(
                    connectionId,
                    ex.Message);
            }
            _logger.LogWarning(ex, "Could not refresh geometry registry for {ConnectionId}", connectionId);
            throw;
        }
    }

    private async Task<GeometryOperationAssessment> AssessRemoteOperationCoreAsync(
        string connectionId,
        string geometryId,
        GeometryRemoteOperationKind operation,
        CancellationToken cancellationToken)
    {
        var findings = new List<GeometryOperationFinding>();
        var local = _documents.TryGet(geometryId, out var foundLocal) ? foundLocal : null;
        GeometryValidationResult? localValidation = null;
        GeometryValidationResult? logosValidation = null;
        string? expectedRevision = null;

        var remoteStateAvailable = await RefreshRemoteForAssessmentAsync(
            connectionId,
            findings,
            cancellationToken);
        var remote = remoteStateAvailable ? FindRemote(connectionId, geometryId) : null;
        var deployment = remoteStateAvailable ? FindDeployment(connectionId, geometryId) : null;

        if (operation is GeometryRemoteOperationKind.Create or GeometryRemoteOperationKind.Update)
        {
            if (local is null)
            {
                findings.Add(Error(
                    "GEOMETRY_LOCAL_MISSING",
                    $"Geometry '{geometryId}' is not present in the local library.",
                    "Robot Command library"));
            }
            else
            {
                localValidation = _validator.Validate(local, requireAuthorableFrame: false);
                AddValidationFindings(findings, localValidation, "Robot Command validation");
            }
        }

        if (remoteStateAvailable)
        {
            switch (operation)
            {
                case GeometryRemoteOperationKind.Create:
                    if (remote is not null)
                    {
                        findings.Add(ConflictFinding(
                            "GEOMETRY_REMOTE_EXISTS_CONFLICT",
                            $"Geometry '{geometryId}' already exists on connection '{connectionId}'."));
                    }
                    break;

                case GeometryRemoteOperationKind.Update:
                    if (remote is null)
                    {
                        findings.Add(Error(
                            "GEOMETRY_REMOTE_MISSING",
                            $"Geometry '{geometryId}' is not present on connection '{connectionId}'.",
                            "Deployment comparison"));
                    }
                    else
                    {
                        expectedRevision = EmptyToNull(remote.Revision);
                        if (string.IsNullOrWhiteSpace(expectedRevision))
                        {
                            findings.Add(Error(
                                "GEOMETRY_REMOTE_REVISION_MISSING",
                                "Logos did not report a revision for this geometry, so Robot Command cannot perform a conflict-safe update.",
                                "Logos registry"));
                        }

                        if (deployment?.Status is GeometryDeploymentStatus.Conflict or GeometryDeploymentStatus.RemoteModified)
                        {
                            findings.Add(ConflictFinding(
                                "GEOMETRY_REMOTE_CHANGED",
                                deployment.Detail));
                        }
                        else if (deployment?.Status != GeometryDeploymentStatus.LocalModified)
                        {
                            findings.Add(Error(
                                "GEOMETRY_UPDATE_NOT_REQUIRED",
                                deployment?.Detail ?? "The local geometry is not known to be a safe update candidate.",
                                "Deployment comparison"));
                        }

                        if (local is not null &&
                            string.Equals(local.SourceConnectionId, connectionId, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(local.SourceRevision) &&
                            !string.Equals(local.SourceRevision, remote.Revision, StringComparison.Ordinal))
                        {
                            findings.Add(ConflictFinding(
                                "GEOMETRY_REVISION_CHANGED",
                                $"The remote revision changed from '{local.SourceRevision}' to '{remote.Revision}' after the last known deployment."));
                        }
                    }
                    break;

                case GeometryRemoteOperationKind.Delete:
                    if (remote is null)
                    {
                        findings.Add(Error(
                            "GEOMETRY_REMOTE_MISSING",
                            $"Geometry '{geometryId}' is not present on connection '{connectionId}'.",
                            "Logos registry"));
                    }

                    if (deployment?.Referenced == true || deployment?.Status == GeometryDeploymentStatus.InUse)
                    {
                        findings.Add(Error(
                            "GEOMETRY_REFERENCED",
                            "The geometry is reported as referenced or in use. Robot Command will not request forced deletion.",
                            "Deployment comparison"));
                    }
                    break;
            }
        }

        if (local is not null &&
            (operation is GeometryRemoteOperationKind.Create or GeometryRemoteOperationKind.Update) &&
            localValidation?.IsValid == true)
        {
            try
            {
                logosValidation = await _gateway.ValidateAsync(
                    connectionId,
                    local,
                    checkUpdateCompatibility: operation == GeometryRemoteOperationKind.Update,
                    cancellationToken);
                AddValidationFindings(findings, logosValidation, "Logos GeometryService");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logosValidation = GeometryValidationResult.Unavailable(ex.Message);
                findings.Add(new GeometryOperationFinding(
                    "GEOMETRY_LOGOS_VALIDATION_UNAVAILABLE",
                    GeometryValidationSeverity.Error,
                    $"Logos validation is unavailable: {ex.Message}",
                    "Logos GeometryService"));
            }
        }

        var state = AssessmentState(findings, logosValidation);
        var summary = AssessmentSummary(operation, state, findings);
        return new GeometryOperationAssessment(
            connectionId,
            geometryId,
            operation,
            state,
            summary,
            localValidation,
            logosValidation,
            deployment,
            expectedRevision,
            findings,
            DateTimeOffset.UtcNow);
    }

    private async Task<bool> RefreshRemoteForAssessmentAsync(
        string connectionId,
        ICollection<GeometryOperationFinding> findings,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await _gateway.ListAsync(
                connectionId,
                new GeometryQuery(IncludeObjects: false, Refresh: true),
                cancellationToken);
            lock (_stateGate)
            {
                _remoteByConnection[connectionId] = records
                    .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
                    .ToArray();
            }

            RecalculateDeployments();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Could not refresh geometry registry before guarded operation on {ConnectionId}",
                connectionId);
            findings.Add(new GeometryOperationFinding(
                "GEOMETRY_REGISTRY_REFRESH_UNAVAILABLE",
                GeometryValidationSeverity.Error,
                $"Robot Command could not refresh the Logos geometry registry before the operation: {ex.Message}",
                "Logos GeometryService"));
            return false;
        }
    }

    private async Task<GeometryCommandResult> VerifyMutationAsync(
        string connectionId,
        GeometryDocument local,
        GeometryCommandResult result,
        string operationPastTense,
        CancellationToken cancellationToken)
    {
        if (!result.Accepted)
        {
            return result;
        }

        RemoteGeometryObject? remote;
        try
        {
            remote = await _gateway.GetAsync(
                connectionId,
                local.GeometryId,
                refresh: true,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Logos accepted geometry mutation for {GeometryId}, but verification failed on {ConnectionId}",
                local.GeometryId,
                connectionId);
            return new GeometryCommandResult(
                false,
                GeometryCommandState.Failed,
                $"Logos accepted the geometry operation, but Robot Command could not verify the remote object: {ex.Message}",
                Validation: result.Validation,
                OperationId: result.OperationId);
        }

        if (remote is null)
        {
            return new GeometryCommandResult(
                false,
                GeometryCommandState.Failed,
                $"Logos accepted the geometry operation, but geometry '{local.GeometryId}' was not returned during verification.",
                Validation: result.Validation,
                OperationId: result.OperationId);
        }

        var recordHash = EmptyToNull(remote.Record.Sha256);
        var documentHash = EmptyToNull(remote.Document.ContentSha256);
        var verifiedHash = recordHash ?? documentHash;
        if (!HashesEqual(local.ContentSha256, verifiedHash))
        {
            UpsertRemote(remote.Record);
            RecalculateDeployments();
            RaiseChanged();
            return new GeometryCommandResult(
                false,
                GeometryCommandState.Conflict,
                $"Logos {operationPastTense} geometry '{local.GeometryId}', but the verified remote content does not match the local document.",
                remote,
                result.Validation,
                result.OperationId);
        }

        var verifiedRecord = remote.Record with { Sha256 = verifiedHash ?? string.Empty };
        var verifiedRemote = remote with { Record = verifiedRecord };
        UpsertRemote(verifiedRecord);
        var baseline = local with
        {
            SourceConnectionId = connectionId,
            SourceRevision = remote.Record.Revision,
            SourceSha256 = verifiedHash,
            IsDirty = false
        };
        await _documents.UpsertAsync(baseline, cancellationToken);
        RecalculateDeployments();
        RaiseChanged();
        return result with
        {
            Geometry = verifiedRemote,
            Message = $"{result.Message} Robot Command verified the remote revision and content hash."
        };
    }

    private static GeometryCommandResult RejectedFromAssessment(GeometryOperationAssessment assessment)
    {
        var primary = assessment.Findings.FirstOrDefault(item => item.Severity == GeometryValidationSeverity.Error);
        var message = primary is null
            ? assessment.Summary
            : $"{assessment.Summary} {primary.Message}";
        return new GeometryCommandResult(
            false,
            assessment.State == GeometryOperationAssessmentState.Conflict
                ? GeometryCommandState.Conflict
                : GeometryCommandState.Rejected,
            message,
            Validation: MergeValidation(assessment.LocalValidation, assessment.LogosValidation));
    }

    private static GeometryValidationResult? MergeValidation(
        params GeometryValidationResult?[] results)
    {
        var available = results.Where(item => item is not null).Cast<GeometryValidationResult>().ToArray();
        if (available.Length == 0)
        {
            return null;
        }

        var issues = available
            .SelectMany(item => item.Issues)
            .DistinctBy(item => (item.Code, item.Message, item.FieldPath, item.Source))
            .ToArray();
        var state = available.Any(item => item.State == GeometryValidationState.Invalid)
            ? GeometryValidationState.Invalid
            : available.Any(item => item.State == GeometryValidationState.Unavailable)
                ? GeometryValidationState.Unavailable
                : available.Any(item => item.State == GeometryValidationState.Warning)
                    ? GeometryValidationState.Warning
                    : GeometryValidationState.Valid;
        var summary = state switch
        {
            GeometryValidationState.Valid => "Robot Command and Logos validation passed.",
            GeometryValidationState.Warning => "Validation passed with warnings.",
            GeometryValidationState.Invalid => "Validation found blocking issues.",
            _ => "Validation could not be completed."
        };
        return new GeometryValidationResult(state, summary, issues);
    }

    private static void AddValidationFindings(
        ICollection<GeometryOperationFinding> findings,
        GeometryValidationResult validation,
        string fallbackSource)
    {
        foreach (var issue in validation.Issues)
        {
            findings.Add(new GeometryOperationFinding(
                issue.Code,
                issue.Severity,
                issue.Message,
                string.IsNullOrWhiteSpace(issue.Source) ? fallbackSource : issue.Source));
        }

        if (validation.State == GeometryValidationState.Invalid &&
            !validation.Issues.Any(item => item.Severity == GeometryValidationSeverity.Error))
        {
            findings.Add(Error(
                "GEOMETRY_VALIDATION_FAILED",
                validation.Summary,
                fallbackSource));
        }
        else if (validation.State == GeometryValidationState.Unavailable)
        {
            findings.Add(Error(
                "GEOMETRY_VALIDATION_UNAVAILABLE",
                validation.Summary,
                fallbackSource));
        }
        else if (validation.State == GeometryValidationState.Warning &&
                 !validation.Issues.Any(item => item.Severity == GeometryValidationSeverity.Warning))
        {
            findings.Add(new GeometryOperationFinding(
                "GEOMETRY_VALIDATION_WARNING",
                GeometryValidationSeverity.Warning,
                validation.Summary,
                fallbackSource));
        }
    }

    private static GeometryOperationAssessmentState AssessmentState(
        IReadOnlyList<GeometryOperationFinding> findings,
        GeometryValidationResult? logosValidation)
    {
        if (findings.Any(item => item.Code.Contains("CONFLICT", StringComparison.Ordinal) ||
                                 item.Code.Contains("CHANGED", StringComparison.Ordinal)))
        {
            return GeometryOperationAssessmentState.Conflict;
        }

        if (logosValidation?.State == GeometryValidationState.Unavailable ||
            findings.Any(item => item.Code.Contains("UNAVAILABLE", StringComparison.Ordinal)))
        {
            return GeometryOperationAssessmentState.Unavailable;
        }

        if (findings.Any(item => item.Severity == GeometryValidationSeverity.Error))
        {
            return GeometryOperationAssessmentState.Blocked;
        }

        return findings.Any(item => item.Severity == GeometryValidationSeverity.Warning)
            ? GeometryOperationAssessmentState.Warning
            : GeometryOperationAssessmentState.Ready;
    }

    private static string AssessmentSummary(
        GeometryRemoteOperationKind operation,
        GeometryOperationAssessmentState state,
        IReadOnlyList<GeometryOperationFinding> findings)
    {
        var operationText = operation.ToString().ToLowerInvariant();
        return state switch
        {
            GeometryOperationAssessmentState.Ready => $"Geometry is ready to {operationText} on Logos.",
            GeometryOperationAssessmentState.Warning => $"Geometry can be {operationText}d after reviewing {findings.Count(item => item.Severity == GeometryValidationSeverity.Warning)} warning(s).",
            GeometryOperationAssessmentState.Conflict => "The remote operation is blocked by a local/Logos conflict.",
            GeometryOperationAssessmentState.Unavailable => "The remote operation is blocked because Logos validation is unavailable.",
            _ => $"The remote operation is blocked by {findings.Count(item => item.Severity == GeometryValidationSeverity.Error)} issue(s)."
        };
    }

    private static GeometryOperationFinding Error(string code, string message, string source)
        => new(code, GeometryValidationSeverity.Error, message, source);

    private static GeometryOperationFinding ConflictFinding(string code, string message)
        => new(code, GeometryValidationSeverity.Error, message, "Deployment comparison");

    private void RecalculateDeployments()
    {
        var locals = _documents.Documents.ToDictionary(item => item.GeometryId, StringComparer.Ordinal);
        Dictionary<string, RemoteGeometryRecord[]> remotes;
        HashSet<string> loadedConnections;
        string[] watchedConnections;
        Dictionary<(string ConnectionId, string GeometryId), GeometryDeploymentRecord> previousDeployments;
        lock (_stateGate)
        {
            remotes = _remoteByConnection.ToDictionary(
                item => item.Key,
                item => item.Value.ToArray(),
                StringComparer.Ordinal);
            loadedConnections = new HashSet<string>(_loadedRemoteConnections, StringComparer.Ordinal);
            watchedConnections = _watchByConnection.Keys.ToArray();
            previousDeployments = _deployments.ToDictionary(
                item => (item.ConnectionId, item.GeometryId));
        }

        var connectionIds = remotes.Keys
            .Concat(watchedConnections)
            .Concat(locals.Values
                .Select(item => item.SourceConnectionId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var deployments = new List<GeometryDeploymentRecord>();
        foreach (var connectionId in connectionIds)
        {
            var remoteById = remotes.TryGetValue(connectionId, out var connectionRecords)
                ? connectionRecords.ToDictionary(item => item.GeometryId, StringComparer.Ordinal)
                : new Dictionary<string, RemoteGeometryRecord>(StringComparer.Ordinal);
            var geometryIds = locals.Keys
                .Concat(remoteById.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal);
            foreach (var geometryId in geometryIds)
            {
                locals.TryGetValue(geometryId, out var local);
                remoteById.TryGetValue(geometryId, out var remote);
                var deployment = BuildDeployment(
                    connectionId,
                    geometryId,
                    local,
                    remote,
                    loadedConnections.Contains(connectionId));
                if (previousDeployments.TryGetValue((connectionId, geometryId), out var previous) &&
                    previous.Referenced)
                {
                    deployment = deployment with
                    {
                        Referenced = true,
                        Status = remote is null ? deployment.Status : GeometryDeploymentStatus.InUse,
                        Detail = remote is null
                            ? $"{deployment.Detail} The last known state also reports this geometry as referenced."
                            : "The geometry is referenced or in use on this Logos connection."
                    };
                }

                deployments.Add(deployment);
            }
        }

        lock (_stateGate)
        {
            _deployments = deployments.ToArray();
        }
    }

    private GeometryDeploymentRecord BuildDeployment(
        string connectionId,
        string geometryId,
        GeometryDocument? local,
        RemoteGeometryRecord? remote,
        bool remoteInventoryLoaded)
    {
        if (local is null && remote is not null)
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.RemoteOnly,
                null,
                remote,
                "The geometry exists on Logos but is not in the local library.");
        }

        if (local is not null)
        {
            var validation = _validator.Validate(local, requireAuthorableFrame: false);
            if (!validation.IsValid)
            {
                return Deployment(
                    geometryId,
                    connectionId,
                    GeometryDeploymentStatus.Invalid,
                    local,
                    remote,
                    validation.Summary);
            }
        }

        if (local is not null && remote is null && !remoteInventoryLoaded)
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.Unknown,
                local,
                null,
                "The live Logos registry inventory is not currently authoritative for this connection.");
        }

        if (local is not null && remote is null)
        {
            var wasDeployedHere = string.Equals(
                local.SourceConnectionId,
                connectionId,
                StringComparison.Ordinal);
            return Deployment(
                geometryId,
                connectionId,
                wasDeployedHere
                    ? GeometryDeploymentStatus.MissingRemote
                    : GeometryDeploymentStatus.LocalOnly,
                local,
                null,
                wasDeployedHere
                    ? "The last known Logos copy is no longer present on this connection."
                    : "The geometry exists only in the local library.");
        }

        if (local is null || remote is null)
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.Unknown,
                local,
                remote,
                "Geometry deployment state is unknown.");
        }

        if (HashesEqual(local.ContentSha256, remote.Sha256))
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.Matching,
                local,
                remote,
                "Local and Logos geometry content match.");
        }

        var sameSource = string.Equals(local.SourceConnectionId, connectionId, StringComparison.Ordinal);
        var hasBaseline = sameSource && !string.IsNullOrWhiteSpace(local.SourceSha256);
        if (!hasBaseline)
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.Conflict,
                local,
                remote,
                "Local and Logos content differ and no common deployment baseline is available.");
        }

        var localChanged = !HashesEqual(local.ContentSha256, local.SourceSha256);
        var remoteChanged = !HashesEqual(remote.Sha256, local.SourceSha256);
        if (localChanged && remoteChanged)
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.Conflict,
                local,
                remote,
                "Both local and Logos content changed after the last known deployment.");
        }

        if (remoteChanged)
        {
            return Deployment(
                geometryId,
                connectionId,
                GeometryDeploymentStatus.RemoteModified,
                local,
                remote,
                "The Logos copy changed after the last known deployment.");
        }

        return Deployment(
            geometryId,
            connectionId,
            GeometryDeploymentStatus.LocalModified,
            local,
            remote,
            "The local copy changed after the last known deployment.");
    }

    private static GeometryDeploymentRecord Deployment(
        string geometryId,
        string connectionId,
        GeometryDeploymentStatus status,
        GeometryDocument? local,
        RemoteGeometryRecord? remote,
        string detail)
        => new(
            geometryId,
            connectionId,
            status,
            local?.ContentSha256,
            remote?.Sha256,
            remote?.Revision,
            remote?.UpdatedAt,
            detail);

    private GeometryDocument GetLocalRequired(string geometryId)
        => _documents.TryGet(geometryId, out var document) && document is not null
            ? document
            : throw new KeyNotFoundException($"Geometry document '{geometryId}' was not found in the local library.");

    private RemoteGeometryRecord? FindRemote(string connectionId, string geometryId)
    {
        RequireConnectionId(connectionId);
        RequireGeometryId(geometryId);
        lock (_stateGate)
        {
            return _remoteByConnection.TryGetValue(connectionId.Trim(), out var records)
                ? records.FirstOrDefault(item => string.Equals(item.GeometryId, geometryId.Trim(), StringComparison.Ordinal))
                : null;
        }
    }

    private GeometryDeploymentRecord? FindDeployment(string connectionId, string geometryId)
    {
        lock (_stateGate)
        {
            return _deployments.FirstOrDefault(item =>
                string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal) &&
                string.Equals(item.GeometryId, geometryId, StringComparison.Ordinal));
        }
    }

    private void UpsertRemote(RemoteGeometryRecord record)
    {
        lock (_stateGate)
        {
            UpsertRemoteLocked(record);
        }
    }

    private void UpsertRemoteLocked(RemoteGeometryRecord record)
    {
        var records = _remoteByConnection.TryGetValue(record.ConnectionId, out var current)
            ? current.ToDictionary(item => item.GeometryId, StringComparer.Ordinal)
            : new Dictionary<string, RemoteGeometryRecord>(StringComparer.Ordinal);
        records[record.GeometryId] = record;
        _remoteByConnection[record.ConnectionId] = records.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GeometryId, StringComparer.Ordinal)
            .ToArray();
    }

    private void RemoveRemote(string connectionId, string geometryId)
    {
        lock (_stateGate)
        {
            RemoveRemoteLocked(connectionId, geometryId);
        }
    }

    private void RemoveRemoteLocked(string connectionId, string geometryId)
    {
        if (!_remoteByConnection.TryGetValue(connectionId, out var current))
        {
            return;
        }

        _remoteByConnection[connectionId] = current
            .Where(item => !string.Equals(item.GeometryId, geometryId, StringComparison.Ordinal))
            .ToArray();
    }

    private static bool WatchStateMeaningfullyChanged(
        GeometryRegistryWatchState previous,
        GeometryRegistryWatchState current)
        => previous.Status != current.Status ||
           previous.RestartCount != current.RestartCount ||
           !string.Equals(previous.Summary, current.Summary, StringComparison.Ordinal) ||
           !string.Equals(previous.LastError, current.LastError, StringComparison.Ordinal);

    private void OnDocumentsChanged(object? sender, EventArgs e)
    {
        RecalculateDeployments();
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static bool HashesEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireConnectionIdValue(string connectionId)
    {
        RequireConnectionId(connectionId);
        return connectionId.Trim();
    }

    private static string RequireGeometryIdValue(string geometryId)
    {
        RequireGeometryId(geometryId);
        return geometryId.Trim();
    }

    private static void RequireConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A Logos connection ID is required.", nameof(connectionId));
        }
    }

    private static void RequireGeometryId(string geometryId)
    {
        if (string.IsNullOrWhiteSpace(geometryId))
        {
            throw new ArgumentException("A geometry ID is required.", nameof(geometryId));
        }
    }
}
