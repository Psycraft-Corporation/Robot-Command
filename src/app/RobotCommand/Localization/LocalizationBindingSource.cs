using System.ComponentModel;

namespace RobotCommand.Localization;

/// <summary>
/// Stable binding source for XAML localization extensions.
///
/// XAML objects can outlive a DI service instance during startup and workspace
/// recreation. Keeping one observable source prevents localized bindings from
/// becoming attached to an earlier fallback service.
/// </summary>
public sealed class LocalizationBindingSource : INotifyPropertyChanged
{
    private ILocalizationService? _service;

    private LocalizationBindingSource() { }

    public static LocalizationBindingSource Instance { get; } = new();

    public string this[string key]
        => _service?.Get(key) ?? $"[{key}]";

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Attach(ILocalizationService service)
    {
        if (ReferenceEquals(_service, service))
        {
            return;
        }

        if (_service is not null)
        {
            _service.PropertyChanged -= OnServicePropertyChanged;
        }

        _service = service;
        _service.PropertyChanged += OnServicePropertyChanged;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Avalonia's indexer binding does not consistently refresh every
        // `[{key}]` path from the broad Item[] notification alone. Publish the
        // exact indexer property for every resource key whenever the language
        // changes, while retaining the broad notifications for consumers that
        // bind to the indexer as a whole.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

        if (_service is not LocalizationService localization)
        {
            return;
        }

        foreach (var key in localization.ResourceKeys)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Item[{key}]"));
        }
    }
}
