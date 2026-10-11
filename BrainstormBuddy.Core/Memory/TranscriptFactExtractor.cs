using System.Text;

namespace BrainstormBuddy.Memory;

/// <summary>
/// Извлечение фактов из транскрипта через директивный формат: строит промпт,
/// который просит LLM вернуть ТОЛЬКО [[MEMORY_*]]-директивы, и парсит ответ
/// через <see cref="MemoryDirectiveParser"/>.
/// </summary>
public static class TranscriptFactExtractor
{
    /// <summary>
    /// Системный промпт: транскрипт в конце, дополнительные правила пользователя —
    /// только если заданы (<see cref="Config.MemoryConfig.TranscriptExtractRules"/>).
    /// </summary>
    public static string BuildPrompt(string transcript, string? customRules)
    {
        var sb = new StringBuilder(
            "Ты — модуль памяти ассистента. Извлеки из транскрипта встречи долговременные " +
            "факты и выдай ТОЛЬКО директивы памяти — по одной на факт, без пояснений, " +
            "без markdown, без любого другого текста.\n\n" +
            "ФОРМАТ (одна директива на факт):\n" +
            "[[MEMORY_ADD]]факт[[/MEMORY_ADD]]\n\n" +
            "ФАКТЫ: кто есть кто (роли, имена, должности), принятые решения, дедлайны, " +
            "конкретные цифры и договорённости. Каждый факт — атомарное самодостаточное " +
            "утверждение, понятное без контекста встречи.\n\n" +
            "ПРАВИЛА:\n" +
            "1. Сохраняй ТОЛЬКО информацию, явно прозвучавшую в транскрипте. Не выдумывай " +
            "факты, имена, цифры, даты.\n" +
            "2. Пропускай мусор: приветствия, благодарности, проверки связи и прочий filler.\n" +
            "3. Если извлекать нечего — верни пустой ответ.");

        if (!string.IsNullOrWhiteSpace(customRules))
            sb.Append("\n\nДополнительные правила пользователя:\n").Append(customRules.Trim());

        sb.Append("\n\nТРАНСКРИПТ:\n").Append(transcript);
        return sb.ToString();
    }

    /// <summary>Разобрать ответ LLM в директивы (обёртка над <see cref="MemoryDirectiveParser.Parse"/>).</summary>
    public static IReadOnlyList<MemoryDirective> ParseOutput(string llmOutput)
        => MemoryDirectiveParser.Parse(llmOutput);
}
