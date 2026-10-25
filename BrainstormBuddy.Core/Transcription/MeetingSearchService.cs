using System.Text;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Memory.Embeddings;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Transcription;

/// <summary>Одно совпадение поиска по живым журналам встреч (meetings/*.jsonl).
/// LineIndex = -1 означает совпадение в AI-саммари (sidecar), а не в строке журнала.</summary>
public sealed class MeetingSearchHit
{
    public required string MeetingId { get; init; }
    public string JournalPath { get; init; } = "";
    /// <summary>Старт встречи (из sidecar/имени файла) — для заголовка и сортировки.</summary>
    public DateTime MeetingStartedAt { get; init; }
    /// <summary>Индекс строки в журнале (0-based, как у ReadLines); -1 — саммари.
    /// Устойчивый адрес реплики: пара (MeetingId, LineIndex) и RecordId однозначно
    /// указывают на строку в отрендеренном транскрипте.</summary>
    public int LineIndex { get; init; } = -1;
    /// <summary>Стабильный mid сообщения журнала (MeetingJournalLine.MessageId);
    /// null у старых журналов и у саммари. Для навигации предпочтительнее LineIndex
    /// при наличии — фолбэк на LineIndex/Ts делает вызывающий.</summary>
    public string? RecordId { get; init; }
    /// <summary>Метка времени строки журнала "HH:mm:ss"; пусто для саммари.</summary>
    public string Ts { get; init; } = "";
    /// <summary>Роль строки (question/user/answer); "summary" для совпадения в саммари.</summary>
    public string Role { get; init; } = "";
    /// <summary>Спикер из diarization ("S1"..), если был записан.</summary>
    public string? Speaker { get; init; }
    /// <summary>Фрагмент строки вокруг первого совпадения (… по краям обрезки).</summary>
    public string Snippet { get; init; } = "";
    /// <summary>Полный текст совпавшей строки — для контекстного блока LLM.</summary>
    public string LineText { get; init; } = "";
    /// <summary>Реплика до/после совпадения (контекст для LLM; может быть пустой).</summary>
    public string ContextBefore { get; init; } = "";
    public string ContextAfter { get; init; } = "";
    /// <summary>Скор хита (mut: векторная ветка добавляет бонус к FTS-хитам).</summary>
    public double Score { get; set; }
    /// <summary>Хит найден векторной (смысловой) веткой E5, а не точным FTS-матчем.</summary>
    public bool SemanticMatch { get; init; }
    /// <summary>Косинусная близость к вектору запроса (0, если векторной ветки не было).</summary>
    public double VectorScore { get; set; }

    /// <summary>"14.10 12:31" — короткий заголовок встречи для списков и ссылок.</summary>
    public string MeetingTitle => MeetingStartedAt.ToString("dd.MM HH:mm");

    /// <summary>Кто сказал совпавшую строку: S2 / Вы / Собеседник / ИИ / Резюме.</summary>
    public string SpeakerLabel => Role == "summary"
        ? UiText.Tr("Резюме", "Summary")
        : !string.IsNullOrEmpty(Speaker) ? Speaker! : MeetingTranscriptStore.RoleLabel(Role);

    /// <summary>Подпись кликабельного источника в чате (§8): дата встречи,
    /// время самой реплики (у саммари его нет) и автор — источник однозначен,
    /// не путается с временем старта встречи.</summary>
    public string SourceLabel
    {
        get
        {
            var ts = string.IsNullOrEmpty(Ts) ? "" : " · " + Ts;
            return $"{MeetingTitle}{ts} · {SpeakerLabel}";
        }
    }
}

/// <summary>Параметры поиска: глубина сканирования и размер выдачи.</summary>
public sealed class MeetingSearchOptions
{
    /// <summary>Сколько последних встреч сканировать (свежие сверху). 0 или меньше — все, без скрытого лимита.</summary>
    public int MaxMeetings { get; init; } = 0;
    /// <summary>Сколько совпадений вернуть суммарно.</summary>
    public int MaxResults { get; init; } = 60;
    /// <summary>Сколько лучших строк оставить от каждой встречи.</summary>
    public int MaxHitsPerMeeting { get; init; } = 5;
    /// <summary>Встреча-исключение (текущая — её реплики уже в хвосте чата).</summary>
    public string? ExcludeMeetingId { get; init; }
    /// <summary>Символов в сниппете вокруг первого совпадения.</summary>
    public int SnippetChars { get; init; } = 160;
    /// <summary>Векторная ветка E5 (смысловой поиск для перефразированных вопросов).
    /// false → только точный FTS.</summary>
    public bool IncludeSemantic { get; init; } = true;
    /// <summary>true — если бюджет вызывающего (ct) истёк уже на смысловой ветке,
    /// вернуть собранные точные хиты со статусом Incomplete вместо OCE: таймаут
    /// векторов не должен выбрасывать уже найденные FTS-совпадения. Внешнюю
    /// отмену вызывающий отличает по своему исходному токену.</summary>
    public bool KeepLexicalOnSemanticTimeout { get; init; }
    /// <summary>Нижняя граница даты встречи включительно (локальная дата старта).</summary>
    public DateOnly? DateFrom { get; init; }
    /// <summary>Верхняя граница даты встречи включительно (локальная дата старта).</summary>
    public DateOnly? DateTo { get; init; }
    /// <summary>При дате без совпадений вернуть сами встречи периода (резюме/первые
    /// реплики): модель должна отличить «в этот день встреч не было» от «встречи
    /// были, но точных слов не нашлось».</summary>
    public bool IncludeMeetingsInRange { get; init; }

    /// <summary>Дата старта встречи в локальном часовом поясе (журналы могут
    /// хранить UTC, старые sidecar — Unspecified).</summary>
    public static DateOnly LocalDate(DateTime startedAt)
        => DateOnly.FromDateTime(startedAt.Kind == DateTimeKind.Utc
            ? startedAt.ToLocalTime() : startedAt);

    public bool MatchesDate(DateTime startedAt)
    {
        var d = LocalDate(startedAt);
        return (DateFrom == null || d >= DateFrom.Value)
            && (DateTo == null || d <= DateTo.Value);
    }

    public MeetingSearchOptions WithDate(DateOnly? from, DateOnly? to, bool includeInRange) => new()
    {
        MaxMeetings = MaxMeetings,
        MaxResults = MaxResults,
        MaxHitsPerMeeting = MaxHitsPerMeeting,
        ExcludeMeetingId = ExcludeMeetingId,
        SnippetChars = SnippetChars,
        IncludeSemantic = IncludeSemantic,
        KeepLexicalOnSemanticTimeout = KeepLexicalOnSemanticTimeout,
        DateFrom = from,
        DateTo = to,
        IncludeMeetingsInRange = includeInRange,
    };
}

