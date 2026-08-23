using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Views.Workspaces;

public sealed partial class ManualControlView : UserControl
{
    public ManualControlView() => AvaloniaXamlLoader.Load(this);
}
