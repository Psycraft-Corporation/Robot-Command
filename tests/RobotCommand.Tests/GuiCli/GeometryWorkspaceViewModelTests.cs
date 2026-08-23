using RobotCommand.Infrastructure;
using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using RobotCommand.State;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryWorkspaceViewModelTests
{
    [Fact]
    public void Presentation_FiltersRemoteRecordsToSelectedConnection()
    {
        var local = Prepared(Route("route-local", "Local route"));
        var workspace = new FakeWorkspace
        {
            LocalDocuments = [local],
            RemoteRecords =
            [
                Remote("connection-1", "remote-one"),
                Remote("connection-2", "remote-two")
            ],
            Deployments =
            [
                Deployment("connection-1", local.GeometryId, GeometryDeploymentStatus.LocalOnly),
                Deployment("connection-1", "remote-one", GeometryDeploymentStatus.RemoteOnly),
                Deployment("connection-2", "remote-two", GeometryDeploymentStatus.RemoteOnly)
            ]
        };
        var connections = Connections();
        var viewModel = Create(workspace, connections);

        Assert.Equal("connection-1", viewModel.SelectedConnection!.Id);
        Assert.Single(viewModel.LocalItems);
        Assert.Equal("remote-one", Assert.Single(viewModel.RemoteItems).GeometryId);
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);

        viewModel.SelectedConnection = connections.Items.Single(item => item.Id == "connection-2");

        Assert.Equal("remote-two", Assert.Single(viewModel.RemoteItems).GeometryId);
        Assert.Equal("route-local", viewModel.SelectedLocal!.GeometryId);
    }

    [Fact]
    public void WorkspaceRefresh_PreservesLocalSelectionByGeometryId()
    {
        var workspace = new FakeWorkspace
        {
            LocalDocuments = [Prepared(Route("route-local", "Original"))]
        };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);

        workspace.LocalDocuments = [Prepared(Route("route-local", "Replacement record"))];
        workspace.RaiseChanged();

        Assert.Equal("route-local", viewModel.SelectedLocal!.GeometryId);
        Assert.Equal("Replacement record", viewModel.SelectedLocal.DisplayName);
    }

    [Fact]
    public void DeleteRemoteCommand_RequiresExactTypedConfirmation()
    {
        var remote = Remote("connection-1", "route-alpha");
        var workspace = new FakeWorkspace
        {
            RemoteRecords = [remote],
            Deployments = [Deployment("connection-1", remote.GeometryId, GeometryDeploymentStatus.RemoteOnly)]
        };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedRemote = Assert.Single(viewModel.RemoteItems);

        viewModel.RemoteDeleteConfirmation = "delete route-alpha";
        Assert.False(viewModel.DeleteRemoteCommand.CanExecute(null));

        viewModel.RemoteDeleteConfirmation = "DELETE route-alpha";
        Assert.True(viewModel.DeleteRemoteCommand.CanExecute(null));
    }


    [Fact]
    public void RemoteMutationCommands_BlockUnsavedAuthoringChanges()
    {
        var local = Prepared(Route("route-alpha", "Route Alpha"));
        var workspace = new FakeWorkspace
        {
            LocalDocuments = [local],
            Deployments = [Deployment("connection-1", local.GeometryId, GeometryDeploymentStatus.LocalOnly)]
        };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);

        Assert.True(viewModel.CreateRemoteCommand.CanExecute(null));
        Assert.True(viewModel.AssessRemoteCommand.CanExecute(null));

        viewModel.AuthoringDisplayName = "Unsaved name";

        Assert.True(viewModel.AuthoringDirty);
        Assert.False(viewModel.CreateRemoteCommand.CanExecute(null));
        Assert.False(viewModel.AssessRemoteCommand.CanExecute(null));
    }

    [Fact]
    public void EditOnMap_StartsCreateModeForEmptyLocalDraft()
    {
        var editSession = new GeometryEditSession();
        var workspace = new FakeWorkspace
        {
            LocalDocuments =
            [
                GeometryDocument.Create(
                    "poi-draft",
                    "PoI draft",
                    GeometryDocumentKind.PointOfInterest)
            ]
        };
        var viewModel = Create(workspace, Connections(), editSession);
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);

        Assert.True(viewModel.EditOnMapCommand.CanExecute(null));
        viewModel.EditOnMapCommand.Execute(null);

        Assert.Equal(GeometryEditSessionStage.Editing, editSession.Snapshot.Stage);
        Assert.Equal(MapInteractionMode.CreatePoint, editSession.Snapshot.InteractionMode);
        Assert.Equal("poi-draft", editSession.Snapshot.Draft!.GeometryId);
    }

    [Fact]
    public void SearchAndKindFiltersApplyToLocalAndRemoteLists()
    {
        var workspace = new FakeWorkspace
        {
            LocalDocuments =
            [
                Prepared(Route("route-alpha", "Alpha route")),
                Prepared(Point("poi-bravo", "Bravo point"))
            ],
            RemoteRecords =
            [
                Remote("connection-1", "route-remote", GeometryDocumentKind.WaypointSequence),
                Remote("connection-1", "poi-remote", GeometryDocumentKind.PointOfInterest)
            ],
            Deployments =
            [
                Deployment("connection-1", "route-alpha", GeometryDeploymentStatus.LocalOnly),
                Deployment("connection-1", "poi-bravo", GeometryDeploymentStatus.LocalOnly),
                Deployment("connection-1", "route-remote", GeometryDeploymentStatus.RemoteOnly),
                Deployment("connection-1", "poi-remote", GeometryDeploymentStatus.RemoteOnly)
            ]
        };
        var viewModel = Create(workspace, Connections());

        viewModel.SelectedKindFilter = "PointOfInterest";
        Assert.Single(viewModel.LocalItems);
        Assert.Single(viewModel.RemoteItems);

        viewModel.SearchText = "bravo";
        Assert.Equal("poi-bravo", Assert.Single(viewModel.LocalItems).GeometryId);
        Assert.Empty(viewModel.RemoteItems);
    }


    [Fact]
    public void Authoring_AddsPointOfInterestFromExactCoordinates()
    {
        var workspace = new FakeWorkspace
        {
            LocalDocuments =
            [
                GeometryDocument.Create(
                    "poi-draft",
                    "PoI draft",
                    GeometryDocumentKind.PointOfInterest)
            ]
        };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);
        viewModel.VertexLongitudeText = "-79.3832";
        viewModel.VertexLatitudeText = "43.6532";
        viewModel.VertexAltitudeText = "55";

        viewModel.AddVertexCommand.Execute(null);

        var point = Assert.Single(viewModel.AuthoringDocument!.Points);
        Assert.Equal(-79.3832, point.LongitudeDegrees, 6);
        Assert.Equal(43.6532, point.LatitudeDegrees, 6);
        Assert.Equal(55d, point.AltitudeMetres);
        Assert.True(viewModel.AuthoringDirty);
    }

    [Fact]
    public void Authoring_PolicyAltitudeRemainsZoneMetadataRatherThanVolumeKind()
    {
        var workspace = new FakeWorkspace
        {
            LocalDocuments = [Prepared(Zone("zone-alpha", "Zone Alpha"))]
        };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);
        viewModel.SelectedPolicyPreset = "Exclusion";
        viewModel.SelectedPolicyDecision = "Deny";
        viewModel.PolicyMinimumAltitudeText = "20";
        viewModel.PolicyMaximumAltitudeText = "120";
        viewModel.PolicyOperationsText = "transit, land";
        viewModel.PolicyTagsText = "field, safety";

        var preview = Assert.IsType<GeometryDocument>(viewModel.AuthoringPreview);

        Assert.Equal(GeometryDocumentKind.Zone, preview.Kind);
        Assert.Equal("safety", preview.Policy.Kind);
        Assert.Equal("exclusion", preview.Policy.Constraint);
        Assert.Equal("deny", preview.Policy.Decision);
        Assert.Equal(20d, preview.Policy.MinimumAltitudeMetres!.Value);
        Assert.Equal(120d, preview.Policy.MaximumAltitudeMetres!.Value);
        Assert.Equal(["transit", "land"], preview.Policy.Operations);
        Assert.Equal(["field", "safety"], preview.Policy.Tags);
    }

    [Fact]
    public void Authoring_ReordersWaypointSequence()
    {
        var workspace = new FakeWorkspace
        {
            LocalDocuments = [Prepared(Route("route-alpha", "Route Alpha"))]
        };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);
        var originalFirst = viewModel.AuthoringDocument!.Points[0];
        var originalSecond = viewModel.AuthoringDocument.Points[1];
        viewModel.SelectedVertex = viewModel.Vertices[1];

        viewModel.MoveVertexUpCommand.Execute(null);

        Assert.Equal(originalSecond, viewModel.AuthoringDocument.Points[0]);
        Assert.Equal(originalFirst, viewModel.AuthoringDocument.Points[1]);
        Assert.Equal(0, viewModel.SelectedVertex!.Index);
    }


    [Fact]
    public void ActiveMapDraft_PreventsSwitchingToDifferentLocalGeometry()
    {
        var editSession = new GeometryEditSession();
        var workspace = new FakeWorkspace
        {
            LocalDocuments =
            [
                Prepared(Route("route-alpha", "Route Alpha")),
                Prepared(Route("route-bravo", "Route Bravo"))
            ]
        };
        var viewModel = Create(workspace, Connections(), editSession);
        viewModel.SelectedLocal = viewModel.LocalItems.Single(item => item.GeometryId == "route-alpha");
        viewModel.EditOnMapCommand.Execute(null);

        viewModel.SelectedLocal = viewModel.LocalItems.Single(item => item.GeometryId == "route-bravo");

        Assert.Equal("route-alpha", viewModel.SelectedLocal!.GeometryId);
        Assert.Equal("route-alpha", viewModel.AuthoringGeometryId);
        Assert.Equal("route-alpha", editSession.Snapshot.Draft!.GeometryId);
    }

    [Fact]
    public void ZoneWithHoles_RemainsReadOnlyInAuthoringWorkspace()
    {
        var zone = Zone("zone-holes", "Zone with holes") with
        {
            Rings =
            [
                Zone("outer", "Outer").Rings[0],
                new GeometryDocumentRing
                {
                    Points =
                    [
                        GeometryDocumentPoint.GlobalWgs84(-79.385, 43.645),
                        GeometryDocumentPoint.GlobalWgs84(-79.380, 43.645),
                        GeometryDocumentPoint.GlobalWgs84(-79.380, 43.650),
                        GeometryDocumentPoint.GlobalWgs84(-79.385, 43.645)
                    ]
                }
            ]
        };
        var workspace = new FakeWorkspace { LocalDocuments = [Prepared(zone)] };
        var viewModel = Create(workspace, Connections());
        viewModel.SelectedLocal = Assert.Single(viewModel.LocalItems);

        Assert.False(viewModel.AuthoringEditable);
        Assert.False(viewModel.EditOnMapCommand.CanExecute(null));
        Assert.False(viewModel.SaveLocalEditsCommand.CanExecute(null));
        Assert.Contains("holes", viewModel.AuthoringReadOnlyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegistryWatchState_IsProjectedForSelectedConnection()
    {
        var workspace = new FakeWorkspace
        {
            RegistryWatchStates =
            [
                new GeometryRegistryWatchState(
                    "connection-1",
                    GeometryRegistryWatchStatusKind.BackingOff,
                    "Retrying the geometry stream.",
                    DateTimeOffset.UtcNow.AddSeconds(-2),
                    2,
                    "Temporary transport failure",
                    DateTimeOffset.UtcNow)
            ],
            RegistrySnapshots =
            [
                new GeometryRegistrySnapshot(
                    "connection-1",
                    GeometryRegistryState.Degraded,
                    "Degraded",
                    "NotReady",
                    "GEOMETRY_DEGRADED",
                    "Registry cache is stale.",
                    3,
                    "signature",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    [])
            ]
        };

        var viewModel = Create(workspace, Connections());

        Assert.Contains("BackingOff", viewModel.RegistryWatchSummary, StringComparison.Ordinal);
        Assert.Contains("2 restart", viewModel.RegistryWatchDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Degraded", viewModel.RegistrySummary, StringComparison.Ordinal);
    }

    private static GeometryWorkspaceViewModel Create(
        IGeometryWorkspaceService workspace,
        IEntityStore<string, ConnectionRecord> connections,
        IGeometryEditSession? editSession = null)
    {
        var codec = new GeometryDocumentCodec();
        return new GeometryWorkspaceViewModel(
            workspace,
            connections,
            new GeometryDocumentValidator(codec),
            editSession ?? new GeometryEditSession(),
            new ImmediateDispatcher());
    }

    private static IEntityStore<string, ConnectionRecord> Connections()
    {
        var store = new EntityStore<string, ConnectionRecord>(item => item.Id, StringComparer.Ordinal);
        store.Upsert(new ConnectionRecord(
            "connection-1",
            "Dracula",
            "http://localhost:50051",
            ConnectionMode.Direct,
            AvailabilityState.Online,
            true));
        store.Upsert(new ConnectionRecord(
            "connection-2",
            "Lucifer",
            "http://localhost:50052",
            ConnectionMode.Direct,
            AvailabilityState.Online,
            true));
        return store;
    }

    private static GeometryDocument Route(string id, string name)
        => GeometryDocument.Create(id, name, GeometryDocumentKind.WaypointSequence) with
        {
            Points =
            [
                GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65),
                GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66)
            ]
        };

    private static GeometryDocument Point(string id, string name)
        => GeometryDocument.Create(id, name, GeometryDocumentKind.PointOfInterest) with
        {
            Points = [GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65)]
        };

    private static GeometryDocument Zone(string id, string name)
        => GeometryDocument.Create(id, name, GeometryDocumentKind.Zone) with
        {
            Rings =
            [
                new GeometryDocumentRing
                {
                    Points =
                    [
                        GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.37, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66),
                        GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64)
                    ]
                }
            ]
        };

    private static GeometryDocument Prepared(GeometryDocument document)
        => new GeometryDocumentCodec().PrepareForSave(document);

    private static RemoteGeometryRecord Remote(
        string connectionId,
        string id,
        GeometryDocumentKind kind = GeometryDocumentKind.WaypointSequence)
        => new(
            connectionId,
            id,
            kind,
            id,
            GeometryCoordinateFrame.GlobalWgs84,
            kind == GeometryDocumentKind.Zone,
            kind == GeometryDocumentKind.PointOfInterest ? 1 : 2,
            kind == GeometryDocumentKind.Zone ? 1 : 0,
            100,
            $"sha-{id}",
            "revision-1",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());

    private static GeometryDeploymentRecord Deployment(
        string connectionId,
        string id,
        GeometryDeploymentStatus status)
        => new(
            id,
            connectionId,
            status,
            null,
            null,
            null,
            null,
            status.ToString());

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspace : IGeometryWorkspaceService
    {
        public event EventHandler? Changed;

        public bool GatewayAvailable => true;

        public string GatewayStatus => "Geometry available";

        public string LocalLibraryPath => "geometry";

        public IReadOnlyList<GeometryDocument> LocalDocuments { get; set; } = [];

        public IReadOnlyList<GeometryLibraryIssue> LibraryIssues { get; set; } = [];

        public IReadOnlyList<RemoteGeometryRecord> RemoteRecords { get; set; } = [];

        public IReadOnlyList<GeometryRegistrySnapshot> RegistrySnapshots { get; set; } = [];

        public IReadOnlyList<GeometryRegistryWatchState> RegistryWatchStates { get; set; } = [];

        public IReadOnlyList<GeometryDeploymentRecord> Deployments { get; set; } = [];

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task RefreshAsync(string? connectionId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GeometryDocument> CreateLocalDraftAsync(
            GeometryDocumentKind kind,
            string? geometryId = null,
            string? displayName = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryDocument> ImportLocalAsync(
            string path,
            bool allowReplace = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ExportLocalAsync(
            string geometryId,
            string path,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryDocument> SaveLocalAsync(
            GeometryDocument document,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryDocument> DuplicateLocalAsync(
            string geometryId,
            string newGeometryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryDocument> PullAsync(
            string connectionId,
            string geometryId,
            bool replaceLocal = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryDocument> PullAsLocalCopyAsync(
            string connectionId,
            string geometryId,
            string newGeometryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryOperationAssessment> AssessRemoteOperationAsync(
            string connectionId,
            string geometryId,
            GeometryRemoteOperationKind operation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeometryOperationAssessment(
                connectionId,
                geometryId,
                operation,
                GeometryOperationAssessmentState.Ready,
                "Ready",
                null,
                null,
                Deployments.FirstOrDefault(item => item.GeometryId == geometryId && item.ConnectionId == connectionId),
                "revision-1",
                [],
                DateTimeOffset.UtcNow));

        public Task<GeometryCommandResult> CreateRemoteAsync(
            string connectionId,
            string geometryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> UpdateRemoteAsync(
            string connectionId,
            string geometryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GeometryCommandResult> DeleteRemoteAsync(
            string connectionId,
            string geometryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveLocalAsync(
            string geometryId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
