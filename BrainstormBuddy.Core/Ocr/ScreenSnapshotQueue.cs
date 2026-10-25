namespace BrainstormBuddy.Core.Ocr;

/// <summary>Состояние элемента очереди снимков (§2.4).</summary>
public enum ScreenSnapState { Queued, Recognizing, WaitingAnswer, Done, Error, Stopped, Held }

/// <summary>Запрос на разовый снимок: пиксели уже сняты, OCR ещё не бежал (§2.2).</summary>
public sealed class ScreenSnapItem
{
    public required int Seq { get; init; }
    public required PixelBuffer Pixels { get; init; }
    /// <summary>Юзер хотел ответ LLM при нажатии (LLM был включён) — фиксируется на нажатие.</summary>
    public required bool LlmIntent { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    /// <summary>Встреча на момент нажатия; снимок не «утекает» в чужую встречу.</summary>
    public long? MeetingId { get; init; }
    public ScreenSnapState State = ScreenSnapState.Queued;
}

/// <summary>
/// UI-независимое ядро очереди разовых снимков (аудит 2026-10-14 §2). Здесь живёт
/// вся «диспетчерская» логика: FIFO-насос, переполнение, отмена/очистка, парковка
/// в Held при закрытом LLM-гейте и явная отправка отложенных. WPF остаётся в App:
/// он передаёт <see cref="ProcessItemAsync"/> (OCR → разнос результата) и реагирует
/// на события <see cref="Changed"/>/<see cref="QueueDrained"/>/<see cref="ItemDismissed"/>.
/// Изоляция нужна, чтобы инварианты §2.6–§2.9 покрывались юнит-тестами без STA-окна.
/// </summary>
public sealed class ScreenSnapshotQueue
{
    /// <summary>Лимит ожидающих в очереди (§2.6) — переполнение даёт явный отказ.</summary>
    private readonly int _max;
    private readonly List<ScreenSnapItem> _queue = new();   // ожидают FIFO
    private readonly List<ScreenSnapItem> _held = new();    // припаркованы до явной отправки
    private bool _pump;                                     // только под lock
    private ScreenSnapItem? _current;

    /// <summary>Актуальный LLM-гейт (вкл/пауза): true = можно отправлять (§2.7).</summary>
    private readonly Func<bool> _llmGateOpen;
    /// <summary>Текущая встреча; чужие снимки останавливаются (§2.9).</summary>
    private readonly Func<long?> _currentMeetingId;
    /// <summary>OCR + разнос результата одного элемента (реализация — в App).</summary>
    private readonly Func<ScreenSnapItem, Task> _process;
    private readonly Action<string>? _log;

    /// <summary>Очередь/held изменились — перерисовать item-тосты и агрегаты.</summary>
    public event Action? Changed;
    /// <summary>Нажатие принято/отклонено/элемент изъят из очереди до OCR — снять его тост.</summary>
    public event Action<ScreenSnapItem>? ItemDismissed;
    /// <summary>Насос опустошил очередь — вернуть индикатор в Idle.</summary>
    public event Action? QueueDrained;

    public ScreenSnapshotQueue(int max,
        Func<bool> llmGateOpen, Func<long?> currentMeetingId,
        Func<ScreenSnapItem, Task> process, Action<string>? log = null)
    {
        _max = max;
        _llmGateOpen = llmGateOpen;
        _currentMeetingId = currentMeetingId;
        _process = process;
        _log = log;
    }

    /// <summary>Число заданий, ещё ждущих обработки (очередь + отложенные), без текущего.</summary>
    public int PendingCount { get { lock (_queue) return _queue.Count + _held.Count; } }

    /// <summary>Снимок «текущий + ожидающие» для перерисовки UI.</summary>
    public List<ScreenSnapItem> Snapshot()
    {
        lock (_queue)
        {
            var s = _queue.ToList();
            if (_current != null) s.Insert(0, _current);
            return s;
        }
    }

    /// <summary>Число припаркованных в Held (для тоста «ждут LLM — отправить?»).</summary>
    public int HeldCount { get { lock (_queue) return _held.Count(x => x.State == ScreenSnapState.Held); } }

    /// <summary>Позиция в очереди (1..N), 0 — не в очереди.</summary>
    public int QueuePosition(int seq)
    {
        lock (_queue) return _queue.FindIndex(x => x.Seq == seq) + 1;
    }

    /// <summary>Поставить снимок в очередь; вернуть позицию (1..N) или -1 при переполнении.</summary>
    public int Enqueue(ScreenSnapItem item)
    {
        lock (_queue)
        {
            if (_queue.Count >= _max) return -1;
            _queue.Add(item);
            var pos = _queue.Count;
            EnsurePump();
            return pos;
        }
    }

