using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Views.Shell;

public sealed partial class OperateInspectorView : UserControl
{
    public OperateInspectorView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
