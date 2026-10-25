using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BrainstormBuddy;

public partial class SettingsWindow
{
    private readonly List<SettingsSearchEntry> _settingsSearchEntries = new();
    private bool _settingsSearchEntriesInitialized;
    // Дебаунс ввода: результат пересчитывается после паузы в печати, а не на каждый
    // символ — список не пересобирается и не дёргает layout под каждым нажатием.
    private DispatcherTimer? _settingsSearchDebounce;

    /// <summary>
    /// Static, safe catalog of settings. It deliberately contains labels and aliases only;
    /// never read bound values (in particular API keys, URLs, model names, or user prompts).
    /// </summary>
    internal sealed class SettingsSearchEntry
    {
        public SettingsSearchEntry(
            string titleKey,
            string sectionKey,
            string descriptionKey,
            Func<FrameworkElement?> target,
            params string[] aliases)
        {
            TitleKey = titleKey;
            SectionKey = sectionKey;
            DescriptionKey = descriptionKey;
            Target = target;
            Aliases = aliases;
        }

        public string TitleKey { get; }
        public string SectionKey { get; }
        public string DescriptionKey { get; }
        public Func<FrameworkElement?> Target { get; }
        public IReadOnlyList<string> Aliases { get; }
    }

    public sealed class SettingsSearchResult
    {
        internal SettingsSearchResult(SettingsSearchEntry entry, string title, string section, string description)
        {
            Entry = entry;
            Title = title;
            Section = section;
            Description = description;
        }

        private SettingsSearchEntry Entry { get; }
        public string Title { get; }
        public string Section { get; }
        public string Description { get; }

        internal FrameworkElement? ResolveTarget() => Entry.Target();
    }

