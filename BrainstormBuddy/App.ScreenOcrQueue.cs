using System.Windows;
using BrainstormBuddy.Core.Ocr;

namespace BrainstormBuddy;

/// <summary>
/// Очередь разовых снимков экрана по хоткею «Распознать сейчас» (Ctrl+Shift+N;
/// аудит 2026-10-14 §2 «Очередь Ctrl+Shift+N»). Раньше повторное нажатие во время
/// прохода молча отбрасывалось (_screenRecognizeOnceActive) — запрос терялся.
///
/// Модель (ядро — <see cref="ScreenSnapshotQueue"/>, UI-независимое и покрыто
/// юнит-тестами; здесь только WPF-клей: захват пикселей, OCR, тосты, статус):
/// - Каждое нажатие СНИМАЕТ экран немедленно (пиксели фиксируются на момент
///   нажатия, §2.1) и становится элементом с id, временем и привязкой к встрече
///   (§2.2). Постановка показывается сразу тостом снимка.
/// - Насос обрабатывает элементы FIFO: OCR → ответ/транскриб (§2.3). Ожидание
///   ответа LLM не запрещает следующий захват — захват уже сделан при нажатии.
/// - У элемента состояния Queued/Recognizing/WaitingAnswer/Done/Error/Stopped/Held,
///   видимые в его тосте (§2.4). Один снимок = свой тост и свой ответ (§2.5).
/// - Отмена отдельного задания — крестиком его тоста (§2.6), обработка in-flight
///   LLM-запроса — крестиком пузыря (OverlayWindow.BeginRequest CTS). Очистка всех
///   ожидающих — тост «Очистить всё». Лимит очереди 10, переполнение — явный отказ.
/// - Включённость LLM перепроверяется перед отправкой (§2.7): элемент, дождавшийся
///   очереди при выключенном/на паузе LLM, паркуется в Held (не отправляется тайно).
///   После включения LLM отложенные не летят сами — тост предлагает «Отправить
///   очередь» явно (§2.8). Снимки чужой (прошлой) встречи останавливаются (§2.9).
/// </summary>
public partial class App
{
    // Ядро очереди (FIFO/Held/отмена/очистка/лимит) — без WPF, тестируемое в Core.
    private ScreenSnapshotQueue SnapQ => _snapQ ??= CreateSnapQ();
    private ScreenSnapshotQueue? _snapQ;

    private ScreenSnapshotQueue CreateSnapQ()
    {
        var q = new ScreenSnapshotQueue(
            MaxOcrQueue,
            llmGateOpen: () => !Config.Ui.LlmDisabled && !IsLlmPaused,
            currentMeetingId: () => CurrentMeetingId,
            process: ProcessOneAsync,
            log: m => Logger.Info($"ScreenOcr queue: {m}", "ScreenOcr"));
        q.Changed += () => Dispatcher.BeginInvoke(RefreshOcrUi);
        q.ItemDismissed += item => Dispatcher.BeginInvoke(() => DismissItemToast(item.Seq));
        q.QueueDrained += () => Dispatcher.BeginInvoke(() =>
        {
            RefreshOcrUi();
            SetOcrState(OcrIndicatorState.Idle);
            RefreshOverlayStatus();
        });
        return q;
    }

    private const int MaxOcrQueue = 10;

    private readonly Dictionary<int, int> _ocrItemToast = new();     // seq → toastId
    private int _ocrSeq;               // монотонный номер снимка (id задания, §2.2)
    private int _ocrQueueToastId;      // агрегат-тост «очередь N — очистить всё» (0 = нет)
    private int _ocrHeldToastId;       // тост «отложено N — отправить?» (0 = нет)

    // ===== Приём нажатия =====

