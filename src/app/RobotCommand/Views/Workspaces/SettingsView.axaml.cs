using Avalonia.Controls;
using RobotCommand.ViewModels;

namespace RobotCommand.Views.Workspaces;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnLanguageDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox && DataContext is SettingsViewModel viewModel)
        {
            // Submit only after the popup closes. SelectionChanged also fires
            // while ItemsSource is rebuilt in response to localization updates;
            // treating those internal changes as user input creates a language
            // feedback loop and makes the UI flicker between cultures.
            viewModel.SelectLanguageIndex(comboBox.SelectedIndex);
        }
    }

    private void OnUnitDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox && DataContext is SettingsViewModel viewModel && comboBox.Tag is string key)
        {
            viewModel.SelectUnit(key, comboBox.SelectedIndex);
        }
    }
}
