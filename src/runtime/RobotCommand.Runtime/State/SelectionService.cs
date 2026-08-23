using RobotCommand.Models;

namespace RobotCommand.State;

public sealed class SelectionService : ISelectionService
{
    public OperationalSelection Current { get; private set; } = OperationalSelection.None;
    public IReadOnlyList<string> SelectedUnitIds { get; private set; } = [];
    public string? UnitSelectionAnchorId { get; private set; }

    public event EventHandler? Changed;

    public void Select(OperationalSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Kind == SelectionKind.Vehicle && selection.Id is not null)
        {
            SetUnitSelection([selection], selection.Id);
            return;
        }

        SelectedUnitIds = [];
        UnitSelectionAnchorId = null;

        if (Current == selection)
        {
            return;
        }

        Current = selection;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetUnitSelection(IReadOnlyList<OperationalSelection> selections, string? anchorId = null)
    {
        var normalized = selections
            .Where(item => item.Kind == SelectionKind.Vehicle && !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id!, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var nextCurrent = normalized.FirstOrDefault() ?? OperationalSelection.None;
        var nextIds = normalized.Select(item => item.Id!).ToArray();
        var nextAnchor = nextIds.Length == 0
            ? null
            : anchorId is not null && nextIds.Contains(anchorId, StringComparer.Ordinal)
                ? anchorId
                : nextIds[0];
        if (Current == nextCurrent && SelectedUnitIds.SequenceEqual(nextIds, StringComparer.Ordinal) &&
            string.Equals(UnitSelectionAnchorId, nextAnchor, StringComparison.Ordinal))
            return;

        Current = nextCurrent;
        SelectedUnitIds = nextIds;
        UnitSelectionAnchorId = nextAnchor;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        SelectedUnitIds = [];
        UnitSelectionAnchorId = null;
        Select(OperationalSelection.None);
    }
}
