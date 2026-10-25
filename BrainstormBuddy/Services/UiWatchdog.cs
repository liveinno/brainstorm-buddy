using System.IO;
using System.Windows.Threading;
using BrainstormBuddy.Services;

namespace BrainstormBuddy;

/// <summary>
/// Сторож зависаний UI. Оверлей скрыт из таскбара и из захвата экрана —
/// если UI-поток намертво встал (как в дедлоке слайдер/лог, 2026-07-04),
/// пользователь НЕ может закрыть приложение штатно. Сторож пингует Dispatcher
/// из фонового потока; если UI не отвечает HangThresholdSeconds подряд —
/// пишет диагностику прямо в файл (минуя UI-логгер) и жёстко завершает процесс.
/// Штатный shutdown (Dispose, отмена ping'а при гаснущем Dispatcher'е) зависанием
/// не считается и процесс не завершает.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    private const int PingIntervalMs = 5000;
    private const int ConfirmPingMs = 1000;
    private const int HangThresholdSeconds = 20;

    private readonly Dispatcher _dispatcher;
    private readonly LoggingService _logger;
    private readonly string _crashLogPath;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _hooksAttached;

    // Что сейчас делает UI-поток по данным DispatcherHooks — единственный
    // способ сказать «застрял В ОПЕРАЦИИ X» без дампа. Пишутся только на
    // диспетчерном потоке, читаются из цикла сторожа → Interlocked/Volatile.
    private string? _curOpDesc;           // описание исполняемой операции
    private long _curOpStartTicks;        // UtcTicks старта текущей операции (0 = простой)
    private long _lastPumpTicks;          // UtcTicks последней прокачки очереди

    public UiWatchdog(Dispatcher dispatcher, LoggingService logger, string appDataDir)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _crashLogPath = Path.Combine(appDataDir, "logs", "ui-hang.log");
    }

    public void Start()
    {
        if (_loop != null) return; // повторный Start не должен плодить второй цикл-убийцу
        AttachDispatcherHooks();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.Info($"UiWatchdog started (ping {PingIntervalMs}ms, kill after {HangThresholdSeconds}s hang)", "App");
    }

    /// <summary>
    /// Подписка на DispatcherHooks: фиксируем имя и момент старта каждой
    /// исполняемой UI-операции. В инциденте 2026-10-16 13:01 сторож убил
    /// процесс, но «почему висел UI» осталось недоказанным — дампа не было.
    /// Теперь при таймауте пинга в ui-hang.log падает и снапшот состояния
    /// диспетчера: какая операция исполняется и сколько уже висит.
    /// </summary>
    private void AttachDispatcherHooks()
    {
        try
        {
            void Attach()
            {
                var hooks = _dispatcher.Hooks;
                hooks.OperationStarted += OnOpStarted;
                hooks.OperationCompleted += OnOpFinished;
                hooks.OperationAborted += OnOpFinished;
                Volatile.Write(ref _hooksAttached, true);
            }
            // Dispatcher.Hooks живёт на диспетчерном потоке: подписываемся там же.
            if (_dispatcher.CheckAccess()) Attach();
            else _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)Attach);
        }
        catch { /* диагностика опциональна — сторож обязан работать и без хуков */ }
    }

    private void OnOpStarted(object? sender, DispatcherHookEventArgs e)
    {
        try { Interlocked.Exchange(ref _curOpDesc, e.Operation?.ToString()); }
        catch { }
        Interlocked.Exchange(ref _curOpStartTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _lastPumpTicks, DateTime.UtcNow.Ticks);
    }

    private void OnOpFinished(object? sender, DispatcherHookEventArgs e)
    {
        Interlocked.Exchange(ref _curOpStartTicks, 0);
        Interlocked.Exchange(ref _curOpDesc, null);
        Interlocked.Exchange(ref _lastPumpTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Снапшот «чем занят UI-поток» для ui-hang.log. Три состояния:
    /// исполняется операция X (её имя и длительность), очередь пустая —
    /// диспетчер простаивает, значит UI-поток мёртв ДО выборки операции
    /// (нативный блок/lock вне Dispatcher), либо хуков нет вовсе.
    /// </summary>
    private string DescribeUiState()
    {
        if (!Volatile.Read(ref _hooksAttached))
            return "dispatcher hooks unavailable";
        var startTicks = Interlocked.Read(ref _curOpStartTicks);
        var now = DateTime.UtcNow;
        if (startTicks != 0)
        {
            var elapsed = (now - new DateTime(startTicks, DateTimeKind.Utc)).TotalSeconds;
            var desc = Volatile.Read(ref _curOpDesc) ?? "unknown op";
            return $"ui busy in op for {elapsed:F0}s: {desc}";
        }
        var lastPump = Interlocked.Read(ref _lastPumpTicks);
        var idleFor = lastPump == 0
            ? -1.0
            : (now - new DateTime(lastPump, DateTimeKind.Utc)).TotalSeconds;
        return idleFor < 0
            ? "dispatcher idle: no op seen since watchdog start — ui thread blocked before queue dispatch"
            : $"dispatcher idle {idleFor:F0}s (no op executing) — ui thread blocked outside the dispatcher queue";
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lastResponse = DateTime.UtcNow;
        // Счётчик подтверждённых промахов подряд: одиночный miss после
        // сна/паузы VM не убивает процесс (см. комментарий у порога ниже).
        var consecutiveTimeouts = 0;
        void MarkAlive() { lastResponse = DateTime.UtcNow; consecutiveTimeouts = 0; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Background мог голодать на штатной очереди рендера/привязок и давать
                // ложное «зависание», хотя UI принимает ввод. Ping идёт приоритетом
                // Send (выше Normal/Input): живой Dispatcher обслуживает его первым
                // даже при потоке Normal-работы и внутри модальных циклов (WPF
                // качает очередь в чужом message-loop), а настоящий синхронный
                // блок UI по-прежнему не пропускает ping.
                // Диспетчер уже гаснет: BeginInvoke на уходящем Dispatcher'е
                // бросает InvalidOperationException — выходим до него. Проверка
                // после ping'а тоже остаётся: shutdown может начаться, пока
                // операция висит в очереди.
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;
                var op = _dispatcher.BeginInvoke(DispatcherPriority.Send, () => { });
                var winner = await Task.WhenAny(
                    op.Task,
                    Task.Delay(PingIntervalMs, ct));

                // Отмена от Dispose завершает delay немедленно — это НЕ таймаут UI:
                // иначе каждый штатный OnExit писал бы ложный «UI ping timeout»,
                // а при давнем lastResponse мог бы уйти FailFast уже на выходе.
                if (ct.IsCancellationRequested) break;

                // Диспетчер гаснет (Shutdown/SessionEnding): ping не выполнится,
                // но это не зависание — сторож выходит тихо, не убивая процесс.
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;

                if (winner == op.Task)
                {
                    if (op.Task.IsCanceled)
                    {
                        // Отменённый DispatcherOperation — диспетчер уходит на shutdown:
                        // ping не выполнялся. Считать его ответом маскировало бы реальный
                        // hang, считать таймаутом — давало бы ложный FailFast на выходе.
                        break;
                    }
                    // RanToCompletion — очевидно жив; Faulted — callback всё же исполнился
                    // на UI-потоке, значит диспетчер работает — тоже жив.
                    MarkAlive();
                }
                else
                {
                    // Пинг не отобран за PingIntervalMs — снимаем его из очереди,
                    // иначе протухшие операции копятся на всём протяжении блока
                    // (Abort потокобезопасен). Abort вернёт false, если операция
                    // уже выполнилась/начала выполняться — гонка «таймаут
                    // одновременно с ответом» тогда честно засчитывает ответ,
                    // а не ложный hang.
                    if (!op.Abort())
                    {
                        MarkAlive();
                    }
                    else
                    {
                        // Подтверждающий пинг: протухший op мог быть отставлен не
                        // из-за блокировки, а из-за паузы ВСЕЙ системы — sleep/
                        // hibernate/останов отладчика/пауза VM. На выходе из паузы
                        // Task.Delay уже истёк, а диспетчер ещё не качнул очередь —
                        // Abort() фиксировал бы «hang», и lastResponse, замёрзший
                        // до сна, давал бы ложный FailFast сразу после пробуждения.
                        // Живой диспетчер обслуживает Send-пинг сразу при первой
                        // прокачке; реально вставший UI не ответит и за лишнюю
                        // секунду — подавления зависания нет (порогу 20 с секунда
                        // подтверждения ничего не меняет по сути).
                        var confirm = _dispatcher.BeginInvoke(DispatcherPriority.Send, () => { });
                        var confirmWinner = await Task.WhenAny(
                            confirm.Task, Task.Delay(ConfirmPingMs, ct));
                        if (ct.IsCancellationRequested) break;
                        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;
                        if (confirmWinner == confirm.Task && confirm.Task.IsCanceled)
                        {
                            // Подтверждение отменено диспетчером — гонка shutdown:
                            // не «ответ» (иначе маскируем hang) и не «таймаут»
                            // (иначе лишняя строка ui-hang.log на выходе). FailFast
                            // всё равно отрезан барьером ниже, но мусорный лог не нужен.
                            break;
                        }
                        if (confirmWinner == confirm.Task)
                        {
                            MarkAlive();
                        }
                        else
                        {
                            // Abort() == false: подтверждение успело исполниться до
                            // снятия — таймаут совпал с ответом, засчитываем ответ
                            // (как у основного ping'а), а не ложный miss.
                            if (!confirm.Abort())
                            {
                                MarkAlive();
                            }
                            else
                            {
                                consecutiveTimeouts++;
                                var hangSeconds = (DateTime.UtcNow - lastResponse).TotalSeconds;
                                TryWriteCrashLog(
                                    $"UI ping timeout #{consecutiveTimeouts}, hang for {hangSeconds:F0}s — {DescribeUiState()}");
                                // hangSeconds считается по wall-clock с lastResponse: после сна/
                                // гибернации/паузы VM или долгой остановки отладчика разница легко
                                // переваливает за порог за один miss, хотя реальной блокировки UI нет —
                                // диспетчер просто ещё не успел качнуть очередь после пробуждения
                                // (см. комментарий у confirm-пинга выше). Поэтому убиваем только по
                                // ДВУМ подряд подтверждённым таймаутам: живой UI отвечает на следующий
                                // же цикл и сбрасывает счётчик через MarkAlive, а реально мёртвый
                                // доживёт до второго miss неизбежно. При настоящем зависании первый
                                // miss приходится на ≥5–6 с неотвечания, второй — на ≥16–17 с, что и
                                // задаёт ~20-секундный порог; wall-clock тогда честно отражает длину
                                // блока. Цена: зависание, начавшееся прямо в сон, убивается на цикл
                                // позже — различить «сон» и «висок» по одному снимку невозможно.
                                if (hangSeconds >= HangThresholdSeconds && consecutiveTimeouts >= 2)
                                {
                                    // Финальный барьер перед убийством: между проверкой
                                    // ct выше и этой точкой OnExit мог начать Dispose/
                                    // shutdown — FailFast на выходе рвал бы сторы
                                    // посреди записи. Молча выйти правильно: процесс
                                    // и так завершается, а диагностика зависания
                                    // уже лежит в ui-hang.log строкой выше.
                                    if (ct.IsCancellationRequested ||
                                        _dispatcher.HasShutdownStarted ||
                                        _dispatcher.HasShutdownFinished) break;
                                    TryWriteCrashLog(
                                        $"UI thread hung for {hangSeconds:F0}s — self-terminating so the user isn't stuck " +
                                        $"with an unkillable overlay. {DescribeUiState()}. " +
                                        $"Check the last entries of app.log for the deadlock site.");
                                    // Дублируем в app.log СИНХРОННО (AppendAllText по месту) — иначе
                                    // FailFast не оставляет в основном логе никакого объяснения смерти.
                                    try { _logger.Error($"UiWatchdog: UI hung >= {HangThresholdSeconds}s — self-terminating", null, "App"); }
                                    catch { }
                                    Environment.FailFast("BrainstormBuddy UI thread hang — watchdog self-termination");
                                }
                            }
                    }
                }
                }

                await Task.Delay(PingIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* сторож не имеет права упасть сам */ }
        }
    }

    private void TryWriteCrashLog(string message)
    {
        try
        {
            // Каталог logs/ создаёт LoggingService, но сторож не должен зависеть
            // от того, успел ли логгер открыться (ранний hang на старте).
            Directory.CreateDirectory(Path.GetDirectoryName(_crashLogPath)!);
            File.AppendAllText(_crashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Ждём выход цикла до dispose CTS: Task.Delay(..., ct) не споткнётся
        // о disposed-источник, и цикл гарантированно ничего не напишет в лог
        // после Dispose. Цикл выходит по отмене сразу — ожидание микро.
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        // Хуки отписываем только с диспетчерного потока; при shutdown диспетчер
        // умирает вместе с хуками — отписка строго best-effort.
        try
        {
            if (_hooksAttached && _dispatcher.CheckAccess())
            {
                var hooks = _dispatcher.Hooks;
                hooks.OperationStarted -= OnOpStarted;
                hooks.OperationCompleted -= OnOpFinished;
                hooks.OperationAborted -= OnOpFinished;
            }
        }
        catch { }
        _cts.Dispose();
    }
}
