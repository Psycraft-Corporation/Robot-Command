using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class MapLibraryView : UserControl
{
    public MapLibraryView()
    {
        InitializeComponent();
    }

    private async void InstallMapClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MapLibraryViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install map",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Map package archive") { Patterns = ["*.zip"] }]
        });
        if (files.Count == 0)
            return;

        viewModel.ImportPath = files[0].Path.LocalPath;
        viewModel.ImportCommand.Execute(null);
    }

    private async void InstallFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MapLibraryViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Install map folder",
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;

        viewModel.ImportPath = folders[0].Path.LocalPath;
        viewModel.ImportCommand.Execute(null);
    }
}
