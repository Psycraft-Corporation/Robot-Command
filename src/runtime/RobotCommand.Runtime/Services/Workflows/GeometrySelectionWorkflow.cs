using RobotCommand.Core;

namespace RobotCommand.Services.Workflows;

/// <summary>
/// In-memory selection state for local geometry map interaction. This is
/// intentionally independent from persisted geometry documents and from unit
/// selection, allowing each front end to clear the other explicitly when its
/// interaction mode changes.
/// </summary>
public sealed class GeometrySelectionWorkflow : IGeometrySelectionWorkflow
{
    private GeometrySelectionWorkflowSnapshot _current = GeometrySelectionWorkflowSnapshot.Empty;

    public event EventHandler? Changed;

    public GeometrySelectionWorkflowSnapshot Current => _current;

    public void Set(IReadOnlyList<string> geometryIds, string? anchorGeometryId = null)
    {
        ArgumentNullException.ThrowIfNull(geometryIds);
        var ids = geometryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var anchor = !string.IsNullOrWhiteSpace(anchorGeometryId) &&
                     ids.Contains(anchorGeometryId.Trim(), StringComparer.Ordinal)
            ? anchorGeometryId.Trim()
            : ids.FirstOrDefault();
        var next = new GeometrySelectionWorkflowSnapshot(ids, anchor);
        if (_current.GeometryIds.SequenceEqual(next.GeometryIds, StringComparer.Ordinal) &&
            string.Equals(_current.AnchorGeometryId, next.AnchorGeometryId, StringComparison.Ordinal))
        {
            return;
        }

        _current = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => Set([]);
}
