using RobotCommand.Models;

namespace RobotCommand.Services.Geometry;

public interface IGeometryEditSession
{
    event EventHandler? Changed;

    GeometryEditSnapshot Snapshot { get; }

    void BeginCreate(GeometryDocument document);

    void BeginEdit(GeometryDocument document);

    void AddVertex(GeometryDocumentPoint point);

    void InsertVertex(int index, GeometryDocumentPoint point);

    void MoveVertex(int index, GeometryDocumentPoint point);

    void ReorderVertex(int fromIndex, int toIndex);

    void Rename(string displayName);

    void SelectVertex(int? index);

    void RemoveVertex(int? index = null);

    void Undo();

    void Redo();

    GeometryDocument Complete();

    GeometryDocument? Cancel();

    void Clear();
}