    /// <summary>
    /// Хоткей «Распознать сейчас»: снять кадр и поставить в очередь. Возвращается
    /// сразу; принятие видно по статусу и тосту снимка.
    /// </summary>
    private void EnqueueScreenSnapshot()
    {
        // Флаг LLM фиксируем на момент нажатия: ветка показа результата должна
        // опираться на одно значение, а не на живой конфиг (юзер мог его переключить).
        var llmIntent = !Config.Ui.LlmDisabled && !IsLlmPaused;
        var seq = Interlocked.Increment(ref _ocrSeq);

        _overlay?.SetStatus(string.Format(TT("L_Ocr_CapturingFmt", "OCR: снимок {0}…"), seq));
        SetOcrState(OcrIndicatorState.Scanning);

        _ = Task.Run(async () =>
        {
            PixelBuffer pixels;
            try
            {
                // Захват — приоритетная часть: пиксели должны соответствовать тому,
                // что видел юзер при нажатии (§2.1), а не тому, что на экране при OCR.
                var (px, captureMs) = await CaptureScreenPixelsAsync();
                pixels = px;
                Logger.Debug($"ScreenOcr queue: snap{seq} captured {px.Width}×{px.Height} за {captureMs:F0} мс", "ScreenOcr");
            }
            catch (Exception ex)
            {
                Logger.Warn($"ScreenOcr queue: snap{seq} capture failed — {ex.Message}", "ScreenOcr");
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.SetStatus(string.Format(TT("L_Ocr_ErrorFmt", "OCR: ошибка — {0}"), ex.Message));
                    ShowOcrError();
                    SetOcrState(OcrIndicatorState.Idle);
                });
                return;
            }

            var item = new ScreenSnapItem
            {
                Seq = seq,
                Pixels = pixels,
                LlmIntent = llmIntent,
                EnqueuedAt = DateTimeOffset.UtcNow,
                MeetingId = CurrentMeetingId,
            };
            var pos = SnapQ.Enqueue(item);
            if (pos < 0)
            {
                // Переполнение — явный отказ, задание не теряем молча (§2.6).
                Logger.Warn($"ScreenOcr queue: snap{seq} REJECTED — очередь заполнена ({MaxOcrQueue})", "ScreenOcr");
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.SetStatus(string.Format(TT("L_Ocr_QueueFullFmt", "OCR: очередь заполнена ({0}) — снимок отклонён"), MaxOcrQueue));
                    PushOverlayToast(ToastKind.Error, TT("L_Ocr_QueueTitle", "OCR: очередь снимков"),
                        string.Format(TT("L_Ocr_QueueFullBodyFmt", "Очередь заполнена ({0}) — снимок #{1} отклонён"), MaxOcrQueue, seq));
                    SetOcrState(OcrIndicatorState.Idle);
                });
                return;
            }

            Logger.Info($"ScreenOcr queue: snap{seq} enqueued (позиция {pos}, встреча {item.MeetingId?.ToString() ?? "-"})", "ScreenOcr");
            await Dispatcher.InvokeAsync(() =>
            {
                if (pos > 1)
                    _overlay?.SetStatus(string.Format(TT("L_Ocr_QueuedFmt", "OCR: снимок {0} в очереди ({1} всего)"), seq, pos));
                RefreshOcrUi();
            });
        });
    }

    /// <summary>OCR + разнос результата одного элемента (LLM-вопрос или transcript-only).</summary>
    private async Task ProcessOneAsync(ScreenSnapItem item)
    {
        var waitedMs = (DateTimeOffset.UtcNow - item.EnqueuedAt).TotalMilliseconds;
        item.State = ScreenSnapState.Recognizing;
        await Dispatcher.InvokeAsync(() =>
        {
            _overlay?.SetStatus(string.Format(TT("L_Ocr_RecognizingFmt", "OCR: распознаю снимок {0}…"), item.Seq));
            SetOcrState(OcrIndicatorState.Scanning);
            UpdateItemToast(item);
        });

        try
        {
            var (text, ocrMs) = await OcrPixelsAsync(item.Pixels);
            Logger.Info($"ScreenOcr queue: snap{item.Seq} OCR {ocrMs:F0} мс (ждал {waitedMs:F0} мс, очередь {PendingOcrCount()})", "ScreenOcr");

            // Пользователь мог отменить задание, пока шёл OCR — результат не разносим.
            if (item.State == ScreenSnapState.Stopped)
            {
                Logger.Info($"ScreenOcr queue: snap{item.Seq} cancelled during OCR — результат отброшен", "ScreenOcr");
                await Dispatcher.InvokeAsync(() => DismissItemToast(item.Seq));
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                item.State = ScreenSnapState.Done;
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.SetStatus(TT("L_Ocr_NoText", "OCR: текст на экране не найден"));
                    DismissItemToast(item.Seq);
                    SetOcrState(OcrIndicatorState.Idle);
                });
                return;
            }

            if (!item.LlmIntent)
            {
                // §5 отключенного LLM при нажатии: только текст с экрана в транскриб + журнал.
                item.State = ScreenSnapState.Done;
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.AddSendingItem($"[screen] {text}", 0, DateTime.Now, null, null, 0,
                        transcriptOnly: true, ocrLatencyMs: ocrMs);
                    AppendJournalWithMemory("question", $"[screen] {text}");
                    _overlay?.SetStatus(string.Format(TT("L_Ocr_DoneLlmOffFmt", "OCR: распознано {0} симв. за {1:F1}s — LLM выключен"), text.Length, ocrMs / 1000));
                    DismissItemToast(item.Seq);
                    SetOcrState(OcrIndicatorState.Idle);
                });
                return;
            }

            // LLM-вопрос: пузырь/караоке/журнал — внутри SendScreenQuestion; пузырь сам
            // даёт per-item отмену in-flight запроса крестиком (BeginRequest CTS).
            item.State = ScreenSnapState.WaitingAnswer;
            await Dispatcher.InvokeAsync(() => { UpdateItemToast(item); });
            await SendScreenQuestion(text, ocrMs);
            item.State = ScreenSnapState.Done;
            await Dispatcher.InvokeAsync(() => DismissItemToast(item.Seq));
        }
        catch (Exception ex)
        {
            item.State = ScreenSnapState.Error;
            Logger.Warn($"ScreenOcr queue: snap{item.Seq} failed — {ex.Message}", "ScreenOcr");
            await Dispatcher.InvokeAsync(() =>
            {
                _overlay?.SetStatus(string.Format(TT("L_Ocr_ErrorFmt", "OCR: ошибка — {0}"), ex.Message));
                // Оставляем тост снимка с текстом ошибки — его закроет пользователь.
                UpdateItemToast(item);
                ShowOcrError();
            });
        }
    }

    // ===== Отмена / очистка / отправка отложенных =====

    /// <summary>
    /// Отмена отдельного задания по номеру (§2.6). Снимает ожидающий/отложенный элемент;
    /// для идущего OCR ставит Stopped — результат не разнесётся. In-flight LLM-запрос
    /// дополнительно отменяется крестиком пузыря (OverlayWindow.BeginRequest CTS).
    /// </summary>
    private void CancelOcrItem(int seq)
    {
        if (SnapQ.Cancel(seq)) Logger.Info($"ScreenOcr queue: snap{seq} отменён пользователем", "ScreenOcr");
        Dispatcher.BeginInvoke(() => { DismissItemToast(seq); RefreshOcrUi(); });
    }

    /// <summary>
    /// Очистка всех ожидающих заданий (§2.6): снимает всю очередь и отложенные.
    /// Идущее задание доедет само — отменять живой OCR/LLM грубо небезопасно.
    /// </summary>
    private void ClearOcrQueue()
    {
        var cleared = SnapQ.ClearPending();
        if (cleared > 0)
            Logger.Info($"ScreenOcr queue: очищено {cleared} заданий (текущее доедет)", "ScreenOcr");
        Dispatcher.BeginInvoke(() =>
        {
            // Снимаем все item-тосты и агрегаты.
            foreach (var id in _ocrItemToast.Values) _overlay?.DismissToast(id);
            _ocrItemToast.Clear();
            DismissAggregateToasts();
            if (cleared > 0)
                _overlay?.SetStatus(TT("L_Ocr_QueueCleared", "OCR: очередь очищена"));
        });
    }

    /// <summary>
    /// «Отправить очередь» из тоста отложенных (§2.8): явно вернуть припаркованные
    /// элементы в очередь (перед новыми — они нажаты раньше). Без LLM — no-op со статусом.
    /// </summary>
    private void SendHeldOcrQueue()
    {
        if (Config.Ui.LlmDisabled || IsLlmPaused)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _overlay?.SetStatus(TT("L_Ocr_LlmOff", "OCR: вопрос найден, но LLM выключен"));
                RefreshOcrUi();
            });
            return;
        }
        var moved = SnapQ.SendHeld();
        if (moved > 0)
            Logger.Info($"ScreenOcr queue: пользователь отправил {moved} отложенных снимков", "ScreenOcr");
        Dispatcher.BeginInvoke(RefreshOcrUi);
    }

    /// <summary>Гейт LLM изменился (вкл/выкл/пауза) — обновить тост отложенных.</summary>
    private void OnOcrLlmGateChanged() => Dispatcher.BeginInvoke(RefreshOcrUi);

    // ===== UI: тосты =====

    /// <summary>Число заданий, ещё ждущих обработки (очередь + отложенные).</summary>
    private int PendingOcrCount() => SnapQ.PendingCount;

    /// <summary>Единая точка перерисовки: item-тосты + агрегат очистки + тост отложенных.</summary>
    private void RefreshOcrUi()
    {
        var snapshot = SnapQ.Snapshot();
        var held = SnapQ.HeldCount;
        // Позиции «В очереди · k/N» пересчитываем на каждом обновлении.
        foreach (var it in snapshot) UpdateItemToast(it);
        UpdateClearAllToast(snapshot.Count(x => x.State == ScreenSnapState.Queued));
        UpdateHeldToast(held);
    }

    /// <summary>Item-тост «OCR: снимок #N»: создаёт/обновляет тело по состоянию. ✕ = отмена задания.</summary>
    private void UpdateItemToast(ScreenSnapItem it)
    {
        string body = it.State switch
        {
            ScreenSnapState.Queued => QueuedBody(it.Seq),
            ScreenSnapState.Recognizing => TT("L_Ocr_ItemRecognizing", "Распознаю…"),
            ScreenSnapState.WaitingAnswer => TT("L_Ocr_ItemWaiting", "Жду ответа LLM…"),
            ScreenSnapState.Held => TT("L_Ocr_ItemHeld", "Ждёт LLM"),
            ScreenSnapState.Error => TT("L_Ocr_ItemError", "Ошибка"),
            ScreenSnapState.Done => TT("L_Ocr_ItemDone", "Готово"),
            ScreenSnapState.Stopped => TT("L_Ocr_ItemStopped", "Остановлено"),
            _ => "",
        };
        if (_ocrItemToast.TryGetValue(it.Seq, out var id))
        {
            _overlay?.UpdateToast(id, body);
        }
        else if (it.State is ScreenSnapState.Queued or ScreenSnapState.Recognizing or ScreenSnapState.WaitingAnswer or ScreenSnapState.Error)
        {
            // Тост есть только у «живого» задания (Done/Stopped/Held — без отдельного тоста).
            var seq = it.Seq;
            _ocrItemToast[seq] = _overlay?.PushToast(
                string.Format(TT("L_Ocr_ItemTitleFmt", "OCR: снимок #{0}"), seq),
                body, ToastKind.Progress,
                onClose: () => CancelOcrItem(seq)) ?? 0;
        }
    }

    private string QueuedBody(int seq)
    {
        var pos = SnapQ.QueuePosition(seq);
        return pos > 0
            ? string.Format(TT("L_Ocr_ItemQueuedPosFmt", "В очереди · позиция {0}"), pos)
            : TT("L_Ocr_ItemQueued", "В очереди");
    }

    private void DismissItemToast(int seq)
    {
        if (_ocrItemToast.Remove(seq, out var id)) _overlay?.DismissToast(id);
    }

    /// <summary>Агрегат-тост «Очистить всё» — только при ≥2 ожидающих (иначе хватает item-✕).</summary>
    private void UpdateClearAllToast(int queued)
    {
        if (queued >= 2)
        {
            var body = string.Format(TT("L_Ocr_QueueBodyFmt", "В очереди: {0} — очистить все"), queued);
            if (_ocrQueueToastId == 0)
            {
                _ocrQueueToastId = _overlay?.PushToast(
                    TT("L_Ocr_QueueTitle", "OCR: очередь снимков"),
                    body, ToastKind.Progress,
                    onClose: ClearOcrQueue,
                    actionText: TT("L_Ocr_ClearAll", "Очистить всё"),
                    onAction: ClearOcrQueue) ?? 0;
            }
            else _overlay?.UpdateToast(_ocrQueueToastId, body);
        }
        else if (_ocrQueueToastId != 0)
        {
            _overlay?.DismissToast(_ocrQueueToastId);
            _ocrQueueToastId = 0;
        }
    }

    /// <summary>Тост отложенных: «N ждут — отправить?» при открытом гейте, иначе «ждут LLM».</summary>
    private void UpdateHeldToast(int held)
    {
        if (held <= 0)
        {
            if (_ocrHeldToastId != 0) { _overlay?.DismissToast(_ocrHeldToastId); _ocrHeldToastId = 0; }
            return;
        }
        var gateOpen = !Config.Ui.LlmDisabled && !IsLlmPaused;
        var body = gateOpen
            ? string.Format(TT("L_Ocr_HeldReadyFmt", "Ждут отправки: {0} — нажмите «Отправить»"), held)
            : string.Format(TT("L_Ocr_HeldWaitingFmt", "Ждут LLM: {0} (LLM выкл/пауза)"), held);
        if (_ocrHeldToastId == 0)
        {
            _ocrHeldToastId = _overlay?.PushToast(
                TT("L_Ocr_HeldTitle", "OCR: отложенные снимки"),
                body, ToastKind.Progress,
                // Кнопка «Отправить» есть всегда; при закрытом гейте SendHeld скажет причину.
                actionText: TT("L_Ocr_SendQueue", "Отправить"),
                onAction: SendHeldOcrQueue) ?? 0;
        }
        else _overlay?.UpdateToast(_ocrHeldToastId, body);
    }

    private void DismissAggregateToasts()
    {
        if (_ocrQueueToastId != 0) { _overlay?.DismissToast(_ocrQueueToastId); _ocrQueueToastId = 0; }
        if (_ocrHeldToastId != 0) { _overlay?.DismissToast(_ocrHeldToastId); _ocrHeldToastId = 0; }
    }
}
