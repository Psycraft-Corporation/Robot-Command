using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Views.Workspaces;

public sealed partial class AutonomyPlansView : UserControl
{
    public AutonomyPlansView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