    private void EnsureSettingsSearchEntries()
    {
        if (_settingsSearchEntriesInitialized) return;
        _settingsSearchEntriesInitialized = true;

        // API / LLM: targets are named controls so navigation works at both ends of long tabs.
        AddSettingsSearchEntry("L_SetApi_ProviderLabel", "L_Tab_Api", "L_SetApi_PrivacyHint",
            () => LlmProviderCombo, "llm", "api", "провайдер", "provider", "подключение", "connection");
        AddSettingsSearchEntry("L_SetApi_ChatModelLabel", "L_Tab_Api", "L_SetApi_ModelExample",
            () => ChatModelCombo, "модель", "model", "чат", "chat", "llm");
        AddSettingsSearchEntry("L_SetApi_BaseUrlLabel", "L_Tab_Api", "L_SetApi_BaseUrlExample",
            () => ActiveBaseUrlBox, "адрес", "url", "endpoint", "сервер", "server");
        AddSettingsSearchEntry("L_SetApi_RateLimitLabel", "L_Tab_Api", "L_SetApi_RateLimitLabel",
            () => RateLimitBox, "rate limit", "задержка", "частота запросов", "requests");
        AddSettingsSearchEntry("L_SetLlm_PresetsTitle", "L_Tab_Llm", "L_SetLlm_PresetsHint",
            () => PromptEditor, "промпт", "prompt", "шаблон", "preset", "пресет", "llm");
        AddSettingsSearchEntry("L_StreamLlm", "L_Tab_Llm", "L_StreamLlmHint",
            () => StreamLlmCheck, "стриминг", "streaming", "stream", "поток", "llm");
        AddSettingsSearchEntry("L_SetLlm_HistorySizeLabel", "L_Tab_Llm", "L_SetLlm_HistorySizeNote",
            () => HistorySizeSlider, "история чата", "chat history", "контекст", "context", "llm");
        AddSettingsSearchEntry("L_SetLlm_MaxTokensLabel", "L_Tab_Llm", "L_SetLlm_MaxTokensText",
            () => MaxTokensBox, "токены", "токен", "tokens", "token", "max tokens", "лимит ответа", "длина ответа", "llm");

        // Audio and recognition.
        AddSettingsSearchEntry("L_SetAudio_EndpointModeLabel", "L_Tab_Audio", "L_SetAudio_EndpointModeHint",
            () => EndpointModeCombo, "аудио", "audio", "пауза", "тишина", "speech", "голос");
        AddSettingsSearchEntry("L_SetAudio_MicLabel", "L_Tab_Audio", "L_SetAudio_MicTestHint",
            () => MicDeviceCombo, "микрофон", "microphone", "mic", "звук", "audio");
        AddSettingsSearchEntry("L_SetAudio_SpeakerLabel", "L_Tab_Audio", "L_SetAudio_EndpointModeHint",
            () => SpeakerDeviceCombo, "динамик", "speaker", "loopback", "звук", "audio");
        AddSettingsSearchEntry("L_SetStt_EngineLabel", "L_Tab_LocalStt", "L_SetStt_EngineApplyNote",
            () => SttEngineCombo, "stt", "распознавание", "speech recognition", "движок", "engine", "голос");

        // Overlay and visible answer limits. MaxAnswerChars is distinct from max response tokens.
        AddSettingsSearchEntry("L_SetOvl_OpacityLabel", "L_Tab_Overlay", "L_SetOvl_OpacityLabel",
            () => OpacitySlider, "оверлей", "overlay", "прозрачность", "opacity");
        AddSettingsSearchEntry("L_SetOvl_PositionLabel", "L_Tab_Overlay", "L_SetOvl_PositionLabel",
            () => PositionCombo, "позиция", "position", "расположение", "экран", "screen");
        AddSettingsSearchEntry("L_SetOvl_FontSizeLabel", "L_Tab_Overlay", "L_SetOvl_FontSizeLabel",
            () => FontSizeSlider, "шрифт", "font", "размер текста", "font size");
        AddSettingsSearchEntry("L_Ui_MaxAnswerChars", "L_Tab_Overlay", "L_Ui_MaxAnswerCharsHint",
            () => MaxAnswerCharsBox, "символы", "символ", "characters", "chars", "лимит", "ответ", "answer length");
        AddSettingsSearchEntry("L_Theme", "L_Tab_Overlay", "L_Theme",
            () => ThemeCombo, "тема", "theme", "оформление");
        AddSettingsSearchEntry("L_Language", "L_Tab_Overlay", "L_Language",
            () => LanguageCombo, "язык", "language", "локализация", "localization");

        // Karaoke is a deep tab; include controls near its top and bottom.
        AddSettingsSearchEntry("L_SetKar_Enable", "L_Tab_Karaoke", "L_SetKar_Enable",
            () => KaraokeEnabledCheck,
            "караоке", "karaoke", "телесуфлёр", "teleprompter");
        AddSettingsSearchEntry("L_SetKar_Position", "L_Tab_Karaoke", "L_SetKar_Position",
            () => KaraokePositionCombo, "полоса", "strip", "позиция", "position");
        AddSettingsSearchEntry("L_SetKar_FontSize", "L_Tab_Karaoke", "L_SetKar_FontSize",
            () => KaraokeFontSizeSlider, "караоке шрифт", "karaoke font", "размер шрифта", "font size");
        AddSettingsSearchEntry("L_SetKar_MaxLines", "L_Tab_Karaoke", "L_SetKar_MaxLines",
            () => KaraokeMaxLinesSlider, "строки", "lines", "видимых строк", "visible lines");
        AddSettingsSearchEntry("L_SetKar_Preview", "L_Tab_Karaoke", "L_SetKar_Preview",
            () => KaraokePreviewButton, "предпросмотр", "preview", "тест");

        // Hotkeys: the dedicated tab has named editors (x:Name) for navigation.
        // Without these, "горячие клавиши" only matched the OCR-hotkey note on
        // the ScreenOcr tab and never surfaced the actual Hotkeys tab.
        AddSettingsSearchEntry("L_SetHK_ToggleOverlay", "L_Tab_Hotkeys", "L_SetHK_Modifiers",
            () => ToggleVisibilityHotkeyBox,
            "горячие клавиши", "hotkeys", "hotkey", "клавиши", "клавиша", "сочетание", "shortcut", "бинды", "keys");
        AddSettingsSearchEntry("L_HK_ShowTray", "L_Tab_Hotkeys", "L_SetHK_Modifiers",
            () => ShowTrayIconHotkeyBox,
            "горячие клавиши", "hotkeys", "hotkey", "клавиши", "трей", "tray", "иконка", "keys");

        // OCR preview/action are at the bottom of a long page; use actual named controls.
        AddSettingsSearchEntry("L_SetOcr_Engine", "L_Tab_ScreenOcr", "L_SetOcr_EngineHint",
            () => ScreenOcrEngineCombo,
            "ocr", "экран", "screen", "распознавание экрана", "text recognition");
        AddSettingsSearchEntry("L_SetOcr_Interval", "L_Tab_ScreenOcr", "L_SetOcr_HotkeyNote",
            () => ScreenOcrPollSlider, "ocr interval", "интервал", "poll", "опрос");
        AddSettingsSearchEntry("L_SetOcr_Threshold", "L_Tab_ScreenOcr", "L_SetOcr_Threshold",
            () => ScreenOcrThresholdSlider, "порог", "threshold", "изменения", "change");
        AddSettingsSearchEntry("L_SetOcr_OnceLabel", "L_Tab_ScreenOcr", "L_SetOcr_HotkeyNote",
            () => ScreenOcrOnceBtn, "распознать сейчас", "recognize now", "once", "разово");
        AddSettingsSearchEntry("L_SetOcr_AutoAnswer", "L_Tab_ScreenOcr", "L_SetOcr_HotkeyNote",
            () => ScreenOcrAutoAnswerCheck, "ocr answer", "ответ через llm", "автоответ", "auto answer");

        // Memory: include both the first card and the deepest project/retention controls.
        AddSettingsSearchEntry("L_Mem_Enabled", "L_Tab_Memory", "L_Mem_EnabledHint",
            () => MemoryEnabledCheck, "память", "memory", "факты", "facts", "buddy memory");
        AddSettingsSearchEntry("L_Mem_ModelMode", "L_Tab_Memory", "L_Mem_ModelHint",
            () => MemoryModelModeCombo, "модель памяти", "memory model", "model");
        AddSettingsSearchEntry("L_Mem_Model", "L_Tab_Memory", "L_Mem_ModelHint",
            () => MemoryModelCombo, "модель", "model", "память", "memory");
        AddSettingsSearchEntry("L_Mem_AutoBudget", "L_Tab_Memory", "L_Mem_AutoBudgetHint",
            () => MemoryAutoBudgetSlider, "токены памяти", "memory tokens", "tokens", "контекст памяти");
        AddSettingsSearchEntry("L_Mem_ExtraSearchBudget", "L_Tab_Memory", "L_Mem_ExtraSearchBudgetHint",
            () => MemoryExtraBudgetSlider, "дополнительный поиск", "extra search", "budget", "бюджет поиска");
        AddSettingsSearchEntry("L_Mem_BuiltinTools", "L_Tab_Memory", "L_Mem_BuiltinToolsHint",
            () => MemoryBuiltinToolsCheck, "инструменты", "tools", "поиск модели", "self search");
        AddSettingsSearchEntry("L_Mem_ExtractIntensity", "L_Tab_Memory", "L_Mem_ExtractIntensityHint",
            () => MemoryIntensityCombo, "интенсивность", "intensity", "извлечение", "extraction");
        AddSettingsSearchEntry("L_Mem_Retention", "L_Tab_Memory", "L_Mem_RetentionDaysHint",
            () => MemoryRetentionCombo, "хранение памяти", "memory retention", "retention", "срок");
        AddSettingsSearchEntry("L_Mem_Project", "L_Tab_Memory", "L_Mem_ProjectAddHint",
            () => MemoryProjectCombo, "проект", "project", "область", "scope");

        // The archive/transfer page is added by the data-portability change. Keep its
        // registration optional: absent names are skipped, so this catalog never creates
        // a fake button or assumes a backup UI that is not present yet.
        RegisterOptionalSettingsSearchEntries();
    }

