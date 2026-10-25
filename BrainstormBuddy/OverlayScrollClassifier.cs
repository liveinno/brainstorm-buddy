namespace BrainstormBuddy;

/// <summary>
/// Классификация ScrollChanged оверлея (§4 аудита 2026-10-15): пользовательская
/// прокрутка против layout/programmatic-индуцированной. ScrollChanged приходит на
/// ЛЮБОЕ изменение прокрутки: колесо/полоса/клавиши пользователя, ScrollTo*, рост
/// extent от новых пузырей, стрим-дельт, загрузки картинок и трим сверху. Раньше
/// якорь читаемого места переснимался на каждое событие → строка дрейфовала.
///
/// Правило: якорь переснимаем только когда сам вьюпорт сдвинулся
/// (VerticalChange != 0) и событие не помечено программным ScrollTo*.
/// Признак «это пользователь» — свежий вводный маркер (колесо, полоса прокрутки,
/// клавиши, touch); немаркированный сдвиг без изменения extent/viewport тоже
/// считаем пользовательским (непомеченный ввод), а немаркированный сдвиг вместе
/// с ростом/сжатием extent — layout-индуцированным (контент двигается, вьюпорт
/// цепляется) → якорь НЕ трогаем, LayoutUpdated вернёт его на место.
///
/// Чистая логика без WPF — линкуется в Tests (как OverlayHistoryTrim).
/// </summary>
internal static class OverlayScrollClassifier
{
    /// <summary>Окно, в котором маркер пользовательского ввода / программного
    /// ScrollTo* считается свежим (мс по Environment.TickCount).</summary>
    public const int ScrollMarkWindowMs = 500;

    /// <summary>Это пользовательская прокрутка (за неё надо переснять якорь)?</summary>
    /// <param name="userInputMarked">В последние ScrollMarkWindowMs был ввод над
    /// скроллом: колесо, ScrollBar.Scroll (бегунок/стрелки/дорожка), клавиши
    /// Page/стрелки/Home/End/Space, touch-панорамирование.</param>
    /// <param name="programmaticMarked">Последний ScrollTo*/ScrollToVerticalOffset
    /// был программным (только что вызван или ещё в окне маркера).</param>
    public static bool IsUserScroll(bool userInputMarked, bool programmaticMarked,
        double verticalChange, double extentHeightChange, double viewportHeightChange)
    {
        if (programmaticMarked) return false;
        if (verticalChange == 0.0) return false;      // только extent/viewport — layout
        if (userInputMarked) return true;              // ввод + сдвиг вьюпорта = пользователь
        // Немаркированный сдвиг при неизменном extent — почти наверняка
        // пользовательский (непомеченный ввод): принимаем, иначе восстановление
        // якоря «съест» прокрутку. Сдвиг вместе с ростом extent — layout.
        return extentHeightChange == 0.0 && viewportHeightChange == 0.0;
    }

    /// <summary>Маркер свежий? Подписанная разность TickCount корректна и при
    /// переполнении счётчика (~24,8 суток).</summary>
    public static bool IsMarkFresh(int markTick, int nowTick) =>
        unchecked(nowTick - markTick) >= 0
        && unchecked(nowTick - markTick) < ScrollMarkWindowMs;
}
