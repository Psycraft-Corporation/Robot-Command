using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Views.Workspaces;

public sealed partial class AutonomyWorkspaceView : UserControl
{
    public AutonomyWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