    private void AddSettingsSearchEntry(
        string titleKey,
        string sectionKey,
        string descriptionKey,
        Func<FrameworkElement?> target,
        params string[] aliases)
    {
        _settingsSearchEntries.Add(new SettingsSearchEntry(titleKey, sectionKey, descriptionKey, target, aliases));
    }

    private FrameworkElement? FindNamedSettingsElement(string name)
    {
        try
        {
            return FindName(name) as FrameworkElement;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Optional extension point for the later backup/transfer page. All candidates are
    /// resolved by name at runtime; entries are omitted until an actual control exists.
    /// </summary>
    private void RegisterOptionalSettingsSearchEntries()
    {
        RegisterOptionalSettingsSearchEntry("BackupSection", "L_SetSearch_BackupTitle", "L_SetSearch_BackupSection", "L_SetSearch_BackupDescription",
            "бэкап", "backup", "резервная копия", "резерв", "перенос", "transfer", "archive", "архив");
        RegisterOptionalSettingsSearchEntry("BackupCreateBtn", "L_SetSearch_BackupCreate", "L_SetSearch_BackupSection", "L_SetSearch_BackupDescription",
            "бэкап", "backup", "создать резервную копию", "create backup", "экспорт", "export");
        RegisterOptionalSettingsSearchEntry("CreateBackupBtn", "L_SetSearch_BackupCreate", "L_SetSearch_BackupSection", "L_SetSearch_BackupDescription",
            "бэкап", "backup", "создать резервную копию", "create backup", "экспорт", "export");
        RegisterOptionalSettingsSearchEntry("BackupRestoreBtn", "L_SetSearch_BackupRestore", "L_SetSearch_BackupSection", "L_SetSearch_BackupDescription",
            "бэкап", "backup", "восстановить", "restore", "импорт", "import", "архив");
        RegisterOptionalSettingsSearchEntry("RestoreBackupBtn", "L_SetSearch_BackupRestore", "L_SetSearch_BackupSection", "L_SetSearch_BackupDescription",
            "бэкап", "backup", "восстановить", "restore", "импорт", "import", "архив");
    }

    private void RegisterOptionalSettingsSearchEntry(
        string controlName,
        string titleKey,
        string sectionKey,
        string descriptionKey,
        params string[] aliases)
    {
        var target = FindNamedSettingsElement(controlName);
        if (target != null)
            AddSettingsSearchEntry(titleKey, sectionKey, descriptionKey, () => target, aliases);
    }

    private DispatcherTimer SearchDebounceTimer
    {
        get
        {
            _settingsSearchDebounce ??= CreateSearchDebounceTimer();
            return _settingsSearchDebounce;
        }
    }

    private DispatcherTimer CreateSearchDebounceTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            RunSettingsSearch();
        };
        return timer;
    }

