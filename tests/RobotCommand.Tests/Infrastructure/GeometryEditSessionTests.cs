using RobotCommand.Models;
using RobotCommand.Services.Geometry;
using Xunit;

namespace RobotCommand.Tests;

public sealed class GeometryEditSessionTests
{
    [Fact]
    public void BeginCreate_ChoosesInteractionModeFromGeometryKind()
    {
        var session = new GeometryEditSession();
        var document = GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence);

        session.BeginCreate(document);

        Assert.Equal(GeometryEditSessionStage.Editing, session.Snapshot.Stage);
        Assert.Equal(MapInteractionMode.CreatePath, session.Snapshot.InteractionMode);
        Assert.Same(document.Points, session.Snapshot.Draft!.Points);
        Assert.False(session.Snapshot.CanComplete);
    }

    [Fact]
    public void AddMoveUndoRedo_ProducesStableDraftHistory()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence));
        var first = GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65, 10);
        var second = GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66, 20);
        var moved = GeometryDocumentPoint.GlobalWgs84(-79.36, 43.67, 20);

        session.AddVertex(first);
        session.AddVertex(second);
        session.MoveVertex(1, moved);

        Assert.Equal([first, moved], session.Snapshot.Vertices);
        Assert.True(session.Snapshot.CanComplete);
        session.Undo();
        Assert.Equal([first, second], session.Snapshot.Vertices);
        Assert.True(session.Snapshot.CanRedo);
        session.Redo();
        Assert.Equal([first, moved], session.Snapshot.Vertices);
    }

    [Fact]
    public void Rename_UpdatesTheDraftAndCanBeUndone()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "poi-alpha",
            "Original",
            GeometryDocumentKind.PointOfInterest));

        session.Rename("Updated name");

        Assert.Equal("Updated name", session.Snapshot.Draft!.DisplayName);
        session.Undo();
        Assert.Equal("Original", session.Snapshot.Draft!.DisplayName);
    }


    [Fact]
    public void ConsecutiveDragMoves_CoalesceIntoOneUndoStep()
    {
        var original = GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65, 30);
        var session = new GeometryEditSession();
        session.BeginEdit(GeometryDocument.Create(
            "poi-alpha",
            "PoI Alpha",
            GeometryDocumentKind.PointOfInterest) with
        {
            Points = [original]
        });
        session.SelectVertex(0);

        session.MoveVertex(0, GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66, 30));
        session.MoveVertex(0, GeometryDocumentPoint.GlobalWgs84(-79.36, 43.67, 30));
        session.Undo();

        Assert.Equal(original, Assert.Single(session.Snapshot.Vertices));
        Assert.False(session.Snapshot.CanUndo);
    }

    [Fact]
    public void Cancel_DiscardsDraftAndReturnsUnchangedBaseline()
    {
        var original = GeometryDocument.Create(
            "poi-alpha",
            "PoI Alpha",
            GeometryDocumentKind.PointOfInterest) with
        {
            Points = [GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65)],
            IsDirty = false
        };
        var session = new GeometryEditSession();
        session.BeginEdit(original);
        session.MoveVertex(0, GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66));

        var restored = session.Cancel();

        Assert.Equal(original.Points, restored!.Points);
        Assert.Equal(GeometryEditSessionStage.Idle, session.Snapshot.Stage);
        Assert.Null(session.Snapshot.Draft);
    }

    [Fact]
    public void Complete_ClosesZoneRingAndLeavesInMemoryDraft()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "zone-alpha",
            "Zone Alpha",
            GeometryDocumentKind.Zone));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.37, 43.64));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66));

        var completed = session.Complete();

        Assert.Equal(GeometryEditSessionStage.Completed, session.Snapshot.Stage);
        Assert.True(session.Snapshot.HasDraft);
        var ring = Assert.Single(completed.Rings).Points;
        Assert.Equal(4, ring.Count);
        Assert.Equal(ring[0], ring[^1]);
        Assert.True(completed.IsDirty);
    }

    [Fact]
    public void Complete_RejectsIncompleteGeometry()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "zone-alpha",
            "Zone Alpha",
            GeometryDocumentKind.Zone));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.37, 43.64));

        var exception = Assert.Throws<InvalidOperationException>(() => session.Complete());

        Assert.Contains("three", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GeometryEditSessionStage.Editing, session.Snapshot.Stage);
    }

    [Fact]
    public void PointOfInterest_RejectsSecondPoint()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "poi-alpha",
            "PoI Alpha",
            GeometryDocumentKind.PointOfInterest));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65));

        Assert.Throws<InvalidOperationException>(() =>
            session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66)));
        Assert.Single(session.Snapshot.Vertices);
    }

    [Fact]
    public void InsertAndRemove_PreserveWaypointOrder()
    {
        var first = GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64);
        var second = GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66);
        var middle = GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65);
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence));
        session.AddVertex(first);
        session.AddVertex(second);

        session.InsertVertex(1, middle);
        Assert.Equal([first, middle, second], session.Snapshot.Vertices);
        session.RemoveVertex(1);
        Assert.Equal([first, second], session.Snapshot.Vertices);
    }


    [Fact]
    public void ReorderVertex_ChangesOrderInOneEdit()
    {
        var first = GeometryDocumentPoint.GlobalWgs84(-79.39, 43.64);
        var second = GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65);
        var third = GeometryDocumentPoint.GlobalWgs84(-79.37, 43.66);
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence));
        session.AddVertex(first);
        session.AddVertex(second);
        session.AddVertex(third);

        session.ReorderVertex(2, 0);

        Assert.Equal([third, first, second], session.Snapshot.Vertices);
        Assert.Equal(0, session.Snapshot.SelectedVertexIndex);
    }

    [Fact]
    public void Complete_RejectsSelfIntersectingZone()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "zone-crossed",
            "Crossed zone",
            GeometryDocumentKind.Zone));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.40, 43.64));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.36, 43.68));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.40, 43.68));
        session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.36, 43.64));

        Assert.False(session.Snapshot.CanComplete);
        Assert.Throws<InvalidOperationException>(() => session.Complete());
    }

    [Fact]
    public void BeginEdit_RejectsZoneWithHoles()
    {
        var zone = GeometryDocument.Create(
            "zone-with-hole",
            "Zone with hole",
            GeometryDocumentKind.Zone) with
        {
            Rings =
            [
                new GeometryDocumentRing
                {
                    Points =
                    [
                        GeometryDocumentPoint.GlobalWgs84(-79.40, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.36, 43.64),
                        GeometryDocumentPoint.GlobalWgs84(-79.36, 43.68),
                        GeometryDocumentPoint.GlobalWgs84(-79.40, 43.64)
                    ]
                },
                new GeometryDocumentRing
                {
                    Points =
                    [
                        GeometryDocumentPoint.GlobalWgs84(-79.39, 43.65),
                        GeometryDocumentPoint.GlobalWgs84(-79.38, 43.65),
                        GeometryDocumentPoint.GlobalWgs84(-79.38, 43.66),
                        GeometryDocumentPoint.GlobalWgs84(-79.39, 43.65)
                    ]
                }
            ]
        };
        var session = new GeometryEditSession();

        var exception = Assert.Throws<NotSupportedException>(() => session.BeginEdit(zone));

        Assert.Contains("holes", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GeometryEditSessionStage.Idle, session.Snapshot.Stage);
    }

    [Fact]
    public void BeginEdit_RejectsConnectionScopedFrame()
    {
        var session = new GeometryEditSession();
        var document = GeometryDocument.Create(
            "route-local",
            "Local route",
            GeometryDocumentKind.WaypointSequence) with
        {
            Frame = GeometryCoordinateFrame.LocalNed
        };

        Assert.Throws<NotSupportedException>(() => session.BeginEdit(document));
        Assert.Equal(GeometryEditSessionStage.Idle, session.Snapshot.Stage);
    }

    [Fact]
    public void Mutations_RejectCoordinatesOutsideWgs84Bounds()
    {
        var session = new GeometryEditSession();
        session.BeginCreate(GeometryDocument.Create(
            "route-alpha",
            "Route Alpha",
            GeometryDocumentKind.WaypointSequence));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.AddVertex(GeometryDocumentPoint.GlobalWgs84(181, 43.65)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.AddVertex(GeometryDocumentPoint.GlobalWgs84(-79.38, 91)));
        Assert.Empty(session.Snapshot.Vertices);
    }
}
