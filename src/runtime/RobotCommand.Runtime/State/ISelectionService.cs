using RobotCommand.Models;

namespace RobotCommand.State;

public interface ISelectionService
{
    OperationalSelection Current { get; }

    IReadOnlyList<string> SelectedUnitIds { get; }

    string? UnitSelectionAnchorId { get; }

    event EventHandler? Changed;

    void Select(OperationalSelection selection);

    void SetUnitSelection(IReadOnlyList<OperationalSelection> selections, string? anchorId = null);

    void Clear();
}
