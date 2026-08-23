using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RobotCommand.ViewModels;
namespace RobotCommand.Views.Shell;

public sealed partial class UnitsPanelView : UserControl
{
    private UnitListItemViewModel? _draggedItem;
    private IReadOnlyList<UnitListItemViewModel> _selectedItemsBeforeDrag = [];
    private UnitsPanelViewModel? _viewModel;
    private TopLevel? _topLevel;

    public static readonly StyledProperty<bool> ShowHeaderProperty =
        AvaloniaProperty.Register<UnitsPanelView, bool>(nameof(ShowHeader), true);

    public bool ShowHeader
    {
        get => GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    public UnitsPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        // Intercept the handle before ListBox processes the press as a new
        // selection. This keeps the selected-row highlight stable while it is
        // being dragged.
        if (UnitList is { } unitList)
        {
            unitList.AddHandler(InputElement.PointerPressedEvent, OnUnitPointerPressed, RoutingStrategies.Tunnel, true);
            unitList.AddHandler(InputElement.PointerMovedEvent, OnUnitPointerMoved, RoutingStrategies.Bubble, true);
            unitList.AddHandler(InputElement.PointerReleasedEvent, OnUnitPointerReleased, RoutingStrategies.Bubble, true);
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, true);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
    }

    private async void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || !IsEffectivelyVisible || e.Key != Key.T)
            return;

        var textInput = (e.Source as Visual)?.FindAncestorOfType<TextBox>() is not null;
        if (textInput || e.KeyModifiers.HasFlag(KeyModifiers.Control) == e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            await _viewModel.CreateTeamFromSelectionAsync();
        else
            await _viewModel.ClearSelectedTeamMembershipAsync();

        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.UnitSelectionChanged -= OnViewModelSelectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (DataContext is UnitsPanelViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.UnitSelectionChanged += OnViewModelSelectionChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncListSelection(viewModel);
        }
        else
        {
            _viewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UnitsPanelViewModel.IsCreatingGhost) ||
            sender is not UnitsPanelViewModel { IsCreatingGhost: true })
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => GhostCreationPanel?.BringIntoView(),
            DispatcherPriority.Background);
    }

    private void OnViewModelSelectionChanged(object? sender, EventArgs e)
        => SyncListSelection(sender as UnitsPanelViewModel ?? DataContext as UnitsPanelViewModel);

    private void SyncListSelection(UnitsPanelViewModel? viewModel)
    {
        if (viewModel is null || UnitList is not { } unitList || unitList.SelectedItems is not { } selectedItems)
            return;
        selectedItems.Clear();
        foreach (var item in viewModel.Units.Where(item =>
                     viewModel.SelectedUnitIds.Contains(item.Record.VehicleId ?? item.Record.Id, StringComparer.Ordinal)))
            selectedItems.Add(item);
    }

    private void OnUnitPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(UnitList).Properties.IsLeftButtonPressed)
            return;

        var source = e.Source as Visual;
        var handle = source as Button ?? source?.FindAncestorOfType<Button>();
        var item = source?.FindAncestorOfType<ListBoxItem>();
        var unit = item?.DataContext as UnitListItemViewModel;
        if (handle?.Classes.Contains("unitDragHandle") == true)
        {
            _draggedItem = unit;
        }
        else if (unit is not null && handle is null && DataContext is UnitsPanelViewModel viewModel)
        {
            viewModel.SelectUnit(
                unit,
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            SyncListSelection(viewModel);
            e.Handled = true;
            return;
        }

        if (_draggedItem is not null && UnitList is { } unitList && unitList.SelectedItems is { } selectedItems)
        {
            _selectedItemsBeforeDrag = selectedItems
                .OfType<UnitListItemViewModel>()
                .ToArray();
            e.Pointer.Capture(unitList);
            e.Handled = true;
        }
    }

    private void OnUnitPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedItem is null ||
            !e.GetCurrentPoint(UnitList).Properties.IsLeftButtonPressed)
            return;

        e.Pointer.Capture(UnitList);
        var point = e.GetPosition(UnitList);
        var targetIndex = GetDropIndex(point.Y);
        if (DataContext is UnitsPanelViewModel viewModel)
            viewModel.ReorderUnit(_draggedItem, targetIndex);
        RestoreSelection();
        e.Handled = true;
    }

    private void OnUnitPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedItem is not null)
        {
            e.Pointer.Capture(null);
            RestoreSelection();
            Dispatcher.UIThread.Post(RestoreSelection, DispatcherPriority.Render);
            e.Handled = true;
        }
        _draggedItem = null;
        _selectedItemsBeforeDrag = [];
    }

    private void RestoreSelection()
    {
        if (_selectedItemsBeforeDrag.Count == 0 ||
            UnitList is not { } unitList ||
            unitList.SelectedItems is not { } selectedItems)
            return;

        selectedItems.Clear();
        foreach (var item in _selectedItemsBeforeDrag.Where(item => unitList.Items.Contains(item)))
            selectedItems.Add(item);
    }

    private int GetDropIndex(double y)
    {
        var containers = UnitList.GetRealizedContainers()
            .OfType<ListBoxItem>()
            .Select(item => new
            {
                Item = item,
                Top = item.TranslatePoint(new Point(0, 0), UnitList)?.Y ?? item.Bounds.Top
            })
            .OrderBy(item => item.Top)
            .ToArray();
        for (var index = 0; index < containers.Length; index++)
        {
            if (y < containers[index].Top + (containers[index].Item.Bounds.Height / 2))
                return index;
        }

        return containers.Length;
    }
}
