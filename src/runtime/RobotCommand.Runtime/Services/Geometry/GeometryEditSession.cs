using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public sealed class GeometryEditSession : IGeometryEditSession
{
    private const int MaximumHistory = 100;
    private readonly object _gate = new();
    private readonly Stack<EditFrame> _undo = new();
    private readonly Stack<EditFrame> _redo = new();
    private GeometryEditSnapshot _snapshot = GeometryEditSnapshot.Empty;
    private int? _coalescedMoveVertexIndex;

    public event EventHandler? Changed;

    public GeometryEditSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void BeginCreate(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        GeometryEditorState.EnsureAuthorable(document);
        EnsureKind(document.Kind);
        Begin(
            document,
            document.Kind switch
            {
                GeometryDocumentKind.PointOfInterest => MapInteractionMode.CreatePoint,
                GeometryDocumentKind.WaypointSequence => MapInteractionMode.CreatePath,
                GeometryDocumentKind.Zone => MapInteractionMode.CreateZone,
                _ => throw new ArgumentOutOfRangeException(nameof(document), document.Kind, null)
            },
            document.Kind switch
            {
                GeometryDocumentKind.PointOfInterest => GeometryEditorMode.CreatePointOfInterest,
                GeometryDocumentKind.WaypointSequence => GeometryEditorMode.CreateWaypointSequence,
                GeometryDocumentKind.Zone => GeometryEditorMode.CreateZone,
                _ => throw new ArgumentOutOfRangeException(nameof(document), document.Kind, null)
            });
    }

    public void BeginEdit(GeometryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        GeometryEditorState.EnsureAuthorable(document);
        EnsureKind(document.Kind);
        Begin(document, MapInteractionMode.EditGeometry, GeometryEditorMode.EditExisting);
    }

    public void AddVertex(GeometryDocumentPoint point)
    {
        ValidatePoint(point);
        Mutate((vertices, current) =>
        {
            if (current.Kind == GeometryDocumentKind.PointOfInterest && vertices.Count >= 1)
            {
                throw new InvalidOperationException("A point of interest can contain only one point.");
            }

            vertices.Add(point);
            return vertices.Count - 1;
        });
    }

    public void InsertVertex(int index, GeometryDocumentPoint point)
    {
        ValidatePoint(point);
        Mutate((vertices, current) =>
        {
            if (current.Kind == GeometryDocumentKind.PointOfInterest)
            {
                throw new InvalidOperationException("A point of interest does not support inserted vertices.");
            }

            if (index < 0 || index > vertices.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            vertices.Insert(index, point);
            return index;
        });
    }

    public void MoveVertex(int index, GeometryDocumentPoint point)
    {
        ValidatePoint(point);
        Mutate((vertices, _) =>
        {
            RequireVertex(index, vertices.Count);
            vertices[index] = point;
            return index;
        }, coalescedMoveVertexIndex: index);
    }

    public void ReorderVertex(int fromIndex, int toIndex)
    {
        Mutate((vertices, current) =>
        {
            if (current.Kind == GeometryDocumentKind.PointOfInterest)
            {
                throw new InvalidOperationException("A point of interest does not support vertex reordering.");
            }

            RequireVertex(fromIndex, vertices.Count);
            RequireVertex(toIndex, vertices.Count);
            if (fromIndex == toIndex)
            {
                return fromIndex;
            }

            var point = vertices[fromIndex];
            vertices.RemoveAt(fromIndex);
            vertices.Insert(toIndex, point);
            return toIndex;
        });
    }

    public void Rename(string displayName)
    {
        var normalizedName = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("A geometry name is required.", nameof(displayName));
        }

        EventHandler? changed;
        lock (_gate)
        {
            RequireEditing();
            var current = _snapshot.Draft!;
            if (string.Equals(current.DisplayName, normalizedName, StringComparison.Ordinal))
            {
                return;
            }

            PushBounded(_undo, CurrentFrame());
            _redo.Clear();
            _coalescedMoveVertexIndex = null;
            var draft = current with { DisplayName = normalizedName };
            _snapshot = BuildSnapshot(
                GeometryEditSessionStage.Editing,
                _snapshot.InteractionMode,
                _snapshot.Editor with { Draft = draft, Validation = null },
                _snapshot.SelectedVertexIndex,
                StatusFor(draft, _snapshot.SelectedVertexIndex));
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    public void SelectVertex(int? index)
    {
        EventHandler? changed;
        lock (_gate)
        {
            RequireEditing();
            var count = GeometryEditShape.GetVertices(_snapshot.Draft).Count;
            if (index is not null)
            {
                RequireVertex(index.Value, count);
            }

            _coalescedMoveVertexIndex = null;
            _snapshot = BuildSnapshot(
                _snapshot.Stage,
                _snapshot.InteractionMode,
                _snapshot.Editor,
                index,
                StatusFor(_snapshot.Editor.Draft, index));
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveVertex(int? index = null)
    {
        Mutate((vertices, _) =>
        {
            var resolved = index ?? _snapshot.SelectedVertexIndex
                ?? throw new InvalidOperationException("Select a vertex before removing it.");
            RequireVertex(resolved, vertices.Count);
            vertices.RemoveAt(resolved);
            return vertices.Count == 0 ? null : Math.Min(resolved, vertices.Count - 1);
        });
    }

    public void Undo()
    {
        EventHandler? changed;
        lock (_gate)
        {
            RequireEditing();
            if (_undo.Count == 0)
            {
                return;
            }

            PushBounded(_redo, CurrentFrame());
            _coalescedMoveVertexIndex = null;
            var frame = _undo.Pop();
            _snapshot = BuildSnapshot(
                GeometryEditSessionStage.Editing,
                _snapshot.InteractionMode,
                _snapshot.Editor with { Draft = frame.Document },
                frame.SelectedVertexIndex,
                "Undid the last geometry edit.");
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        EventHandler? changed;
        lock (_gate)
        {
            RequireEditing();
            if (_redo.Count == 0)
            {
                return;
            }

            PushBounded(_undo, CurrentFrame());
            _coalescedMoveVertexIndex = null;
            var frame = _redo.Pop();
            _snapshot = BuildSnapshot(
                GeometryEditSessionStage.Editing,
                _snapshot.InteractionMode,
                _snapshot.Editor with { Draft = frame.Document },
                frame.SelectedVertexIndex,
                "Redid the geometry edit.");
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    public GeometryDocument Complete()
    {
        GeometryDocument completed;
        EventHandler? changed;
        lock (_gate)
        {
            RequireEditing();
            if (!GeometryEditShape.CanComplete(_snapshot.Draft))
            {
                throw new InvalidOperationException(CompletionRequirement(_snapshot.Draft));
            }

            completed = GeometryEditShape.NormalizeCompleted(_snapshot.Draft!);
            _coalescedMoveVertexIndex = null;
            var editor = _snapshot.Editor with { Draft = completed };
            _snapshot = BuildSnapshot(
                GeometryEditSessionStage.Completed,
                MapInteractionMode.Select,
                editor,
                null,
                "Geometry editing is complete.");
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
        return completed;
    }

    public GeometryDocument? Cancel()
    {
        GeometryDocument? baseline;
        EventHandler? changed;
        lock (_gate)
        {
            baseline = _snapshot.Editor.Baseline;
            ResetLocked();
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
        return baseline;
    }

    public void Clear()
    {
        EventHandler? changed;
        lock (_gate)
        {
            if (_snapshot.Stage == GeometryEditSessionStage.Idle)
            {
                return;
            }

            ResetLocked();
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    private void Begin(
        GeometryDocument document,
        MapInteractionMode interactionMode,
        GeometryEditorMode editorMode)
    {
        EventHandler? changed;
        lock (_gate)
        {
            if (_snapshot.Stage != GeometryEditSessionStage.Idle)
            {
                throw new InvalidOperationException("Save, discard, or cancel the current geometry draft first.");
            }

            _undo.Clear();
            _redo.Clear();
            _coalescedMoveVertexIndex = null;
            var baseline = document with { IsDirty = false };
            var draft = document with { IsDirty = false };
            var editor = new GeometryEditorState(editorMode, baseline, draft, null);
            var vertices = GeometryEditShape.GetVertices(draft);
            int? selected = vertices.Count == 0 ? null : vertices.Count - 1;
            _snapshot = BuildSnapshot(
                GeometryEditSessionStage.Editing,
                interactionMode,
                editor,
                selected,
                StatusFor(draft, selected));
            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    private void Mutate(
        Func<List<GeometryDocumentPoint>, GeometryDocument, int?> mutation,
        int? coalescedMoveVertexIndex = null)
    {
        EventHandler? changed;
        lock (_gate)
        {
            RequireEditing();
            var current = _snapshot.Draft!;
            var vertices = GeometryEditShape.GetVertices(current).ToList();
            var recordHistory = coalescedMoveVertexIndex is null ||
                                _coalescedMoveVertexIndex != coalescedMoveVertexIndex;
            if (recordHistory)
            {
                PushBounded(_undo, CurrentFrame());
            }

            try
            {
                var selected = mutation(vertices, current);
                _redo.Clear();
                _coalescedMoveVertexIndex = coalescedMoveVertexIndex;
                var draft = GeometryEditShape.WithVertices(current, vertices);
                _snapshot = BuildSnapshot(
                    GeometryEditSessionStage.Editing,
                    _snapshot.InteractionMode,
                    _snapshot.Editor with { Draft = draft, Validation = null },
                    selected,
                    StatusFor(draft, selected));
            }
            catch
            {
                if (recordHistory)
                {
                    _undo.Pop();
                }
                throw;
            }

            changed = Changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    private GeometryEditSnapshot BuildSnapshot(
        GeometryEditSessionStage stage,
        MapInteractionMode mode,
        GeometryEditorState editor,
        int? selectedVertexIndex,
        string status)
        => new(
            stage,
            mode,
            editor,
            selectedVertexIndex,
            stage == GeometryEditSessionStage.Editing && _undo.Count > 0,
            stage == GeometryEditSessionStage.Editing && _redo.Count > 0,
            stage == GeometryEditSessionStage.Editing && GeometryEditShape.CanComplete(editor.Draft),
            status);

    private EditFrame CurrentFrame()
        => new(_snapshot.Draft!, _snapshot.SelectedVertexIndex);

    private static void PushBounded(Stack<EditFrame> stack, EditFrame frame)
    {
        if (stack.Count >= MaximumHistory)
        {
            var retained = stack.Reverse().Skip(1).ToArray();
            stack.Clear();
            foreach (var item in retained)
            {
                stack.Push(item);
            }
        }

        stack.Push(frame);
    }

    private void RequireEditing()
    {
        if (_snapshot.Stage != GeometryEditSessionStage.Editing || _snapshot.Draft is null)
        {
            throw new InvalidOperationException("No active geometry edit is available.");
        }
    }

    private void ResetLocked()
    {
        _undo.Clear();
        _redo.Clear();
        _coalescedMoveVertexIndex = null;
        _snapshot = GeometryEditSnapshot.Empty;
    }

    private static void ValidatePoint(GeometryDocumentPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Geometry coordinates must be finite.");
        }

        if (point.X is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Longitude must be between -180 and 180 degrees.");
        }

        if (point.Y is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Latitude must be between -90 and 90 degrees.");
        }
    }

    private static void RequireVertex(int index, int count)
    {
        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private static void EnsureKind(GeometryDocumentKind kind)
    {
        if (kind is not GeometryDocumentKind.PointOfInterest and
            not GeometryDocumentKind.WaypointSequence and
            not GeometryDocumentKind.Zone)
        {
            throw new NotSupportedException($"Geometry kind '{kind}' cannot be edited on the map.");
        }
    }

    private static string StatusFor(GeometryDocument? document, int? selected)
    {
        if (document is null)
        {
            return "No geometry draft is available.";
        }

        var count = GeometryEditShape.GetVertices(document).Count;
        var selectedText = selected is null ? string.Empty : $" Vertex {selected.Value + 1} is selected.";
        return document.Kind switch
        {
            GeometryDocumentKind.PointOfInterest => count == 0
                ? "Click the map to place the point of interest."
                : $"Point of interest placed.{selectedText}",
            GeometryDocumentKind.WaypointSequence =>
                $"{count} waypoint(s). Click the map to append a point or drag a handle to move it.{selectedText}",
            GeometryDocumentKind.Zone =>
                $"{count} zone vertex/vertices. Click the map to append a point or drag a handle to move it.{selectedText}",
            _ => "Geometry edit active."
        };
    }

    private static string CompletionRequirement(GeometryDocument? document)
        => document?.Kind switch
        {
            GeometryDocumentKind.PointOfInterest => "Place exactly one point before completing the edit.",
            GeometryDocumentKind.WaypointSequence => "Place at least two waypoints before completing the edit.",
            GeometryDocumentKind.Zone => "Place at least three non-intersecting zone vertices with non-zero area before completing the edit.",
            _ => "The geometry draft cannot be completed."
        };

    private sealed record EditFrame(GeometryDocument Document, int? SelectedVertexIndex);
}