/// <summary>Снимок готовности поискового индекса встреч (TASK §8).</summary>
public sealed class MeetingSearchStatus
{
    /// <summary>Локальный индекс создан и FTS5 доступен (false → файловый скан).</summary>
    public bool IndexEnabled { get; init; }
    /// <summary>Идёт инкрементальный sync (перечитывание изменённых журналов).</summary>
    public bool Syncing { get; init; }
    /// <summary>Индекс согласован с каталогом meetings/ на момент последнего sync.</summary>
    public bool Synced { get; init; }
    /// <summary>Провайдер эмбеддингов доступен — смысловая ветка может работать.</summary>
    public bool SemanticAvailable { get; init; }
    /// <summary>Векторная обвязка ещё строится (встречи без векторов остаются).
    /// Точный поиск при этом уже отвечает — показывай как «строится», а не как сбой.</summary>
    public bool SemanticPending { get; init; }
    /// <summary>Встреч, ждущих векторов для текущей модели.</summary>
    public int PendingVecMeetings { get; init; }
    /// <summary>Встреч/документов в индексе.</summary>
    public int IndexedMeetings { get; init; }
    public int IndexedDocs { get; init; }
    /// <summary>Смысловая ветка прервана бюджетом запроса после точного поиска:
    /// вернулись только точные/FTS-хиты, перефразы могли не успеть.</summary>
    public bool SemanticTimedOut { get; init; }
    /// <summary>Sync начат, но не завершён: выдача может не покрывать самые свежие записи.</summary>
    public bool Incomplete => IndexEnabled && (Syncing || SemanticTimedOut);
}

/// <summary>Результат поиска с явным статусом — «ничего не найдено» допустимо
/// только при Completed (исключение/таймаут = поиск не состоялся, а не «нет совпадений»).</summary>
public sealed class MeetingSearchResult
{
    public required IReadOnlyList<MeetingSearchHit> Hits { get; init; }
    public required MeetingSearchStatus Status { get; init; }
    /// <summary>Хиты — это встречи выбранного периода при отсутствии совпадений
    /// по запросу, а не подтверждение слов пользователя.</summary>
    public bool DateFallback { get; init; }
    /// <summary>Применённый фильтр даты (для честной подписи в ответе инструмента).</summary>
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
}

/// <summary>
/// Поиск по прошлым встречам (TASK §7, §8): локальный SQLite FTS5-индекс
/// (meetings-index.db рядом с meetings/) для точных слов + векторная ветка на
/// существующем E5-провайдере для перефразированных вопросов; совместное
/// ранжирование — тот же ScoreLine, что и раньше, + косинусный бонус.
///
/// Индекс инкрементальный: каждый запрос синкает изменённые/удалённые журналы
/// по отпечатку файла (sync — singleflight, отмена запроса не обрывает его).
/// RebuildAsync пересоздаёт индекс из журналов. Пока вектора строятся (фоновый
/// job после AttachEmbeddingProvider), точный поиск уже отвечает — статус
/// честно это показывает. При недоступности индекса/FTS5 — прежний построчный
/// скан файлов (те же MeetingSearchHit). Отмена → OperationCanceledException.
/// </summary>
public sealed class MeetingSearchService : IDisposable
{
    private const int MaxQueryTerms = 8;
    /// <summary>Мета-слова поиска по встречам («в истории встреч обсуждали»):
    /// они описывают сам поиск, а не его тему. Без фильтра длинный вопрос
    /// состоит из них, и редкая сущность («waterfall») проигрывает строкам,
    /// совпавшим с пятью служебными словами. Если запрос состоит только из
    /// таких слов, QueryText возвращает исходный набор (поведение прежнее).</summary>
    private static readonly HashSet<string> MeetingScaffolding = new(StringComparer.Ordinal)
    {
        "встреча", "встречи", "встреч", "встрече", "встречу", "встречам",
        "встречах", "встречами", "встречей",
        "история", "истории", "историю", "историей", "историях", "историям",
        "обсуждали", "обсудили", "обсуждать", "обсуждаем", "обсуждение",
        "говорили", "говорим", "говорил", "говорила", "сказал", "сказала",
        "сказали", "сказать", "упоминал", "упоминали", "упоминание",
        "запись", "записи", "записей", "записям", "записях",
        "транскрипт", "транскрипты", "транскриптов", "транскриптах",
        "прошлая", "прошлой", "прошлые", "прошлых", "прошлым",
        "предыдущая", "предыдущей", "предыдущие", "предыдущих",
        "созвон", "созвоны", "созвоне", "созвонах", "созвона",
        "митинг", "митинге", "митингах", "митинга",
        "память", "памяти", "памятью", "доступ", "доступа", "доступе", "доступом",
        "meeting", "meetings", "transcript", "transcripts", "history",
    };
    /// <summary>Верх кандидатов из FTS до перескоринга ScoreLine.</summary>
    private const int FtsCandidateMin = 400;
    /// <summary>Косинус: бонус к FTS-хиту от векторной ветки (слабее — шум E5).</summary>
    private const double VecBonusThreshold = 0.35;
    /// <summary>Косинус: порог для хита БЕЗ текстового совпадения (перефраз).</summary>
    private const double VecOnlyThreshold = 0.50;
    /// <summary>Абсолютный пол косинуса для смыслового хита: E5 на коротких
    /// русских репликах даёт 0.6–0.8 даже случайным парам, поэтому 0.50
    /// пропускает шум. Ниже этого порога считаем совпадение отсутствующим.</summary>
    private const double VecOnlyMinCosine = 0.80;
    /// <summary>Смысловой хит должен заметно выделяться на фоне выдачи: его
    /// косинус выше медианы всех реплик не меньше чем на эту величину. Иначе
    /// запрос без ответа («неттакойстроки_zz») возвращал нейтральные реплики
    /// с косинусом 0.82–0.84. Калибровка на реальном E5: отсутствующий запрос
    /// даёт отрыв всего 0.039 (0.845 против медианы 0.806), реальный перефраз —
    /// 0.053–0.066. 0.02 пропускал шум; 0.045 разделяет эти распределения.</summary>
    private const double SemanticMedianMargin = 0.045;
    /// <summary>Максимум смысловых хитов без текстового совпадения. E5 даёт высокий
    /// косинус многим нейтральным репликам; без лимита они вытесняют точные
    /// совпадения и забивают выдачу однотипными строками.</summary>
    private const int SemanticOnlyMax = 24;
    /// <summary>Сколько поиск готов ждать embed-запроса при холодной модели
    /// (как ColdEmbedWait в MemoryRecallService): FTS не блокируем, модель
    /// догревается — следующий запрос получит векторную ветку.</summary>
    private static readonly TimeSpan ColdEmbedWait = TimeSpan.FromMilliseconds(800);
    private const int VecBatchSize = 64;
    private const string LogCat = "Memory";

    private readonly MeetingTranscriptStore _store;
    private readonly MeetingSearchIndex? _index;
    private readonly LoggingService? _log;
    private Func<IMemoryEmbeddingProvider?>? _embeddingProvider;

    // Sync — singleflight: конкурирующие запросы ждут один проход, а не дублируют IO.
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private Task? _syncTask;
    private volatile bool _syncInFlight;
    private bool _syncedOnce;

