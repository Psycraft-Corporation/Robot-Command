using RobotCommand.Localization;
using RobotCommand.Services;
using RobotCommand.ViewModels;
using Xunit;

namespace RobotCommand.Tests;

[Collection("Global localization")]
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
            new LanguageOption("fr", "Français"));
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
    public async Task JapaneseResourcesAreSelectableAndTechnicalFallbackRemainsEnglish()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        Assert.Contains(new LanguageOption("ja", "日本語"), localization.AvailableLanguages);

        await localization.SetLanguageAsync("ja", TestContext.Current.CancellationToken);

        Assert.Equal("ja", localization.CurrentLanguage);
        Assert.Equal("設定", localization.Get("Settings"));
        Assert.Equal("日本語", localization.AvailableLanguages.Single(item => item.Code == "ja").DisplayName);
        Assert.Equal("RTL", localization.Get("FlightMissionRtl"));
        Assert.Equal("ロボットコマンド", localization.Get("AppTitle"));
        Assert.Equal("コマンド", localization.Get("Commands"));
        Assert.Equal("コマンド履歴", localization.Get("HistoryCommandHistory"));
        Assert.Equal("ジオメトリを検索", localization.Get("GeometrySearch"));
        Assert.Equal("インポートとエクスポート", localization.Get("GeometryImportExport"));
        Assert.Equal("グループ", localization.Get("GeometryGroups"));
        Assert.DoesNotContain("[", localization.Get("OperateDescription"), StringComparison.Ordinal);

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CommandHistoryTitleUsesLocalizedUiLabelsWhileNativeCommandNamesRemainStable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        await localization.SetLanguageAsync("fr", TestContext.Current.CancellationToken);
        Assert.Equal("Commandes", localization.Get("Commands"));
        Assert.Equal("Takeoff", localization.Get("FlightMissionTakeoff"));
        Assert.Equal("Land", localization.Get("FlightMissionLand"));
        Assert.Equal("Hold", localization.Get("FlightMissionEndHold"));
        Assert.Equal("RTL", localization.Get("FlightMissionEndRtl"));

        await localization.SetLanguageAsync("uk", TestContext.Current.CancellationToken);
        Assert.Equal("Команди", localization.Get("Commands"));
        Assert.Equal("Takeoff", localization.Get("FlightMissionTakeoff"));
        Assert.Equal("Land", localization.Get("FlightMissionLand"));
        Assert.Equal("Hold", localization.Get("FlightMissionEndHold"));
        Assert.Equal("RTL", localization.Get("FlightMissionEndRtl"));

        await localization.SetLanguageAsync("ko", TestContext.Current.CancellationToken);
        Assert.Equal("명령", localization.Get("Commands"));
        Assert.Equal("Takeoff", localization.Get("FlightMissionTakeoff"));
        Assert.Equal("Land", localization.Get("FlightMissionLand"));
        Assert.Equal("Hold", localization.Get("FlightMissionEndHold"));
        Assert.Equal("RTL", localization.Get("FlightMissionEndRtl"));

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
        Assert.Equal("Commands", localization.Get("Commands"));
    }

    [Fact]
    public async Task UkrainianResourcesAreSelectableAndLanguageNameIsLocalized()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        Assert.Contains(new LanguageOption("uk", "Українська"), localization.AvailableLanguages);

        await localization.SetLanguageAsync("uk-UA", TestContext.Current.CancellationToken);

        Assert.Equal("uk", localization.CurrentLanguage);
        Assert.Equal("Налаштування", localization.Get("Settings"));
        Assert.Equal("Операції", localization.Get("Operate"));
        Assert.Equal("Українська", localization.AvailableLanguages.Single(item => item.Code == "uk").DisplayName);
        Assert.Equal("Історія команд", localization.Get("HistoryCommandHistory"));
        Assert.Equal("Деталі з’єднання", localization.Get("ConnectionDetails"));
        Assert.Equal("Медіа", localization.Get("SettingsMedia"));
        Assert.Equal("Профіль", localization.Get("ManualProfile"));
        Assert.Equal("Контролер", localization.Get("ManualController"));
        Assert.Equal("Погода", localization.Get("MapWeatherRadar"));
        Assert.Equal("Takeoff", localization.Get("FlightMissionTakeoff"));
        Assert.Equal("RTL", localization.Get("FlightMissionRtl"));
        Assert.DoesNotContain("[", localization.Get("WorkspaceSummary"), StringComparison.Ordinal);

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task KoreanResourcesAreSelectableAndPersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        Assert.Contains(new LanguageOption("ko", "한국어"), localization.AvailableLanguages);

        await localization.SetLanguageAsync("ko-KR", TestContext.Current.CancellationToken);

        Assert.Equal("ko", localization.CurrentLanguage);
        Assert.Equal("설정", localization.Get("Settings"));
        Assert.Equal("운영", localization.Get("Operate"));
        Assert.Equal("한국어", localization.AvailableLanguages.Single(item => item.Code == "ko").DisplayName);
        Assert.Equal("지도", localization.Get("OperateMap"));
        Assert.Equal("영상", localization.Get("OperateVideo"));
        Assert.Equal("유닛", localization.Get("OperateUnit"));
        Assert.Equal("선택 항목 따라가기", localization.Get("MapFollowSelected"));
        Assert.Equal("따라가기 중지", localization.Get("MapStopFollowing"));
        Assert.Equal("설치된 지도", localization.Get("MapInstalledMaps"));
        Assert.Equal("명령 기록", localization.Get("HistoryCommandHistory"));
        Assert.Equal("이벤트 스트림", localization.Get("HistoryEventStream"));
        Assert.Equal("지오메트리 검색", localization.Get("GeometrySearch"));
        Assert.Equal("새로 만들기", localization.Get("FlightMissionNew"));
        Assert.Equal("연결 세부 정보", localization.Get("ConnectionDetails"));
        Assert.Equal("장치 검색", localization.Get("ConnectionDetectDevices"));
        Assert.Equal("팀 관찰 서버", localization.Get("MyTeamServer"));
        Assert.Equal("Takeoff", localization.Get("FlightMissionTakeoff"));
        Assert.Equal("RTL", localization.Get("FlightMissionRtl"));
        Assert.Equal("Land", localization.Get("FlightMissionLand"));
        Assert.Equal("Hold", localization.Get("FlightMissionEndHold"));
        Assert.DoesNotContain("[", localization.Get("WorkspaceSummary"), StringComparison.Ordinal);
        Assert.Equal("ko", AppConfiguration.Load(directory).Ui.Language);

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LanguageNamesRemainInTheirNativeLanguage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);
        var expected = new[]
        {
            new LanguageOption("en", "English"),
            new LanguageOption("fr", "Français"),
            new LanguageOption("ja", "日本語"),
            new LanguageOption("uk", "Українська"),
            new LanguageOption("ko", "한국어")
        };

        foreach (var language in new[] { "en", "fr", "ja", "uk", "ko" })
        {
            await localization.SetLanguageAsync(language, TestContext.Current.CancellationToken);
            Assert.Equal(expected, localization.AvailableLanguages);
        }

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SharedBindingsReturnEnglishAfterEverySupportedLanguage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new ApplicationSettingsService(
            AppConfiguration.Load(directory),
            new ApplicationSettingsPersistence(directory));
        var localization = new LocalizationService(settings);

        foreach (var language in new[] { "fr", "ja", "uk", "ko" })
        {
            await localization.SetLanguageAsync(language, TestContext.Current.CancellationToken);
            Assert.NotEqual("Operations", LocalizationBindingSource.Instance["InspectorOperations"]);
            Assert.NotEqual("Units", LocalizationBindingSource.Instance["Units"]);
            Assert.NotEqual("Ghosts", LocalizationBindingSource.Instance["Ghosts"]);

            await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
            Assert.Equal("Operations", LocalizationBindingSource.Instance["InspectorOperations"]);
            Assert.Equal("Units", LocalizationBindingSource.Instance["Units"]);
            Assert.Equal("Ghosts", LocalizationBindingSource.Instance["Ghosts"]);
            Assert.Equal("Robot Command", LocalizationBindingSource.Instance["AppTitle"]);
        }
    }

    [Fact]
    public async Task UkrainianSelectionPersistsAndSurvivesSettingsReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RobotCommand-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configuration = AppConfiguration.Load(directory);
        var persistence = new ApplicationSettingsPersistence(directory);
        var settings = new ApplicationSettingsService(configuration, persistence);
        var localization = new LocalizationService(settings);

        await localization.SetLanguageAsync("uk-UA", TestContext.Current.CancellationToken);

        Assert.Equal("uk", settings.Current.Language);
        Assert.Equal("uk", AppConfiguration.Load(directory).Ui.Language);
        Assert.Equal("Налаштування", localization.Get("Settings"));

        await localization.SetLanguageAsync("en", TestContext.Current.CancellationToken);
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

        Assert.Equal(["en", "fr", "ja", "uk", "ko"], localization.AvailableLanguages.Select(item => item.Code).ToArray());

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
        viewModel.SelectLanguage("uk");
        await WaitForLanguageAsync(localization, "uk");
        Assert.Equal("Налаштування", localization.Get("Settings"));
        viewModel.SelectLanguage("ko");
        await WaitForLanguageAsync(localization, "ko");
        Assert.Equal("설정", localization.Get("Settings"));
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

        viewModel.SelectedLanguageIndex = 3;
        await WaitForLanguageAsync(localization, "uk");
        Assert.Equal("Налаштування", LocalizationBindingSource.Instance["Settings"]);

        viewModel.SelectedLanguageIndex = 4;
        await WaitForLanguageAsync(localization, "ko");
        Assert.Equal("설정", LocalizationBindingSource.Instance["Settings"]);

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
