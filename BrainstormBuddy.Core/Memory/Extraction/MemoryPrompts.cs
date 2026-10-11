namespace BrainstormBuddy.Memory.Extraction;

/// <summary>
/// Versioned system-prompts для memory extraction (спека: PROMPTS хранятся
/// как versioned resources — bump <see cref="PromptVersion"/> при изменении
/// схемы/инструкций, чтобы можно было мигрировать старые результаты).
/// </summary>
public static class MemoryPrompts
{
    /// <summary>Версия промпта/JSON-схемы. Сохранять в metadata job/result.</summary>
    public const int PromptVersion = 1;

    /// <summary>
    /// Извлечение: краткое саммари встречи + массив memories по строгой JSON-схеме.
    /// Фильтрация «мусора» (привет/спасибо/слышно) — на стороне LLM через этот
    /// system-prompt: extractor отдаёт транскрипт ЦЕЛИКОМ, без локальной фильтрации.
    /// </summary>
    public const string Extract =
        "Ты — модуль памяти ассистента. По транскрипту встречи извлеки краткое саммари " +
        "и долговременные факты (memories). Ответь ТОЛЬКО валидным JSON по схеме ниже — " +
        "без markdown, без пояснений, без обрамления в ```.\n\n" +
        "СХЕМА ОТВЕТА:\n" +
        "{\n" +
        "  \"summary\": \"краткое саммари встречи, 2-4 предложения, по-русски\",\n" +
        "  \"memories\": [\n" +
        "    {\n" +
        "      \"type\": \"decision|action_item|deadline|open_question|risk|idea|preference|" +
        "constraint|correction|person|project|fact\",\n" +
        "      \"content\": \"одно конкретное утверждение\",\n" +
        "      \"importance\": 0.0-1.0,\n" +
        "      \"confidence\": 0.0-1.0,\n" +
        "      \"speakerId\": \"S1 (если понятно, кто сказал/принял, иначе null)\",\n" +
        "      \"sourceStartMs\": 0,\n" +
        "      \"sourceEndMs\": 0\n" +
        "    }\n" +
        "  ]\n" +
        "}\n\n" +
        "ПРАВИЛА:\n" +
        "1. Сохраняй ТОЛЬКО информацию, явно поддержанную транскриптом. Не додумывай факты, " +
        "имена, цифры, даты. Если информации недостаточно — не создавай memory.\n" +
        "2. Для каждого item ОБЯЗАТЕЛЬНО укажи source interval: sourceStartMs/sourceEndMs " +
        "в миллисекундах. В транскрипте реплики помечены [S1 00:12:03] (hh:mm:ss) — " +
        "интервал должен накрывать реплики, где этот факт прозвучал.\n" +
        "3. НЕ сохраняй мусор: приветствия, благодарности, «слышно?», «секунду», «я вернулся», " +
        "проверки связи, перебивания и прочий filler — они не полезны в будущем разговоре.\n" +
        "4. Полезные memories: decision, commitment, deadline, constraint, устойчивая " +
        "preference, project/person fact, responsibility, open question, risk, идея, " +
        "коррекция ранее сказанного.\n" +
        "5. importance — насколько факт важен в долгосрочной перспективе; " +
        "confidence — насколько транскрипт однозначно его подтверждает.\n" +
        "6. Каждый content — атомарное самодостаточное утверждение, понятное без контекста " +
        "встречи (вместо «он» — роль/имя, если известно).\n" +
        "7. Если извлекать нечего — верни пустой массив memories, summary всё равно заполни.";

    /// <summary>
    /// Repair-промпт: модель вернула broken JSON — просим вернуть только валидный JSON
    /// по той же схеме (исходный текст ответа передаётся user-сообщением).
    /// </summary>
    public const string Repair =
        "Ответ на прошлое сообщение должен был быть JSON по схеме extraction " +
        "(поля \"summary\" и \"memories\": type, content, importance, confidence, " +
        "speakerId, sourceStartMs, sourceEndMs), но он сломан. " +
        "Исправь его и верни ТОЛЬКО валидный JSON по той же схеме — без markdown, " +
        "без пояснений, без обрамления в ```. Не меняй смысл и не добавляй новых фактов.";
}
