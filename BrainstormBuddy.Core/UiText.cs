namespace BrainstormBuddy;

/// <summary>
/// Мини-локализатор для Core: у Core нет доступа к WPF-словарям Strings.*.xaml,
/// поэтому UI-строки, рождённые в Core (метки ролей, подписи списка встреч,
/// статусы саммари), дублируются парами ru/en — как SpeakerTr в SettingsViewModel.
/// Язык выставляет App.ApplyLanguage → UiText.Lang (дефолт "ru").
/// </summary>
public static class UiText
{
    /// <summary>"ru" | "en" — синхронизируется с Config.Ui.Language при применении.</summary>
    public static string Lang { get; set; } = "ru";

    /// <summary>Выбор строки по текущему языку UI.</summary>
    public static string Tr(string ru, string en) => Lang == "en" ? en : ru;
}
