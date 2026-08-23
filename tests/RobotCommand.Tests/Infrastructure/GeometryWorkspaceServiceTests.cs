using Microsoft.Extensions.Logging.Abstractions;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryWorkspaceServiceTests
{
    [Fact]
    public async Task CreateLocalDraft_DoesNotPersistUntilTheEditorSavesIt()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            using var service = Service(store, new FakeGateway());

            var draft = await service.CreateLocalDraftAsync(GeometryDocumentKind.PointOfInterest);

            Assert.Empty(store.Documents);
            var saved = await service.SaveLocalAsync(draft with
            {
                Points = [GeometryDocumentPoint.GlobalWgs84(-79.42, 43.73, 20)]
            });
            Assert.Equal(saved.GeometryId, Assert.Single(store.Documents).GeometryId);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SaveLocal_PersistsAnIncompleteRouteForAutosaveAuthoring()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            using var service = Service(store, new FakeGateway());

            var draft = await service.CreateLocalDraftAsync(GeometryDocumentKind.WaypointSequence);
            var saved = await service.SaveLocalAsync(draft with
            {
                Points = [GeometryDocumentPoint.GlobalWgs84(-79.42, 43.73, 20)]
            });

            Assert.Equal(saved.GeometryId, Assert.Single(store.Documents).GeometryId);
            Assert.Single(saved.Points);
            Assert.Contains(store.Issues, item =>
                item.GeometryId == saved.GeometryId &&
                item.Code == "GEOMETRY_ROUTE_POINT_COUNT");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Refresh_ReconcilesLocalMatchingAndRemoteOnlyGeometry()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var local = await store.UpsertAsync(Route("route-local", "Local route"));
            var gateway = new FakeGateway
            {
                Listed =
                [
                    RemoteRecord("connection-1", local.GeometryId, local.ContentSha256, "revision-1"),
                    RemoteRecord("connection-1", "remote-only", "remote-sha", "revision-2")
                ]
            };
            using var service = Service(store, gateway);

            await service.RefreshAsync("connection-1");

            Assert.Equal(2, service.RemoteRecords.Count);
            Assert.Contains(service.Deployments, item =>
                item.GeometryId == "route-local" &&
                item.Status == GeometryDeploymentStatus.Matching);
            Assert.Contains(service.Deployments, item =>
                item.GeometryId == "remote-only" &&
                item.Status == GeometryDeploymentStatus.RemoteOnly);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Refresh_DoesNotOverwriteLocalDocumentWhenRemoteDiffers()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            await store.UpsertAsync(Route("route-alpha", "Local route"));
            var gateway = new FakeGateway
            {
                Listed = [RemoteRecord("connection-1", "route-alpha", "different-remote-sha", "revision-9")]
            };
            using var service = Service(store, gateway);

            await service.RefreshAsync("connection-1");

            Assert.Equal("Local route", Assert.Single(service.LocalDocuments).DisplayName);
            Assert.Equal(
                GeometryDeploymentStatus.Conflict,
                Assert.Single(service.Deployments).Status);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Pull_RequiresExplicitReplacementAndRecordsRemoteBaseline()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            await store.UpsertAsync(Route("route-alpha", "Local route"));
            var remoteDocument = Prepared(Route("route-alpha", "Remote route"));
            var remoteRecord = RemoteRecord(
                "connection-1",
                remoteDocument.GeometryId,
                remoteDocument.ContentSha256,
                "revision-4");
            var gateway = new FakeGateway
            {
                Objects =
                {
                    [remoteDocument.GeometryId] = new RemoteGeometryObject(remoteDocument, remoteRecord)
                }
            };
            using var service = Service(store, gateway);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PullAsync("connection-1", "route-alpha"));

            var pulled = await service.PullAsync(
                "connection-1",
                "route-alpha",
                replaceLocal: true);

            Assert.Equal("Remote route", pulled.DisplayName);
            Assert.Equal(GeometryDocumentOrigin.PulledFromLogos, pulled.Origin);
            Assert.Equal("connection-1", pulled.SourceConnectionId);
            Assert.Equal("revision-4", pulled.SourceRevision);
            Assert.Equal(remoteRecord.Sha256, pulled.SourceSha256);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Update_UsesRemoteRevisionAndAdvancesDeploymentBaseline()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var baseline = Prepared(Route("route-alpha", "Baseline"));
            var baselineSha = baseline.ContentSha256;
            var modified = await store.UpsertAsync(baseline with
            {
                DisplayName = "Locally modified",
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = baselineSha
            });
            var remoteBefore = RemoteRecord(
                "connection-1",
                modified.GeometryId,
                baselineSha,
                "revision-1");
            var remoteAfterDocument = Prepared(modified);
            var remoteAfter = RemoteRecord(
                "connection-1",
                modified.GeometryId,
                remoteAfterDocument.ContentSha256,
                "revision-2");
            var gateway = new FakeGateway
            {
                Listed = [remoteBefore],
                Objects =
                {
                    [modified.GeometryId] = new RemoteGeometryObject(remoteAfterDocument, remoteAfter)
                },
                UpdateResult = new GeometryCommandResult(
                    true,
                    GeometryCommandState.Accepted,
                    "Updated",
                    new RemoteGeometryObject(remoteAfterDocument, remoteAfter))
            };
            using var service = Service(store, gateway);
            await service.RefreshAsync("connection-1");
            Assert.Equal(
                GeometryDeploymentStatus.LocalModified,
                Assert.Single(service.Deployments).Status);

            var result = await service.UpdateRemoteAsync("connection-1", modified.GeometryId);

            Assert.True(result.Accepted);
            Assert.Equal("revision-1", gateway.LastUpdate!.ExpectedRevision);
            Assert.True(store.TryGet(modified.GeometryId, out var saved));
            Assert.Equal("revision-2", saved!.SourceRevision);
            Assert.Equal(remoteAfter.Sha256, saved.SourceSha256);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DeleteRemote_NeverForcesAndDoesNotRemoveLocalDocument()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var prepared = Prepared(Route("route-alpha", "Local route"));
            var local = await store.UpsertAsync(prepared with
            {
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = prepared.ContentSha256
            });
            var gateway = new FakeGateway
            {
                Listed = [RemoteRecord("connection-1", local.GeometryId, local.ContentSha256, "revision-1")],
                DeleteResult = new GeometryCommandResult(
                    true,
                    GeometryCommandState.Accepted,
                    "Deleted")
            };
            using var service = Service(store, gateway);
            await service.RefreshAsync("connection-1");

            var result = await service.DeleteRemoteAsync("connection-1", local.GeometryId);

            Assert.True(result.Accepted);
            Assert.NotNull(gateway.LastDelete);
            Assert.False(gateway.LastDelete!.AllowDeleteReferenced);
            Assert.True(store.TryGet(local.GeometryId, out _));
            Assert.Empty(service.RemoteRecords);
            Assert.Equal(
                GeometryDeploymentStatus.MissingRemote,
                Assert.Single(service.Deployments).Status);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }


    [Fact]
    public async Task AssessUpdate_SeparatesLocalLogosAndDeploymentChecks()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var baseline = Prepared(Route("route-alpha", "Baseline"));
            var local = await store.UpsertAsync(baseline with
            {
                DisplayName = "Changed locally",
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = baseline.ContentSha256
            });
            var gateway = new FakeGateway
            {
                Listed = [RemoteRecord("connection-1", local.GeometryId, baseline.ContentSha256, "revision-1")],
                ValidationResult = new GeometryValidationResult(
                    GeometryValidationState.Warning,
                    "Logos accepted with warnings.",
                    [new GeometryValidationIssue(
                        "GEOMETRY_POLICY_WARNING",
                        GeometryValidationSeverity.Warning,
                        "Review the zone policy.",
                        Source: "Logos GeometryService")])
            };
            using var service = Service(store, gateway);
            await service.RefreshAsync("connection-1");

            var assessment = await service.AssessRemoteOperationAsync(
                "connection-1",
                local.GeometryId,
                GeometryRemoteOperationKind.Update);

            Assert.True(assessment.CanExecute);
            Assert.Equal(GeometryOperationAssessmentState.Warning, assessment.State);
            Assert.Equal(GeometryValidationState.Valid, assessment.LocalValidation!.State);
            Assert.Equal(GeometryValidationState.Warning, assessment.LogosValidation!.State);
            Assert.Equal(GeometryDeploymentStatus.LocalModified, assessment.Deployment!.Status);
            Assert.Equal("revision-1", assessment.ExpectedRevision);
            Assert.True(gateway.LastCheckUpdateCompatibility);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AssessUpdate_BlocksWhenRemoteRevisionChanged()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var baseline = Prepared(Route("route-alpha", "Baseline"));
            var local = await store.UpsertAsync(baseline with
            {
                DisplayName = "Changed locally",
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = baseline.ContentSha256
            });
            var gateway = new FakeGateway
            {
                Listed = [RemoteRecord("connection-1", local.GeometryId, "remote-changed", "revision-2")]
            };
            using var service = Service(store, gateway);
            await service.RefreshAsync("connection-1");

            var assessment = await service.AssessRemoteOperationAsync(
                "connection-1",
                local.GeometryId,
                GeometryRemoteOperationKind.Update);

            Assert.False(assessment.CanExecute);
            Assert.Equal(GeometryOperationAssessmentState.Conflict, assessment.State);
            Assert.Contains(assessment.Findings, item => item.Code == "GEOMETRY_REVISION_CHANGED");
            Assert.Null(gateway.LastUpdate);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task UpdateRemote_RefreshesRegistryImmediatelyBeforeSendingRpc()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var baseline = Prepared(Route("route-alpha", "Baseline"));
            var local = await store.UpsertAsync(baseline with
            {
                DisplayName = "Changed locally",
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = baseline.ContentSha256
            });
            var gateway = new FakeGateway
            {
                Listed = [RemoteRecord("connection-1", local.GeometryId, baseline.ContentSha256, "revision-1")]
            };
            using var service = Service(store, gateway);
            await service.RefreshAsync("connection-1");
            gateway.Listed = [RemoteRecord("connection-1", local.GeometryId, "remote-changed", "revision-2")];

            var result = await service.UpdateRemoteAsync("connection-1", local.GeometryId);

            Assert.False(result.Accepted);
            Assert.Equal(GeometryCommandState.Conflict, result.State);
            Assert.Null(gateway.LastUpdate);
            Assert.Contains("changed", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Create_VerifiesRemoteContentBeforeAdvancingBaseline()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var local = await store.UpsertAsync(Route("route-alpha", "Local route"));
            var mismatched = Prepared(Route("route-alpha", "Different remote route"));
            var gateway = new FakeGateway
            {
                CreateResult = new GeometryCommandResult(true, GeometryCommandState.Accepted, "Created"),
                Objects =
                {
                    [local.GeometryId] = new RemoteGeometryObject(
                        mismatched,
                        RemoteRecord("connection-1", local.GeometryId, mismatched.ContentSha256, "revision-1"))
                }
            };
            using var service = Service(store, gateway);

            var result = await service.CreateRemoteAsync("connection-1", local.GeometryId);

            Assert.False(result.Accepted);
            Assert.Equal(GeometryCommandState.Conflict, result.State);
            Assert.True(store.TryGet(local.GeometryId, out var saved));
            Assert.Null(saved!.SourceRevision);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DeleteRemote_BlocksReferencedGeometryBeforeCallingGateway()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var local = await store.UpsertAsync(Route("route-alpha", "Local route"));
            var remote = RemoteRecord("connection-1", local.GeometryId, local.ContentSha256, "revision-1");
            var gateway = new FakeGateway { Listed = [remote] };
            using var service = Service(store, gateway);
            await service.RefreshAsync("connection-1");
            var deployment = Assert.Single(service.Deployments);
            typeof(GeometryWorkspaceService)
                .GetField("_deployments", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(service, new[] { deployment with { Referenced = true } });

            var result = await service.DeleteRemoteAsync("connection-1", local.GeometryId);

            Assert.False(result.Accepted);
            Assert.Null(gateway.LastDelete);
            Assert.Contains("referenced", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }


    [Fact]
    public async Task ConflictCopies_PreserveEitherSideUnderIndependentLocalIds()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var local = await store.UpsertAsync(Route("route-alpha", "Local route"));
            var remoteDocument = Prepared(Route("route-alpha", "Remote route"));
            var gateway = new FakeGateway
            {
                Objects =
                {
                    [remoteDocument.GeometryId] = new RemoteGeometryObject(
                        remoteDocument,
                        RemoteRecord("connection-1", remoteDocument.GeometryId, remoteDocument.ContentSha256, "revision-4"))
                }
            };
            using var service = Service(store, gateway);

            var localCopy = await service.DuplicateLocalAsync(local.GeometryId, "route-alpha-local-copy");
            var remoteCopy = await service.PullAsLocalCopyAsync(
                "connection-1",
                remoteDocument.GeometryId,
                "route-alpha-remote-copy");

            Assert.Equal("route-alpha-local-copy", localCopy.GeometryId);
            Assert.Equal("Local route copy", localCopy.DisplayName);
            Assert.Null(localCopy.SourceConnectionId);
            Assert.Null(localCopy.SourceRevision);
            Assert.Equal("route-alpha-remote-copy", remoteCopy.GeometryId);
            Assert.Equal("Remote route remote copy", remoteCopy.DisplayName);
            Assert.Null(remoteCopy.SourceConnectionId);
            Assert.Null(remoteCopy.SourceRevision);
            Assert.Equal(3, service.LocalDocuments.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ReplaceRemoteSnapshot_EmptyInventoryMakesMissingRemoteAuthoritative()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var prepared = Prepared(Route("route-alpha", "Route"));
            await store.UpsertAsync(prepared with
            {
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = prepared.ContentSha256
            });
            using var service = Service(store, new FakeGateway());

            service.SetRegistryWatchState(GeometryRegistryWatchState.Starting("connection-1"));
            Assert.Equal(GeometryDeploymentStatus.Unknown, Assert.Single(service.Deployments).Status);

            service.ReplaceRemoteSnapshot(
                "connection-1",
                [],
                Registry("connection-1", 0));

            Assert.Equal(GeometryDeploymentStatus.MissingRemote, Assert.Single(service.Deployments).Status);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RegistryUpdate_RecalculatesDirtyLocalAsConflict()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var baseline = Prepared(Route("route-alpha", "Baseline"));
            await store.UpsertAsync(baseline with
            {
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = baseline.ContentSha256
            });
            using var service = Service(store, new FakeGateway());
            service.ReplaceRemoteSnapshot(
                "connection-1",
                [RemoteRecord("connection-1", baseline.GeometryId, baseline.ContentSha256, "revision-1")],
                Registry("connection-1", 1));

            await service.SaveLocalAsync(baseline with
            {
                DisplayName = "Changed locally",
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = baseline.ContentSha256
            });
            Assert.Equal(GeometryDeploymentStatus.LocalModified, Assert.Single(service.Deployments).Status);

            service.ApplyRegistryEvent(new GeometryRegistryEvent(
                "connection-1",
                GeometryRegistryEventKind.Updated,
                Registry("connection-1", 1),
                RemoteRecord("connection-1", baseline.GeometryId, "remote-changed", "revision-2"),
                null,
                DateTimeOffset.UtcNow));

            Assert.Equal(GeometryDeploymentStatus.Conflict, Assert.Single(service.Deployments).Status);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Heartbeat_UpdatesFreshnessWithoutRaisingWorkspaceChanged()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            using var service = Service(store, new FakeGateway());
            service.SetRegistryWatchState(new GeometryRegistryWatchState(
                "connection-1",
                GeometryRegistryWatchStatusKind.Live,
                "Live",
                DateTimeOffset.UtcNow.AddSeconds(-5),
                0,
                null,
                DateTimeOffset.UtcNow));
            var changed = 0;
            service.Changed += (_, _) => changed++;
            var observedAt = DateTimeOffset.UtcNow;

            service.ApplyRegistryEvent(new GeometryRegistryEvent(
                "connection-1",
                GeometryRegistryEventKind.Heartbeat,
                null,
                null,
                null,
                observedAt));

            Assert.Equal(0, changed);
            Assert.Equal(observedAt, Assert.Single(service.RegistryWatchStates).LastMessageAt);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ClearRemoteConnection_RemovesLiveInventoryAndLeavesDeploymentUnknown()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new GeometryDocumentStore(root);
            var prepared = Prepared(Route("route-alpha", "Route"));
            await store.UpsertAsync(prepared with
            {
                SourceConnectionId = "connection-1",
                SourceRevision = "revision-1",
                SourceSha256 = prepared.ContentSha256
            });
            using var service = Service(store, new FakeGateway());
            service.ReplaceRemoteSnapshot(
                "connection-1",
                [RemoteRecord("connection-1", prepared.GeometryId, prepared.ContentSha256, "revision-1")],
                Registry("connection-1", 1));
            Assert.Equal(GeometryDeploymentStatus.Matching, Assert.Single(service.Deployments).Status);

            service.ClearRemoteConnection(
                "connection-1",
                GeometryRegistryWatchState.Stopped("connection-1", "Disconnected"));

            Assert.Empty(service.RemoteRecords);
            Assert.Empty(service.RegistrySnapshots);
            Assert.Equal(GeometryDeploymentStatus.Unknown, Assert.Single(service.Deployments).Status);
            Assert.Equal(GeometryRegistryWatchStatusKind.Stopped, Assert.Single(service.RegistryWatchStates).Status);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static GeometryRegistrySnapshot Registry(string connectionId, int objectCount)
        => new(
            connectionId,
            GeometryRegistryState.Ready,
            "Healthy",
            "Ready",
            "OK",
            "Registry ready",
            objectCount,
            "signature",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);

    private static GeometryWorkspaceService Service(
        IGeometryDocumentStore store,
        IGeometryGateway gateway)
    {
        var codec = new GeometryDocumentCodec();
        return new GeometryWorkspaceService(
            store,
            gateway,
            new GeometryDocumentValidator(codec),
            NullLogger<GeometryWorkspaceService>.Instance);
    }

    private static GeometryDocument Route(string id, string name)
        => GeometryDocument.Create(id, name, GeometryDocumentKind.WaypointSequence) with
        {
            Points =
            [
                GeometryDocumentPoint.GlobalWgs84(-79.3832, 43.6532, 50),
                GeometryDocumentPoint.GlobalWgs84(-79.3820, 43.6540, 50)
            ]
        };

    private static GeometryDocument Prepared(GeometryDocument document)
        => new GeometryDocumentCodec().PrepareForSave(document);

    private static RemoteGeometryRecord RemoteRecord(
        string connectionId,
        string geometryId,
        string sha256,
        string revision)
        => new(
            connectionId,
            geometryId,
            GeometryDocumentKind.WaypointSequence,
            geometryId,
            GeometryCoordinateFrame.GlobalWgs84,
            false,
            2,
            0,
            128,
            sha256,
            revision,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"robot-command-geometry-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeGateway : IGeometryGateway
    {
        public bool IsAvailable => true;

        public string AvailabilityMessage => "Geometry available";

        public IReadOnlyList<RemoteGeometryRecord> Listed { get; set; } = [];

        public Dictionary<string, RemoteGeometryObject> Objects { get; init; } = new(StringComparer.Ordinal);

        public GeometryValidationResult ValidationResult { get; set; } = new(
            GeometryValidationState.Valid,
            "Valid",
            []);

        public bool LastCheckUpdateCompatibility { get; private set; }

        public GeometryCommandResult CreateResult { get; set; } = new(
            false,
            GeometryCommandState.Rejected,
            "Create not configured");

        public GeometryCommandResult UpdateResult { get; set; } = new(
            false,
            GeometryCommandState.Rejected,
            "Update not configured");

        public GeometryCommandResult DeleteResult { get; set; } = new(
            false,
            GeometryCommandState.Rejected,
            "Delete not configured");

        public GeometryCreateRequest? LastCreate { get; private set; }

        public GeometryUpdateRequest? LastUpdate { get; private set; }

        public GeometryDeleteRequest? LastDelete { get; private set; }

        public Task<IReadOnlyList<RemoteGeometryRecord>> ListAsync(
            string connectionId,
            GeometryQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Listed);

        public Task<RemoteGeometryObject?> GetAsync(
            string connectionId,
            string geometryId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Objects.TryGetValue(geometryId, out var value) ? value : null);

        public Task<GeometryValidationResult> ValidateAsync(
            string connectionId,
            GeometryDocument document,
            bool checkUpdateCompatibility = false,
            CancellationToken cancellationToken = default)
        {
            LastCheckUpdateCompatibility = checkUpdateCompatibility;
            return Task.FromResult(ValidationResult);
        }

        public Task<GeometryCommandResult> CreateAsync(
            GeometryCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCreate = request;
            return Task.FromResult(CreateResult);
        }

        public Task<GeometryCommandResult> UpdateAsync(
            GeometryUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUpdate = request;
            return Task.FromResult(UpdateResult);
        }

        public Task<GeometryCommandResult> DeleteAsync(
            GeometryDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastDelete = request;
            return Task.FromResult(DeleteResult);
        }

        public Task<GeometryRegistrySnapshot> GetRegistryStatusAsync(
            string connectionId,
            bool includeDetails = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeometryRegistrySnapshot(
                connectionId,
                GeometryRegistryState.Ready,
                "Healthy",
                "Ready",
                "OK",
                "Registry ready",
                Listed.Count,
                "signature",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                []));

        public async IAsyncEnumerable<GeometryRegistryEvent> WatchAsync(
            string connectionId,
            GeometryQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
