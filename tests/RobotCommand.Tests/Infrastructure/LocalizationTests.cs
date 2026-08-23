using RobotCommand.Localization;
using RobotCommand.Services;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public async Task FrenchResourcesAreSelectedAndEnglishRemainsFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);
        var changed = 0;
        var summaryBindingChanged = false;
        localization.PropertyChanged += (_, _) => changed++;
        LocalizationBindingSource.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Item[WorkspaceSummary]")
            {
                summaryBindingChanged = true;
            }
        };

        await localization.SetLanguageAsync("fr", TestContext.Current.CancellationToken);

        Assert.Equal("fr", localization.CurrentLanguage);
        Assert.Equal("Param\u00e8tres", LocalizationBindingSource.Instance["Settings"]);
        Assert.Equal(
            localization.AvailableLanguages.Single(item => item.Code == "fr"),
            new LanguageOption("fr", "different localized label"));
        Assert.Equal("Paramètres", localization.Get("Settings"));
        Assert.Equal("Opérations", localization.Get("Operate"));
        Assert.Equal("Carte, vidéo, télémétrie et commandes opérationnelles", localization.Get("OperateDescription"));
        Assert.Equal("Topographique", localization.Get("MapTopographic"));
        Assert.DoesNotContain("[", localization.Get("OperateDescription"), StringComparison.Ordinal);
        Assert.True(changed > 0);
        Assert.True(summaryBindingChanged);

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
        Assert.Equal("Settings", localization.Get("Settings"));
        Assert.Equal("Topographic", localization.Get("MapTopographic"));
        Assert.Equal("en", AppConfiguration.Load(directory).Ui.Language);
    }

    [Fact]
    public async Task UnsupportedLanguageNormalizesToEnglish()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        await localization.SetLanguageAsync("de", TestContext.Current.CancellationToken);

        Assert.Equal("en", localization.CurrentLanguage);
        Assert.Equal("Settings", localization.Get("Settings"));
    }

    [Fact]
    public async Task ResourceLookupAcceptsLegacyCamelCaseKeysWhenLanguageChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        Assert.Equal(["en", "fr"], localization.AvailableLanguages.Select(item => item.Code).ToArray());

        await localization.SetLanguageAsync("fr", TestContext.Current.CancellationToken);

        Assert.Equal("Carte, vidéo, télémétrie et commandes opérationnelles", localization.Get("operateDescription"));
        Assert.DoesNotContain("[", localization.Get("mapsDescription"), StringComparison.Ordinal);

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);

        Assert.Equal("Map, video, telemetry, and operational controls", localization.Get("operateDescription"));
        Assert.Equal("English", localization.AvailableLanguages.Single(item => item.Code == "en").DisplayName);
    }

    [Fact]
    public async Task SettingsLanguageSelectionAlwaysReachesLocalizationService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);
        var viewModel = new SettingsViewModel(settings, localization);

        viewModel.SelectLanguage("fr");
        await WaitForLanguageAsync(localization, "fr");
        Assert.Equal("Param\u00e8tres", localization.Get("Settings"));
        viewModel.SelectLanguage("en");
        await WaitForLanguageAsync(localization, "en");
        Assert.Equal("Settings", localization.Get("Settings"));
    }

    [Fact]
    public async Task SettingsLanguageIndexSelectionChangesTheServiceAndBindingSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Logos-robot-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);
        var viewModel = new SettingsViewModel(settings, localization);

        viewModel.SelectedLanguageIndex = 1;
        await WaitForLanguageAsync(localization, "fr");
        Assert.Equal("Param\u00e8tres", LocalizationBindingSource.Instance["Settings"]);

        viewModel.SelectedLanguageIndex = 0;
        await WaitForLanguageAsync(localization, "en");
        Assert.Equal("Settings", LocalizationBindingSource.Instance["Settings"]);
    }

    private static async Task WaitForLanguageAsync(ILocalizationService localization, string expected)
    {
        for (var attempt = 0; attempt < 50 && localization.CurrentLanguage != expected; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(expected, localization.CurrentLanguage);
    }
}
