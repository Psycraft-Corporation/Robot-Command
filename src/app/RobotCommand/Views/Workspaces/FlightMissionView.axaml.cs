using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public partial class FlightMissionView : UserControl
{
    public FlightMissionView()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, MissionEditor_KeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void MissionEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is not TextBox and not NumericUpDown) return;

        // Mission editor bindings commit on LostFocus. Enter uses normal keyboard
        // navigation so the edited value is committed without waiting for a click.
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        if (focusManager?.TryMoveFocus(NavigationDirection.Next) != true)
            Focus();
        e.Handled = true;
    }

    private async void ImportMission_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FlightMissionViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Robot Command mission",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Robot Command mission") { Patterns = ["*.json"] }]
        });
        if (files.Count > 0) await viewModel.ImportAsync(files[0].Path.LocalPath);
    }

    private async void ExportMission_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FlightMissionViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Robot Command mission",
            SuggestedFileName = "flight-mission.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Robot Command mission") { Patterns = ["*.json"] }]
        });
        if (file is not null) await viewModel.ExportAsync(file.Path.LocalPath);
    }
}