    private void OnSettingsSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        EnsureSettingsSearchEntries();
        var query = SettingsSearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            // Пустой запрос сворачивает выдачу сразу, без ожидания дебаунса.
            _settingsSearchDebounce?.Stop();
            ClearSettingsSearchResults();
            return;
        }

        SearchDebounceTimer.Stop();
        SearchDebounceTimer.Start();
    }

    /// <summary>Клавиатура в поле поиска: Down — в выдачу, Enter — перейти к
    /// выбранному/первому результату, Escape — закрыть поиск.</summary>
    private void OnSettingsSearchBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool hasResults = SettingsSearchResultsList.Visibility == Visibility.Visible
                          && SettingsSearchResultsList.Items.Count > 0;
        switch (e.Key)
        {
            case Key.Down when hasResults:
                if (SettingsSearchResultsList.SelectedIndex < 0)
                    SettingsSearchResultsList.SelectedIndex = 0;
                Keyboard.Focus(SettingsSearchResultsList);
                e.Handled = true;
                break;
            case Key.Enter when hasResults:
                ActivateSettingsSearchResult(
                    SettingsSearchResultsList.SelectedItem as SettingsSearchResult
                    ?? SettingsSearchResultsList.Items.OfType<SettingsSearchResult>().FirstOrDefault());
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSettingsSearch();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Клик мышью — единственный «мышиный» способ активации. Намеренно не
    /// SelectionChanged: стрелки по списку только ходят по пунктам, а переход к
    /// настройке выполняют клик и Enter. Иначе клавиатурный просмотр выдачи
    /// переключал бы вкладки под пользователем.</summary>
    private void OnSettingsSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        // MouseLeftButtonUp приходит до SelectionChanged в некоторых шаблонах:
        // SelectedItem ещё указывает на прошлую строку. Берём DataContext именно
        // нажатого контейнера, а не «текущее выделение».
        var container = ItemsControl.ContainerFromElement(
            SettingsSearchResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not SettingsSearchResult result) return;
        ActivateSettingsSearchResult(result);
    }

    /// <summary>Enter на выбранном пункте выдачи переходит к настройке;
    /// Escape возвращает фокус в поле и закрывает поиск.</summary>
    private void OnSettingsSearchResultsKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ActivateSettingsSearchResult(SettingsSearchResultsList.SelectedItem as SettingsSearchResult);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSettingsSearch();
                SettingsSearchBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void ActivateSettingsSearchResult(SettingsSearchResult? result)
    {
        var target = result?.ResolveTarget();
        if (target == null) return;

        // Тот же порядок, что в HighlightSection: выбрать вкладку → UpdateLayout
        // (контент вкладки попадает в визуальное дерево) → на следующем проходе
        // (Loaded) прокрутить и подсветить. Без UpdateLayout BringIntoView мог
        // отработать до разворачивания контента только что выбранной вкладки.
        //
        // Панель выдачи закрываем до прокрутки: она лежит поверх вкладки и
        // перекрывала верхние настройки (провайдер, MaxTokens) — вспышка
        // рисовалась под ней, пользователь не видел, куда его перевели. Запрос
        // в поле оставляем, чтобы можно было продолжить поиск с того же места.
        ClearSettingsSearchResults();
        SelectTabContaining(target);
        try { UpdateLayout(); } catch { }
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    target.BringIntoView();
                    // ScrollViewer обрабатывает RequestBringIntoView в своём
                    // layout-проходе; вспышку вешаем после него, иначе adorner
                    // получает старую (докрученную) позицию.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            target.UpdateLayout();
                            FlashElement(target);
                        }
                        catch (Exception ex)
                        {
                            App.Current.Logger?.Warn($"Settings search flash failed: {ex.Message}", "UI");
                        }
                    }), DispatcherPriority.Render);
                }
                catch (Exception ex)
                {
                    App.Current.Logger?.Warn($"Settings search navigation failed: {ex.Message}", "UI");
                }
            }), DispatcherPriority.Loaded);
        }
        catch { /* окно уже закрывается */ }
    }

    private void OnSettingsSearchClose(object sender, RoutedEventArgs e) => CloseSettingsSearch();

    private void CloseSettingsSearch()
    {
        // Очищается только поисковый UI — привязанные редакторы не трогаем,
        // несохранённые правки настроек остаются на месте.
        _settingsSearchDebounce?.Stop();
        SettingsSearchBox.Clear();
        ClearSettingsSearchResults();
    }

    private void ClearSettingsSearchResults()
    {
        SettingsSearchResultsList.ItemsSource = null;
        SettingsSearchResultsPanel.Visibility = Visibility.Collapsed;
        SettingsSearchEmptyText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Смена языка: пересчитать подписи выдачи немедленно (не через
    /// дебаунс), чтобы уже открытый список показал новый язык.</summary>
    private void RefreshSettingsSearchResults()
    {
        _settingsSearchDebounce?.Stop();
        if (SettingsSearchBox.Text?.Length > 0)
            RunSettingsSearch();
    }

    private void RunSettingsSearch()
    {
        EnsureSettingsSearchEntries();
        var query = SettingsSearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            ClearSettingsSearchResults();
            return;
        }

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSearchText)
            .Where(static term => term.Length > 0)
            .ToArray();
        var results = new List<SettingsSearchResult>();
        foreach (var entry in _settingsSearchEntries)
        {
            var title = SearchResourceText(entry.TitleKey);
            var section = SearchResourceText(entry.SectionKey);
            var description = SearchResourceText(entry.DescriptionKey);
            var haystack = NormalizeSearchText(string.Join(" ", title, section, description, string.Join(" ", entry.Aliases)));
            if (terms.All(haystack.Contains))
                results.Add(new SettingsSearchResult(entry, title, section, description));
        }

        SettingsSearchResultsList.ItemsSource = results;
        SettingsSearchResultsPanel.Visibility = Visibility.Visible;
        SettingsSearchResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchEmptyText.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }


    private string SearchResourceText(string key)
    {
        try
        {
            var value = TryFindResource(key);
            return value as string ?? value?.ToString() ?? key;
        }
        catch
        {
            return key;
        }
    }

    private static string NormalizeSearchText(string value)
        => value.Trim().ToUpperInvariant();
}
