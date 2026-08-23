using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RobotCommand.ViewModels;
namespace RobotCommand.Views.Workspaces;

public sealed partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();

    private async void ImportParameters_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import MAVLink parameter profile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MAVLink parameters") { Patterns = ["*.params", "*.param", "*.txt"] }
            ]
        });
        if (files.Count == 0) return;
        await viewModel.ImportParameterProfileAsync(files[0].Path.LocalPath);
    }

    private async void ExportParameters_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export MAVLink parameter profile",
            SuggestedFileName = "mavlink-parameters.params",
            DefaultExtension = "params",
            FileTypeChoices =
            [
                new FilePickerFileType("MAVLink parameters") { Patterns = ["*.params", "*.param"] }
            ]
        });
        if (file is null) return;
        await viewModel.ExportSelectedParameterProfileAsync(file.Path.LocalPath);
    }
}
