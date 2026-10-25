namespace BrainstormBuddy;

/// <summary>
/// Чистая логика тримминга истории оверлея (вынесено для юнит-тестов — WPF не нужен):
/// список режется сверху до targetCap, но элементы от firstProtectedIndex включительно
/// и ниже (якорь/первый видимый) не удаляются НИКОГДА — защита читаемого места
/// сильнее потолка (аудит 2026-10-15, §4: трим удалял читаемый элемент и рвал позицию).
/// hardCap работает только когда защищённых элементов нет (автоскролл вкл):
/// считая targetCap ≤ hardCap, результат без защиты всегда «до targetCap».
/// </summary>
internal static class OverlayHistoryTrim
{
    public static int ComputeHeadRemoveCount(int count, int targetCap, int hardCap, int firstProtectedIndex)
    {
        if (count <= targetCap) return 0;
        // Защита активна: удаляем только выше якоря — список может остаться
        // больше hardCap, пока пользователь читает; доберётся на следующих тримах.
        if (firstProtectedIndex >= 0 && firstProtectedIndex < count)
            return Math.Min(count - targetCap, firstProtectedIndex);
        // Якоря нет (автоскролл вкл): голова режется до целевого потолка;
        // hardCap — страховка при кривой конфигурации (targetCap > hardCap).
        return Math.Max(count - targetCap, count - hardCap);
    }
}