    // Фоновая векторизация — один job на сервис (второму некуда писать: без
    // attach провайдера сервис вообще не запускает векторную обвязку).
    private readonly object _vecGate = new();
    private Task? _vecJob;
    private readonly CancellationTokenSource _vecCts = new();
    private bool _disposed;

    // Кэш векторов по (tag, stamp) — не перечитываем БЛОБы на каждый запрос.
    private string? _vecTag;
    private (long Count, long MaxId) _vecStamp;
    private List<(MeetingSearchIndex.Doc D, float[] V)>? _vecCache;

    public MeetingSearchService(MeetingTranscriptStore store) : this(store, true) { }

    /// <summary>useIndex=false — чистый файловый скан (тесты/редкие сценарии).</summary>
    public MeetingSearchService(MeetingTranscriptStore store, bool useIndex,
        LoggingService? log = null)
    {
        _store = store;
        _log = log;
        if (!useIndex) return;
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(store.Directory));
            var idx = new MeetingSearchIndex(
                Path.Combine(parent ?? ".", "meetings-index.db"), log);
            if (idx.FtsAvailable) _index = idx;
            else idx.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Warn($"MeetingSearch: index unavailable, file scan only: {ex.Message}", LogCat);
        }
    }

    /// <summary>Явный путь индекса (тесты/миграции).</summary>
    public MeetingSearchService(MeetingTranscriptStore store, string indexDbPath,
        LoggingService? log = null)
    {
        _store = store;
        _log = log;
        try
        {
            var idx = new MeetingSearchIndex(indexDbPath, log);
            if (idx.FtsAvailable) _index = idx;
            else idx.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Warn($"MeetingSearch: index unavailable, file scan only: {ex.Message}", LogCat);
        }
    }

    public MeetingSearchService(string appDataDir) : this(new MeetingTranscriptStore(appDataDir)) { }

    /// <summary>Подключить E5-провайдер памяти (лениво — он создаётся позже инициализации
    /// журнала). Включает векторную ветку поиска и фоновую обвязку индекса.</summary>
    public void AttachEmbeddingProvider(Func<IMemoryEmbeddingProvider?> provider)
    {
        _embeddingProvider = provider;
        KickVecJob();
    }

    /// <summary>Текущая готовность индекса — для статуса «строится» в UI/LLM.
    /// semanticTimedOut — смысловая ветка этого запроса не успела в бюджет.</summary>
    public MeetingSearchStatus GetStatus(bool semanticTimedOut = false)
    {
        var prov = _embeddingProvider?.Invoke();
        var semantic = prov?.IsAvailable == true;
        var pending = 0;
        if (_index != null && semantic)
        {
            try { pending = _index.CountPendingVecMeetings(prov!.EmbeddingTag); }
            catch { }
        }
        var (m, d) = _index?.Stats() ?? (0, 0);
        return new MeetingSearchStatus
        {
            IndexEnabled = _index != null,
            Syncing = _syncInFlight,
            Synced = _syncedOnce && !_syncInFlight,
            SemanticAvailable = semantic,
            SemanticPending = semantic && (pending > 0 || VecJobRunning),
            SemanticTimedOut = semanticTimedOut,
            PendingVecMeetings = pending,
            IndexedMeetings = m,
            IndexedDocs = d,
        };
    }

    private bool VecJobRunning => _vecJob is { IsCompleted: false };

    /// <summary>Дождаться завершения sync и фоновой векторизации (smoke/диагностика).</summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        // AttachEmbeddingProvider может быть вызван до первого поиска. В этом
        // случае старый вариант ждал только уже созданные задачи и возвращал
        // управление до первичного sync, поэтому «ожидание простоя» не было
        // барьером для детерминированных semantic-проверок.
        await EnsureSyncedAsync(ct).ConfigureAwait(false);
        var v = _vecJob;
        if (v != null)
            await v.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Полный rebuild индекса из исходных журналов (§8), затем свежий sync.</summary>
    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        if (_index == null) return;
        _index.Rebuild();
        await EnsureSyncedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Найти строки журналов и саммари, совпадающие с запросом.
    /// Отменяется через ct; с KeepLexicalOnSemanticTimeout уже найденные точные
    /// хиты сохраняются, а смысловая ветка помечается неполной.</summary>
    public async Task<IReadOnlyList<MeetingSearchHit>> SearchAsync(
        string query, CancellationToken ct, MeetingSearchOptions? options = null)
        => (await SearchDetailedAsync(query, ct, options).ConfigureAwait(false)).Hits;

    /// <summary>Поиск с явным статусом индекса (§8): вызывающий отличает
    /// «не найдено» от «индекс ещё строится/поиск не завершён».</summary>
    public async Task<MeetingSearchResult> SearchDetailedAsync(
        string query, CancellationToken ct, MeetingSearchOptions? options = null)
    {
        var opt = options ?? new MeetingSearchOptions();
        // [secret]/[screen] — метка канала, не содержание вопроса.
        query = QueryText.StripServiceMarkers(query ?? "");

        // Дата в свободном вопросе («что обсуждали вчера», «22 октября») —
        // это фильтр встречи, а не термин внутри транскрипта. Явные DateFrom/
        // DateTo от инструмента сильнее извлечённой из текста даты.
        if (opt.DateFrom == null && opt.DateTo == null)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (MeetingDateQuery.TryExtractPeriod(query, today,
                    out var inferredFrom, out var inferredTo, out var cleaned))
            {
                opt = opt.WithDate(inferredFrom, inferredTo, includeInRange: true);
                query = cleaned;
            }
        }
        var hasDate = opt.DateFrom != null || opt.DateTo != null;
        if (string.IsNullOrWhiteSpace(query) && !hasDate)
            return Empty();

        var phrase = query.Trim().ToLowerInvariant();
        var terms = SelectTerms(query, allowScaffoldingFallback: !hasDate);
        if (terms.Count == 0 && !hasDate)
            return Empty();

        var dateFallback = false;
        IReadOnlyList<MeetingSearchHit> hits;
        bool semanticTimedOut = false;
        if (_index != null)
        {
            (hits, semanticTimedOut) = await SearchIndexedAsync(phrase, terms, opt, ct)
                .ConfigureAwait(false);
        }
        else if (terms.Count > 0)
        {
            hits = await SearchFilesAsync(phrase, terms, opt, ct).ConfigureAwait(false);
        }
        else
        {
            hits = Array.Empty<MeetingSearchHit>();
        }

        if (terms.Count == 0 || (hits.Count == 0 && hasDate && opt.IncludeMeetingsInRange))
        {
            var dateHits = await BuildDateRangeHitsAsync(opt, ct).ConfigureAwait(false);
            if (dateHits.Count > 0)
            {
                hits = dateHits;
                // Чистый запрос по дате — это штатный листинг периода, а не
                // замена отсутствующих точных совпадений. Fallback оставляем
                // только для содержательного запроса, чтобы модель не считала
                // листинг доказательством темы.
                dateFallback = terms.Count > 0;
            }
        }

        return new MeetingSearchResult
        {
            Hits = hits,
            Status = GetStatus(semanticTimedOut),
            DateFallback = dateFallback,
            DateFrom = opt.DateFrom,
            DateTo = opt.DateTo,
        };

        MeetingSearchResult Empty() => new()
        {
            Hits = Array.Empty<MeetingSearchHit>(),
            Status = GetStatus(),
            DateFrom = opt.DateFrom,
            DateTo = opt.DateTo,
        };
    }

    // ---------------- путь индекса ----------------

    private async Task EnsureSyncedAsync(CancellationToken ct)
    {
        if (_index == null) return;
        Task? flight;
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_syncTask is not { IsCompleted: false })
                _syncTask = SyncCoreAsync();
            flight = _syncTask;
        }
        finally { _syncGate.Release(); }
        // Ждём общий пролёт: отмена ЭТОГО запроса не должна убивать sync —
        // следующий запрос получит уже согласованный индекс.
        await flight.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task SyncCoreAsync()
    {
        _syncInFlight = true;
        try
        {
            var meetings = await _store.LoadAllAsync(_vecCts.Token).ConfigureAwait(false);
            var changed = await _index!.SyncAsync(meetings,
                (m, c) => _store.ReadLinesAsync(m.JournalPath, c), _vecCts.Token)
                .ConfigureAwait(false);
            if (changed > 0)
                _log?.Debug($"MeetingIndex: synced {changed} meetings", LogCat);
            _syncedOnce = true;
            KickVecJob(); // свежие документы ждут векторов
        }
        catch (OperationCanceledException) { /* dispose/выключение — повторим при след. запросе */ }
        catch (Exception ex)
        {
            _log?.Warn($"MeetingIndex sync failed: {ex.Message}", LogCat);
        }
        finally
        {
            _syncInFlight = false;
        }
    }

    private async Task<(IReadOnlyList<MeetingSearchHit> Hits, bool SemanticTimedOut)>
        SearchIndexedAsync(
        string phrase, List<string> terms, MeetingSearchOptions opt, CancellationToken ct)
    {
        await EnsureSyncedAsync(ct).ConfigureAwait(false);

        // Ограничение глубины/даты: сначала фильтр даты, затем свежие N встреч
        // (LoadAll отдаёт свежие первыми). Без фильтров allowed = null — все.
        HashSet<string>? allowed = null;
        if (opt.MaxMeetings > 0 || opt.DateFrom != null || opt.DateTo != null)
        {
            var meetings = await _store.LoadAllAsync(ct).ConfigureAwait(false);
            IEnumerable<MeetingInfo> filtered = meetings.Where(m => opt.MatchesDate(m.StartedAt));
            if (opt.MaxMeetings > 0) filtered = filtered.Take(opt.MaxMeetings);
            allowed = new HashSet<string>(
                filtered.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        }

        // FTS5 prefix-матчи по токенам («дед» → «дедлайн»); термины уже
        // отсанитизированы Tokenize'ем (буквы/цифры, ≥2) — MATCH-синтаксис безопасен.
        // Запрос только по дате: пустой MATCH — синтаксическая ошибка SQLite,
        // поэтому FTS и смысловую ветку пропускаем, ниже сработает листинг периода.
        var candLimit = Math.Max(FtsCandidateMin, opt.MaxResults * 4);
        var docs = terms.Count == 0
            ? new List<MeetingSearchIndex.Doc>()
            : _index!.SearchFts(string.Join(" OR ", terms.Select(t => t + "*")),
                candLimit, opt.ExcludeMeetingId);

        var byMeeting = new Dictionary<string, List<(MeetingSearchIndex.Doc D, double Score, int Pos)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var d in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (allowed != null && !allowed.Contains(d.MeetingId)) continue;
            // Ответ ассистента «подтверждения нет» — не факт встречи, а прежняя
            // неудачная выдача. Если индексировать его как обычную строку, при
            // повторе вопроса он всплывает первым и модель повторяет «данных
            // нет», хотя рядом лежат реальные ответы. (Регрессия 19.10.)
            if (IsNegativeAssistantAnswer(d.Role, d.Text)) continue;
            var s = ScoreLine(d.Text.ToLowerInvariant(), phrase, terms, out var pos);
            if (s <= 0) continue; // prefix-матч FTS, но не совпадение по нашей шкале
            if (!byMeeting.TryGetValue(d.MeetingId, out var l))
                byMeeting[d.MeetingId] = l = new List<(MeetingSearchIndex.Doc, double, int)>();
            l.Add((d, s, pos));
        }

        var hits = new List<MeetingSearchHit>();
        foreach (var kv in byMeeting)
        {
            foreach (var (d, score, pos) in kv.Value
                .OrderByDescending(x => x.Score).Take(opt.MaxHitsPerMeeting).OrderBy(x => x.D.LineIdx))
                hits.Add(DocToHit(d, score, pos, opt));
        }

        // Векторная ветка: перефразы и смысловые совпадения. Не блокирует точный
        // поиск — холодная модель догревается в фоне, ветка просто пропускается.
        // Бюджет вызывающего может истечь уже здесь (вектора медленнее FTS):
        // найденные точные хиты не выбрасываем — отдаём их как неполную выдачу.
        var semanticTimedOut = false;
        if (opt.IncludeSemantic)
        {
            try
            {
                await MergeVectorHitsAsync(phrase, hits, allowed, opt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (opt.KeepLexicalOnSemanticTimeout && ct.IsCancellationRequested)
            {
                semanticTimedOut = true;
            }
        }

        return (RankHits(hits, opt), semanticTimedOut);
    }

    private MeetingSearchHit DocToHit(MeetingSearchIndex.Doc d, double score, int pos,
        MeetingSearchOptions opt)
    {
        var (before, after) = _index!.GetContext(d.MeetingId, d.LineIdx);
        return new MeetingSearchHit
        {
            MeetingId = d.MeetingId,
            JournalPath = d.JournalPath,
            MeetingStartedAt = d.StartedAt,
            LineIndex = d.LineIdx,
            RecordId = d.Mid,
            Ts = d.Ts,
            Role = d.Role,
            Speaker = d.Speaker,
            LineText = d.Text,
            Snippet = MakeSnippet(d.Text, pos, opt.SnippetChars),
            ContextBefore = before,
            ContextAfter = after,
            Score = score,
        };
    }

    /// <summary>Векторная ветка: embed запроса (bounded wait) + косинус по кэшу
    /// векторов индекса. Совпавшие с FTS хиты получают бонус; чисто смысловые
    /// добавляются отдельными хитами (SemanticMatch=true).</summary>
    private async Task MergeVectorHitsAsync(string query,
        List<MeetingSearchHit> hits, HashSet<string>? allowed,
        MeetingSearchOptions opt, CancellationToken ct)
    {
        var prov = _embeddingProvider?.Invoke();
        if (prov == null || !prov.IsAvailable || _index == null) return;
        var tag = prov.EmbeddingTag;

        var embedTask = prov.EmbedAsync(query, isQuery: true, ct);
        float[]? qv;
        if (opt.KeepLexicalOnSemanticTimeout)
        {
            // Контекст чата ждёт смысловую ветку до заданного пользователем срока,
            // а не до прежнего внутреннего лимита 800 мс. WaitAsync ограничивает
            // ожидание даже у провайдера, который не поддерживает отмену.
            qv = await embedTask.WaitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var done = await Task.WhenAny(embedTask, Task.Delay(ColdEmbedWait, ct))
                .ConfigureAwait(false);
            if (done != embedTask)
            {
                ct.ThrowIfCancellationRequested();
                return; // холодная модель догреется в фоне
            }
            qv = await embedTask.ConfigureAwait(false);
        }
        if (qv == null) return;

        var vecs = GetVecCache(tag);
        if (vecs.Count == 0) return;

        var byKey = hits.ToDictionary(
            h => (h.MeetingId, h.LineIndex), h => h);
        var semanticOnly = new List<(MeetingSearchIndex.Doc D, double Cos)>();
        var allScores = new List<double>(vecs.Count);
        foreach (var (d, v) in vecs)
        {
            ct.ThrowIfCancellationRequested();
            if (allowed != null && !allowed.Contains(d.MeetingId)) continue;
            if (opt.ExcludeMeetingId != null && string.Equals(
                    d.MeetingId, opt.ExcludeMeetingId, StringComparison.OrdinalIgnoreCase))
                continue;
            var cos = VectorMath.CosineSimilarity(qv, v);
            allScores.Add(cos);
            if (cos < VecBonusThreshold) continue;
            if (byKey.TryGetValue((d.MeetingId, d.LineIdx), out var existing))
            {
                existing.VectorScore = Math.Max(existing.VectorScore, cos);
                existing.Score += Math.Max(0.0, cos - VecBonusThreshold) * 20.0;
                continue;
            }
            if (cos < VecOnlyThreshold || cos < VecOnlyMinCosine) continue;
            semanticOnly.Add((d, cos));
        }

        // Смысловые хиты без текстового совпадения — только те, что заметно
        // выделяются на фоне всех реплик, и только лучшие по косинусу.
        // E5 выдаёт 0.82–0.84 даже на запрос, которого нет в записях; медиана
        // отделяет реальный перефраз (верхний выброс) от ровного шума.
        if (semanticOnly.Count > 0)
        {
            allScores.Sort();
            var mid = allScores.Count / 2;
            var median = allScores.Count == 0 ? 0.0
                : allScores.Count % 2 == 0
                    ? (allScores[mid - 1] + allScores[mid]) / 2.0
                    : allScores[mid];
            // На одном-двух длинных документах медиана совпадает с единственным
            // кандидатом; относительный порог там ничего не значит, поэтому
            // малый набор решает абсолютный пол.
            var useMedian = allScores.Count >= 3;
            foreach (var (d, cos) in semanticOnly
                .Where(x => useMedian
                    ? x.Cos - median >= SemanticMedianMargin
                    : x.Cos >= VecOnlyMinCosine)
                .OrderByDescending(x => x.Cos)
                .Take(SemanticOnlyMax))
            {
                var (before, after) = _index.GetContext(d.MeetingId, d.LineIdx);
                hits.Add(new MeetingSearchHit
                {
                    MeetingId = d.MeetingId,
                    JournalPath = d.JournalPath,
                    MeetingStartedAt = d.StartedAt,
                    LineIndex = d.LineIdx,
                    RecordId = d.Mid,
                    Ts = d.Ts,
                    Role = d.Role,
                    Speaker = d.Speaker,
                    LineText = d.Text,
                    Snippet = MakeSnippet(d.Text, 0, opt.SnippetChars),
                    ContextBefore = before,
                    ContextAfter = after,
                    Score = cos * 24.0, // шкала сопоставима с фразовым бонусом ScoreLine
                    SemanticMatch = true,
                    VectorScore = cos,
                });
            }
        }
    }

    private List<(MeetingSearchIndex.Doc D, float[] V)> GetVecCache(string tag)
    {
        var stamp = _index!.VecStamp(tag);
        if (_vecCache != null && _vecTag == tag && _vecStamp == stamp)
            return _vecCache;
        _vecCache = _index.LoadVecs(tag);
        _vecTag = tag;
        _vecStamp = stamp;
        return _vecCache;
    }

    /// <summary>Фоновая векторизация документов без векторов текущей модели.
    /// Точный поиск при этом уже работает; SemanticPending честно показывает прогресс.</summary>
    private void KickVecJob()
    {
        if (_index == null) return;
        lock (_vecGate)
        {
            if (VecJobRunning || _disposed) return;
            var prov = _embeddingProvider?.Invoke();
            if (prov?.IsAvailable != true) return;
            _vecJob = Task.Run(VecJobAsync);
        }
    }

    private async Task VecJobAsync()
    {
        var ct = _vecCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var prov = _embeddingProvider?.Invoke();
                if (prov?.IsAvailable != true) return;
                var tag = prov.EmbeddingTag;

                // Пакет за пакетом: домечиваем встречи у которых достойных
                // документов больше нет — так прогресс виден и частично.
                var docs = _index!.DocsNeedingVec(tag, VecBatchSize);
                if (docs.Count == 0)
                {
                    if (_index.CountPendingVecMeetings(tag) == 0) return;
                    await Task.Delay(400, ct).ConfigureAwait(false);
                    continue; // состояние vec_tag догонит SaveVecs следующего батча
                }
                var vecs = await prov.EmbedBatchAsync(
                    docs.Select(d => d.Text).ToList(), isQuery: false, ct).ConfigureAwait(false);
                var pairs = new List<(long DocId, float[] Vec)>();
                for (var i = 0; i < docs.Count; i++)
                    if (vecs[i] is { } v) pairs.Add((docs[i].Id, v));
                _index.SaveVecs(pairs, tag);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn($"MeetingIndex vec job failed: {ex.Message}", LogCat); }
    }

    // ---------------- файловый скан (fallback / useIndex=false) ----------------

    private async Task<IReadOnlyList<MeetingSearchHit>> SearchFilesAsync(
        string phrase, List<string> terms, MeetingSearchOptions opt, CancellationToken ct)
    {
        List<MeetingInfo> meetings;
        try { meetings = await _store.LoadAllAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<MeetingSearchHit>(); }

        var hits = new List<MeetingSearchHit>();
        var scanned = 0;
        foreach (var m in meetings)
        {
            ct.ThrowIfCancellationRequested();
            if (opt.MaxMeetings > 0 && scanned >= opt.MaxMeetings) break;
            if (opt.ExcludeMeetingId != null
                && string.Equals(m.Id, opt.ExcludeMeetingId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!opt.MatchesDate(m.StartedAt)) continue;
            scanned++;
            hits.AddRange(await ScanMeetingAsync(m, phrase, terms, opt, ct).ConfigureAwait(false));
        }
        return RankHits(hits, opt);
    }

    /// <summary>Встречи выбранного периода, когда совпадений по словам нет:
    /// по одной карточке на встречу (резюме, иначе первые содержательные реплики).
    /// Это НЕ подтверждение запроса — вызывающий обязан пометить DateFallback.</summary>
    private async Task<List<MeetingSearchHit>> BuildDateRangeHitsAsync(
        MeetingSearchOptions opt, CancellationToken ct)
    {
        if (opt.DateFrom == null && opt.DateTo == null)
            return new List<MeetingSearchHit>();
        List<MeetingInfo> meetings;
        try { meetings = await _store.LoadAllAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return new List<MeetingSearchHit>(); }

        var hits = new List<MeetingSearchHit>();
        foreach (var m in meetings)
        {
            ct.ThrowIfCancellationRequested();
            if (!opt.MatchesDate(m.StartedAt)) continue;
            if (opt.ExcludeMeetingId != null
                && string.Equals(m.Id, opt.ExcludeMeetingId, StringComparison.OrdinalIgnoreCase))
                continue;

            var summary = m.Summary?.Trim() ?? "";
            if (summary.Length > 0)
            {
                hits.Add(new MeetingSearchHit
                {
                    MeetingId = m.Id,
                    JournalPath = m.JournalPath,
                    MeetingStartedAt = m.StartedAt,
                    LineIndex = -1,
                    Role = "summary",
                    LineText = summary,
                    Snippet = MakeSnippet(summary, 0, opt.SnippetChars),
                    Score = 1,
                });
            }

            // Даже при наличии резюме даём несколько дословных реплик: на вопрос
            // «процитируй» модель обязана опереться на слова участников, а не
            // пересказать AI-сводку. Без резюме — первые содержательные строки.
            IReadOnlyList<MeetingJournalLine> lines;
            try { lines = await _store.ReadLinesAsync(m.JournalPath, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { lines = Array.Empty<MeetingJournalLine>(); }
            var picked = 0;
            // Резюме (если есть) + три дословные строки: вопрос «процитируй»
            // должен получать не только ответ ИИ, но и следующую реплику участника.
            const int wantLines = 3;
            for (var i = 0; i < lines.Count && picked < wantLines && hits.Count < opt.MaxResults; i++)
            {
                var l = lines[i];
                if (string.IsNullOrWhiteSpace(l.Text)) continue;
                if (IsNegativeAssistantAnswer(l.Role, l.Text)) continue;
                hits.Add(new MeetingSearchHit
                {
                    MeetingId = m.Id,
                    JournalPath = m.JournalPath,
                    MeetingStartedAt = m.StartedAt,
                    LineIndex = i,
                    RecordId = l.MessageId,
                    Ts = l.Ts ?? "",
                    Role = l.Role ?? "",
                    Speaker = l.Speaker,
                    LineText = l.Text ?? "",
                    Snippet = MakeSnippet(l.Text ?? "", 0, opt.SnippetChars),
                    ContextBefore = i > 0 ? lines[i - 1].Text ?? "" : "",
                    ContextAfter = i + 1 < lines.Count ? lines[i + 1].Text ?? "" : "",
                    Score = 1,
                });
                picked++;
            }
            if (picked == 0 && summary.Length == 0)
            {
                hits.Add(new MeetingSearchHit
                {
                    MeetingId = m.Id,
                    JournalPath = m.JournalPath,
                    MeetingStartedAt = m.StartedAt,
                    LineIndex = -1,
                    Role = "meeting",
                    LineText = UiText.Tr("(встреча без расшифровки)",
                        "(meeting without transcript)"),
                    Score = 1,
                });
            }
            if (hits.Count >= opt.MaxResults) break;
        }
        return hits;
    }

    /// <summary>Термины запроса: нижний регистр, разделители — не буквы/цифры,
    /// длина ≥2, до MaxQueryTerms самых длинных (длинные термины селективнее).</summary>
    public static List<string> Tokenize(string query) =>
        TokenizeWords(query).OrderByDescending(t => t.Length).Take(MaxQueryTerms).ToList();

    /// <summary>Термины для поиска: служебные/поисковые слова отбрасываются
    /// ДО усечения по длине, иначе длинный вопрос вытесняет редкую сущность.
    /// Fallback — если остались только служебные слова, берём весь набор.</summary>
    private static List<string> SelectTerms(string query, bool allowScaffoldingFallback = true)
    {
        var all = TokenizeWords(query);
        var filtered = QueryText.FilterScaffolding(
            all, MeetingScaffolding, fallbackToAll: allowScaffoldingFallback);
        return filtered.OrderByDescending(t => t.Length).Take(MaxQueryTerms).ToList();
    }

    private static List<string> TokenizeWords(string query)
    {
        var terms = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 2)
            {
                var t = sb.ToString();
                if (!terms.Contains(t)) terms.Add(t);
            }
            sb.Clear();
        }
        foreach (var c in query)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else Flush();
        }
        Flush();
        return terms;
    }

    /// <summary>Общая выдача: встреча ранжируется её лучшей строкой + половиной
    /// суммы остальных; при равенстве — свежая выше; внутри встречи — хронология.</summary>
    private static List<MeetingSearchHit> RankHits(List<MeetingSearchHit> hits,
        MeetingSearchOptions opt)
    {
        var groups = hits.GroupBy(h => h.MeetingId).ToList();
        // Точное/префиксное совпадение важнее смыслового: semantic-only не может
        // вытеснить найденную текстом строку. Внутри tier — лучшая строка встречи,
        // остальные строки только тай-брейк: пять нейтральных реплик не должны
        // обгонять одну сильную смысловую.
        var meetingBest = groups.ToDictionary(g => g.Key, g => g.Max(h => h.Score));
        var meetingSupport = groups.ToDictionary(
            g => g.Key, g => g.Sum(h => h.Score) - g.Max(h => h.Score));
        var meetingHasLexical = groups.ToDictionary(
            g => g.Key, g => g.Any(h => !h.SemanticMatch));
        var meetingDate = groups.ToDictionary(g => g.Key, g => g.Max(h => h.MeetingStartedAt));

        return hits
            .OrderByDescending(h => meetingHasLexical[h.MeetingId])
            .ThenByDescending(h => meetingBest[h.MeetingId])
            .ThenByDescending(h => meetingSupport[h.MeetingId])
            .ThenByDescending(h => meetingDate[h.MeetingId])
            .ThenBy(h => h.LineIndex)
            .Take(opt.MaxResults)
            .ToList();
    }

    /// <summary>Ответ ассистента, который сам сообщает «подтверждения нет» /
    /// «не нашлось»: это отрицательный результат прошлого поиска, а не факт.
    /// Хранить его в индексе можно (история), но при повторном поиске он не
    /// должен конкурировать с содержательными строками — иначе модель видит
    /// свой прежний отказ и повторяет его. Роль summary не трогаем: это
    /// сводка встречи, а не ответ на конкретный поисковый вопрос.</summary>
    private static bool IsNegativeAssistantAnswer(string? role, string? text)
    {
        if (!string.Equals(role, "answer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(text))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"подтверждени\w*[^\n.!?]{0,80}(?:\bнет\b|не\s+нашл)" +
            @"|не\s+нашл\w*[^\n.!?]{0,80}подтвержд" +
            @"|\bнет\s+данных\b|\bno\s+(?:confirmation|data|matches)\b|\bnot\s+found\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>Скор одной строки текста: разные термины &gt; повторы; фраза целиком —
    /// сильный бонус. 0 = строка не совпала. firstPos — позиция первого термина.</summary>
    private static double ScoreLine(string lower, string phrase, IReadOnlyList<string> terms, out int firstPos)
    {
        var distinct = 0;
        var occurrences = 0;
        firstPos = -1;
        foreach (var t in terms)
        {
            var count = 0;
            var pos = lower.IndexOf(t, StringComparison.Ordinal);
            while (pos >= 0)
            {
                count++;
                if (firstPos < 0 || pos < firstPos) firstPos = pos;
                pos = lower.IndexOf(t, pos + t.Length, StringComparison.Ordinal);
            }
            if (count > 0) { distinct++; occurrences += count; }
        }
        if (distinct == 0) return 0;
        var score = distinct * 4.0 + Math.Min(occurrences - distinct, 6) * 0.5;
        // Фраза целиком (≥3 симв.) — почти наверняка то, что искали.
        if (phrase.Length >= 3 && lower.Contains(phrase, StringComparison.Ordinal))
            score += 12;
        // Тай-брейк при равных терминах: длинная строка информативнее
        // «согласен»/«ок» (бонус ≤0.4 — меньше одного повтора термина).
        score += Math.Min(lower.Length, 400) * 0.001;
        return score;
    }

    private async Task<List<MeetingSearchHit>> ScanMeetingAsync(
        MeetingInfo m, string phrase, IReadOnlyList<string> terms,
        MeetingSearchOptions opt, CancellationToken ct)
    {
        // (индекс строки, скор, позиция первого термина) — топ-K выбираем после прохода.
        var scored = new List<(int idx, double score, int pos)>();

        var lines = await _store.ReadLinesAsync(m.JournalPath, ct).ConfigureAwait(false);
        for (var i = 0; i < lines.Count; i++)
        {
            if ((i & 63) == 63) ct.ThrowIfCancellationRequested();
            var l = lines[i];
            if (string.IsNullOrEmpty(l.Text)) continue;
            if (IsNegativeAssistantAnswer(l.Role, l.Text)) continue;
            var s = ScoreLine(l.Text.ToLowerInvariant(), phrase, terms, out var pos);
            if (s > 0) scored.Add((i, s, pos));
        }

        // Саммари в sidecar'е — тоже текст встречи, ищем как ещё одну «строку».
        var summaryPos = -1;
        double summaryScore = 0;
        if (!string.IsNullOrEmpty(m.Summary))
            summaryScore = ScoreLine(m.Summary.ToLowerInvariant(), phrase, terms, out summaryPos);

        var hits = new List<MeetingSearchHit>();
        foreach (var (idx, score, pos) in scored
            .OrderByDescending(x => x.score).Take(opt.MaxHitsPerMeeting).OrderBy(x => x.idx))
        {
            var l = lines[idx];
            hits.Add(new MeetingSearchHit
            {
                MeetingId = m.Id,
                JournalPath = m.JournalPath,
                MeetingStartedAt = m.StartedAt,
                LineIndex = idx,
                RecordId = l.MessageId,
                Ts = l.Ts ?? "",
                Role = l.Role ?? "",
                Speaker = l.Speaker,
                LineText = l.Text ?? "",
                Snippet = MakeSnippet(l.Text ?? "", pos, opt.SnippetChars),
                ContextBefore = idx > 0 ? lines[idx - 1].Text ?? "" : "",
                ContextAfter = idx + 1 < lines.Count ? lines[idx + 1].Text ?? "" : "",
                Score = score,
            });
        }
        if (summaryScore > 0)
        {
            hits.Add(new MeetingSearchHit
            {
                MeetingId = m.Id,
                JournalPath = m.JournalPath,
                MeetingStartedAt = m.StartedAt,
                LineIndex = -1,
                Role = "summary",
                LineText = m.Summary!,
                Snippet = MakeSnippet(m.Summary!, summaryPos, opt.SnippetChars),
                Score = summaryScore,
            });
        }
        return hits;
    }

    /// <summary>Сниппет вокруг первого совпадения, пробелы схлопнуты, «…» на срезах.</summary>
    private static string MakeSnippet(string text, int matchPos, int width)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        if (clean.Length <= width) return clean;
        var start = Math.Max(0, Math.Min(matchPos - width / 3, clean.Length - width));
        var snippet = clean.Substring(start, width);
        return (start > 0 ? "…" : "") + snippet + (start + width < clean.Length ? "…" : "");
    }

    /// <summary>
    /// Контекстный блок для системного промпта (&lt;past_meetings&gt;) — аналог
    /// &lt;memory_context&gt; из ContextBlockBuilder, но по журналам встреч.
    /// Каждая строка подписана датой/временем/спикером — модель обязана цитировать
    /// дату; роли различаются (слова участника ≠ ответ ИИ ≠ резюме); правила:
    /// при противоречиях назвать обе даты, без подтверждения — сказать прямо.
    /// Пустой hits + searched=true → блок «совпадений нет» (grounding).
    /// searchIncomplete=true — поиск оборвался (таймаут/отмена/недосинхронизированный
    /// индекс): модель НЕ говорит «обсуждений не было», а сообщает что поиск не
    /// завершён и предлагает повторить. Флаг имеет приоритет над searched:false —
    /// таймаут вызывающего = (searched:false, searchIncomplete:true), и это блок
    /// «не завершён», а не тишина.
    /// semanticPending=true — векторная обвязка ещё строится: точные совпадения
    /// уже учтены, перефразы могут найтись позже; пустой точный результат нельзя
    /// выдавать за «совпадений нет».
    /// </summary>
    public static string BuildMeetingsContextBlock(
        IReadOnlyList<MeetingSearchHit> hits, int maxChars, bool searched,
        bool searchIncomplete = false, bool semanticPending = false,
        DateOnly? dateFrom = null, DateOnly? dateTo = null, bool dateFallback = false)
    {
        var sb = new StringBuilder();
        var period = PeriodLabel(dateFrom, dateTo);
        if (hits.Count == 0)
        {
            if (searchIncomplete)
            {
                sb.Append(UiText.Tr(
                    "<past_meetings/>\nПоиск по прошлым встречам НЕ ЗАВЕРШЁН (таймаут/индекс строится). " +
                    "Не утверждай, что обсуждений не было: скажи, что поиск не завершён, и предложи повторить.",
                    "<past_meetings/>\nPast-meeting search did NOT complete (timeout/index building). " +
                    "Do not claim there were no discussions: say the search did not finish and offer to retry."));
                return sb.ToString();
            }
            if (!searched) return "";
            if (semanticPending)
            {
                sb.Append(UiText.Tr(
                    "<past_meetings/>\nТочный поиск по прошлым встречам выполнен — совпадений нет. " +
                    "Смысловой индекс ещё строится, поэтому перефразированные совпадения могут найтись позже. " +
                    "Не утверждай, что обсуждений не было: скажи, что точных совпадений не найдено, " +
                    "и при необходимости предложи повторить запрос позже.",
                    "<past_meetings/>\nExact past-meeting search completed — no matches. " +
                    "The semantic index is still building, so paraphrased matches may appear later. " +
                    "Do not claim there were no discussions: say no exact matches were found and, " +
                    "if needed, offer to retry the question later."));
                return sb.ToString();
            }
            sb.Append(period == null
                ? UiText.Tr(
                    "<past_meetings/>\nПоиск по прошлым встречам выполнен — совпадений по этому вопросу нет. " +
                    "Если спрашивают о прошлых договорённостях/решениях — скажи прямо, что подтверждения " +
                    "в прошлых встречах не найдено. Не выдумывай факты.",
                    "<past_meetings/>\nPast meetings were searched — no matches for this question. " +
                    "If asked about past agreements/decisions, say plainly that no confirmation was found " +
                    "in past meetings. Do not invent facts.")
                : UiText.Tr(
                    $"<past_meetings/>\nЗа период {period} встреч не найдено. Так и скажи: " +
                    "в указанный период встреч нет; не подставляй встречи других дат. Не выдумывай факты.",
                    $"<past_meetings/>\nNo meetings were found for {period}. Say plainly that there " +
                    "were no meetings in that period; do not substitute meetings from other dates. " +
                    "Do not invent facts."));
            return sb.ToString();
        }

        if (dateFallback && period != null)
            sb.Append(UiText.Tr(
                $"Точных совпадений по вопросу нет. Ниже — встречи за период {period}: " +
                "это контекст периода, а не доказательство того, что тема обсуждалась.\n",
                $"No exact matches for the question. Below are meetings from {period}: " +
                "this is period context, not proof the topic was discussed.\n"));
        sb.Append(UiText.Tr(
            "<past_meetings>\nФрагменты прошлых встреч, релевантные вопросу (дата · время · кто; " +
            "«ИИ» — ответ ассистента, «Резюме» — AI-сводка: это НЕ слова участников):\n",
            "<past_meetings>\nRelevant fragments from past meetings (date · time · who; " +
            "\"AI\" = assistant reply, \"Summary\" = AI digest: NOT participants' words):\n"));
        foreach (var h in hits)
        {
            var when = h.Ts.Length > 0 ? $" {h.Ts}" : "";
            // Только найденная строка со своей ролью. Соседние реплики без роли
            // склеивались с ней и приписывались её автору (ответ ИИ «включал»
            // следующую реплику участника); контекст модели даёт поиск.
            var line = $"[{h.MeetingStartedAt:dd.MM.yyyy}{when}] {h.SpeakerLabel}: {h.LineText}";
            if (sb.Length + line.Length + 1 > maxChars) break;
            sb.Append(line).Append('\n');
        }
        sb.Append("</past_meetings>\n");
        if (searchIncomplete)
            sb.Append(UiText.Tr(
                "Поиск был прерван (таймаут/индекс строится) — выдача может быть неполной. " +
                "Не утверждай «других обсуждений не было»; при нужде скажи, что поиск не завершён.\n",
                "The search was interrupted (timeout/index building) — results may be incomplete. " +
                "Do not claim there were no other discussions; say the search did not finish if asked.\n"));
        else if (semanticPending)
            sb.Append(UiText.Tr(
                "Смысловой индекс встреч ещё строится — перефразированные совпадения могут добавиться позже.\n",
                "The semantic meeting index is still building — paraphrased matches may appear later.\n"));
        sb.Append(UiText.Tr(
            "Правила: ссылаясь на прошлые встречи, называй дату («14.10», «вчера»). " +
            "Различай слова участников, ответы ИИ и AI-резюме: ответ ИИ или резюме не " +
            "подтверждают слова участника — помечай их источником явно. " +
            "Если в разные даты решения противоречат — назови обе даты. " +
            "Если нужного факта во фрагментах нет — скажи, что подтверждения не нашлось. Не выдумывай.",
            "Rules: when citing past meetings, name the date. Distinguish participants' words, " +
            "AI replies and AI summaries: an AI reply or summary does not confirm what a " +
            "participant said — mark its source explicitly. If decisions on different dates " +
            "contradict — name both dates. If the needed fact is not in the fragments, say " +
            "no confirmation was found. Do not invent."));
        return sb.ToString();
    }

    private static string? PeriodLabel(DateOnly? from, DateOnly? to)
    {
        if (from == null && to == null) return null;
        if (from != null && to != null && from == to) return from.Value.ToString("dd.MM.yyyy");
        if (from == null) return "≤ " + to!.Value.ToString("dd.MM.yyyy");
        if (to == null) return "≥ " + from.Value.ToString("dd.MM.yyyy");
        return from.Value.ToString("dd.MM.yyyy") + "–" + to.Value.ToString("dd.MM.yyyy");
    }

    /// <summary>
    /// Смещения строк отрендеренного транскрипта: offsets[i] = позиция начала i-й
    /// строки в тексте FormatTranscript(lines, null). Возвращает null, если
    /// рендер расходится с репликой (например Text содержал '\n') — тогда звонящий
    /// ищет строку по префиксу метки времени. Метка «[Изображение: …]» учитывается
    /// в формате ShortImageId (imageId → ≤12 симв. + «…»), как у FormatTranscript
    /// без resolver'а.
    /// </summary>
    public static int[]? LineCharOffsets(IReadOnlyList<MeetingJournalLine> lines, string renderedTranscript)
    {
        var offsets = new int[lines.Count];
        var pos = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            offsets[i] = pos;
            var l = lines[i];
            var label = l.Role == "question" && !string.IsNullOrEmpty(l.Speaker)
                ? l.Speaker!
                : MeetingTranscriptStore.RoleLabel(l.Role ?? "");
            var len = 1 + (l.Ts?.Length ?? 0) + 2 + label.Length + 2 + (l.Text?.Length ?? 0);
            if (l.ImageIds != null)
            {
                // Пробел перед меткой «[Изображение: …]» ставится, только если
                // хвост записи не на пробеле (FormatTranscript: sb[^1] != ' ').
                // Хвост — последний символ непустого Text; при пустом/отсутствующем
                // тексте после «: » уже стоит пробел; после первой метки — ']'.
                var tailIsSpace = string.IsNullOrEmpty(l.Text) || l.Text[^1] == ' ';
                foreach (var id in l.ImageIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    var name = id.Length <= 12 ? id : id[..12] + "…";
                    len += (tailIsSpace ? 0 : 1)
                        + 1 + UiText.Tr("Изображение", "Image").Length + 2 + name.Length + 1;
                    tailIsSpace = false; // хвост записи теперь на ']'
                }
            }
            len += 1; // '\n'
            // Проверка сходимости с реальным текстом — защита от '\n' в Text.
            if (pos + len > renderedTranscript.Length
                || renderedTranscript[pos] != '['
                || pos + len - 1 >= renderedTranscript.Length
                || renderedTranscript[pos + len - 1] != '\n')
                return null;
            pos += len;
        }
        return offsets;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vecCts.Cancel();
        _index?.Dispose();
        _vecCts.Dispose();
        _syncGate.Dispose();
    }
}
