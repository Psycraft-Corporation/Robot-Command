using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Views.Workspaces;

public sealed partial class BehaviourLibraryView : UserControl
{
    public BehaviourLibraryView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