    /// <summary>Запустить насос, если не бежит (только под lock).</summary>
    private void EnsurePump()
    {
        if (!_pump) { _pump = true; _ = Task.Run(PumpAsync); }
    }

    /// <summary>
    /// Отмена отдельного задания (§2.6): снимает ожидающий/отложенный элемент или
    /// помечает идущий Stopped — результат не разнесётся. Возвращает true, если попал.
    /// </summary>
    public bool Cancel(int seq)
    {
        bool hit;
        lock (_queue)
        {
            hit = false;
            var q = _queue.FirstOrDefault(x => x.Seq == seq);
            if (q != null) { q.State = ScreenSnapState.Stopped; _queue.Remove(q); hit = true; }
            var h = _held.FirstOrDefault(x => x.Seq == seq);
            if (h != null) { h.State = ScreenSnapState.Stopped; _held.Remove(h); hit = true; }
            if (_current?.Seq == seq) { _current.State = ScreenSnapState.Stopped; hit = true; }
        }
        if (hit) { _log?.Invoke($"cancel {seq}"); Changed?.Invoke(); }
        return hit;
    }

    /// <summary>Очистка всех ожидающих (§2.6): очередь + отложенные. Идущее доедет само.</summary>
    public int ClearPending()
    {
        int cleared;
        lock (_queue)
        {
            cleared = _queue.Count;
            foreach (var it in _queue) it.State = ScreenSnapState.Stopped;
            _queue.Clear();
            foreach (var it in _held) it.State = ScreenSnapState.Stopped;
            cleared += _held.Count;
            _held.Clear();
        }
        if (cleared > 0) { _log?.Invoke($"cleared {cleared}"); Changed?.Invoke(); }
        return cleared;
    }

    /// <summary>
    /// Явная отправка отложенных (§2.8): вернуть Held в начало очереди (нажаты раньше).
    /// Возвращает число перемещённых; -1 — гейт закрыт (LLM выкл/пауза, не отправляем).
    /// </summary>
    public int SendHeld()
    {
        if (!_llmGateOpen()) return -1;
        int moved;
        lock (_queue)
        {
            var toSend = _held.Where(x => x.State == ScreenSnapState.Held).OrderBy(x => x.Seq).ToList();
            foreach (var it in toSend) it.State = ScreenSnapState.Queued;
            _queue.InsertRange(0, toSend);
            _held.RemoveAll(x => toSend.Contains(x));
            moved = toSend.Count;
            if (moved > 0) EnsurePump();
        }
        if (moved > 0) { _log?.Invoke($"send held {moved}"); Changed?.Invoke(); }
        return moved;
    }

    /// <summary>
    /// FIFO-насос: guard (отмена/чужая встреча/LLM-гейт) → ProcessItemAsync. Один элемент
    /// за раз — порядок ответов = порядку нажатий. Ошибка одного элемента не роняет насос.
    /// </summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            ScreenSnapItem? item;
            lock (_queue)
            {
                while (_queue.Count > 0 && _queue[0].State == ScreenSnapState.Stopped)
                {
                    var dead = _queue[0];
                    _queue.RemoveAt(0);
                    ItemDismissed?.Invoke(dead);
                }
                if (_queue.Count == 0)
                {
                    _pump = false;
                    _current = null;
                    QueueDrained?.Invoke();
                    return;
                }
                item = _queue[0];
                _queue.RemoveAt(0);
                _current = item;
            }

            // §2.9: снимок чужой (прошлой) встречи не улетает в текущую — останавливаем.
            if (item.MeetingId != null && item.MeetingId != _currentMeetingId())
            {
                item.State = ScreenSnapState.Stopped;
                _log?.Invoke($"stop foreign {item.Seq}");
                ItemDismissed?.Invoke(item);
                Changed?.Invoke();
                continue;
            }

            // §2.7: актуальный LLM-гейт перед отправкой. Нажатый при включённой LLM, но
            // дождавшийся очереди при выключенной/паузе — в Held, не отправляем тайно.
            if (item.LlmIntent && !_llmGateOpen())
            {
                item.State = ScreenSnapState.Held;
                lock (_queue) _held.Add(item);
                _log?.Invoke($"held {item.Seq}");
                ItemDismissed?.Invoke(item);
                Changed?.Invoke();
                continue;
            }

            try
            {
                await _process(item);
            }
            catch (Exception ex)
            {
                // ProcessItemAsync уже ловит свои ошибки; это страховка, чтобы насос жил.
                item.State = ScreenSnapState.Error;
                _log?.Invoke($"process threw {item.Seq}: {ex.Message}");
            }
            finally
            {
                lock (_queue) _current = null;
                Changed?.Invoke();
            }
        }
    }
}
