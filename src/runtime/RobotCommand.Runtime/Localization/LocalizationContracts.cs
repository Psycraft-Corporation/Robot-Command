using System.ComponentModel;

namespace RobotCommand.Localization;

public interface ILocalizationService : INotifyPropertyChanged
{
    string CurrentLanguage { get; }
    IReadOnlyList<LanguageOption> AvailableLanguages { get; }
    string Get(string key);
    string this[string key] { get; }
    Task SetLanguageAsync(string language, CancellationToken cancellationToken = default);
}

public sealed class LanguageOption : IEquatable<LanguageOption>
{
    public LanguageOption(string code, string displayName) { Code = code; DisplayName = displayName; }
    public string Code { get; }
    public string DisplayName { get; }
    public bool Equals(LanguageOption? other) => other is not null && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => Equals(obj as LanguageOption);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Code);
}
