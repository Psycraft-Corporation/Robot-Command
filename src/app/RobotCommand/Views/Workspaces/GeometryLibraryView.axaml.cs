using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class GeometryLibraryView : UserControl
{
    public GeometryLibraryView() => InitializeComponent();

    private async void ImportFence_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GeometryLibraryViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import fence",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Robot Command fence") { Patterns = ["*.json"] }]
        });
        if (files.Count > 0) await viewModel.ImportFenceAsync(files[0].Path.LocalPath);
    }

    private async void ExportFence_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GeometryLibraryViewModel viewModel || viewModel.SelectedFence is null || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export fence",
            SuggestedFileName = "fence.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Robot Command fence") { Patterns = ["*.json"] }]
        });
        if (file is not null) await viewModel.ExportFenceAsync(file.Path.LocalPath);
    }
}
