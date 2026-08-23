using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace RobotCommand.Localization;

public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding
        {
            Path = $"[{Key}]",
            Source = LocalizationBindingSource.Instance,
            Mode = BindingMode.OneWay
        };
}
