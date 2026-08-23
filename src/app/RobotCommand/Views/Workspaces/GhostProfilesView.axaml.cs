using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public partial class GhostProfilesView : UserControl
{
    public GhostProfilesView() => InitializeComponent();

    private async void UploadVisualClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not GhostProfilesViewModel viewModel) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Upload visual",
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"] },
                new FilePickerFileType("3D models") { Patterns = ["*.gltf", "*.glb", "*.obj"] }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) await viewModel.UploadVisualAsync(path);
    }
}
