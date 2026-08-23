using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Views.Workspaces;

public sealed partial class TaskBehaviourComposerView : UserControl
{
    public TaskBehaviourComposerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
