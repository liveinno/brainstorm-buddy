// Фасад «изображения в чате» — клей между UI (OverlayWindow, ImageViewerWindow,
// MeetingHistoryWindow, SettingsWindow) и Core (IChatImageStore, транспорт).
// Контракт зафиксирован координатором до реализации: UI-слой пишется ПОД ЭТИ
// сигнатуры. Тяжёлая IO — фоновая; события и поля черновика — только UI-поток.

using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Core.Images;

namespace BrainstormBuddy;

/// <summary>Состояние подготовки одной картинки в черновике.</summary>
public enum ChatImageTileState { Preparing, Ready, Error }

/// <summary>
/// Тайл изображения для биндингов (полоса черновика, пузырь, история).
/// Thumb — декодированное превью (≤640px), создаётся кодеком; полный оригинал
/// грузится по требованию просмотрщика через ChatImageFlow.LoadOriginal*.
/// </summary>
public sealed class ChatImageTileVm : INotifyPropertyChanged
{
    public string ImageId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public string? PreviewPath { get; init; }
    public string? SendPath { get; init; }
    public string Mime { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public long ByteSize { get; init; }
    public long SendByteSize { get; set; }
    private BitmapSource? _thumb;
    /// <summary>Декодированное превью; INPC — тайл обновляет картинку без пересоздания.</summary>
    public BitmapSource? Thumb { get => _thumb; set { _thumb = value; OnPropertyChanged(); } }

    private ChatImageTileState _state = ChatImageTileState.Ready;
    public ChatImageTileState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(IsPreparing)); OnPropertyChanged(nameof(IsError)); }
    }
    public bool IsReady => _state == ChatImageTileState.Ready;
    public bool IsPreparing => _state == ChatImageTileState.Preparing;
    public bool IsError => _state == ChatImageTileState.Error;

    private string? _errorText;
    public string? ErrorText { get => _errorText; set { _errorText = value; OnPropertyChanged(); } }

    /// <summary>«Изображение N» — нумерация выставляет владелец коллекции.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Tooltip: имя + исходные размеры + размер файла (+ отправляемой копии).</summary>
    public string ToolTipText => $"{FileName} · {Width}×{Height} · {FormatBytes(ByteSize)}"
        + (SendByteSize > 0 && SendByteSize != ByteSize ? $" → {FormatBytes(SendByteSize)}" : "");

    public static string FormatBytes(long b) => b switch
    {
        >= 1 << 20 => $"{b / 1048576.0:0.#} МБ",
        >= 1 << 10 => $"{b / 1024.0:0.#} КБ",
        _ => $"{b} Б"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Оркестратор черновика/отправки/истории изображений. Создаётся App при
/// инициализации AttachmentStore; null — хранилище недоступно (UI деградирует
/// молча: вставка картинок игнорируется с уведомлением).
/// Все публичные методы вызываются с UI-потока; тяжёлая IO внутри — фоновая.
/// </summary>
public sealed class ChatImageFlow
{
    private readonly App _app;
    private readonly IChatImageStore _store;
    private IImageCapabilityService? _capabilities;

    /// <summary>Данные последнего снапшота прерванной отправки (для retry из пузыря).</summary>
    public sealed record SentImageSet(string MessageId, string MeetingId, IReadOnlyList<ChatImageTileVm> Tiles);

    public ChatImageFlow(App app, IChatImageStore store)
    {
        _app = app;
        _store = store;
    }

    /// <summary>Сервис определения поддержки картинок (подключается App после создания).</summary>
    internal void SetCapabilityService(IImageCapabilityService? svc) => _capabilities = svc;

    /// <summary>Изменился состав черновика → UI перечитывает DraftTiles/DraftNotice.</summary>
    public event Action? DraftChanged;

    /// <summary>Тайлы текущего черновика (порядок = порядок вставки).</summary>
    public IReadOnlyList<ChatImageTileVm> DraftTiles => _draft;
    private readonly List<ChatImageTileVm> _draft = new();

    /// <summary>Подсказка возле поля ввода (напр. «Поддержка изображений не
    /// подтверждена…», «Изображение исключено из контекста»). null — скрыть.</summary>
    public string? DraftNotice { get; private set; }

    /// <summary>Установить/снять подсказку извне flow (App при возврате черновика).</summary>
    public void SetDraftNotice(string? notice)
    {
        DraftNotice = notice;
        DraftChanged?.Invoke();
    }

    /// <summary>Хранилище — для линковки/резолва из App (SendChatWithImagesAsync).</summary>
    public IChatImageStore Store => _store;

    /// <summary>Тайл по сохранённому id (pause-дренаж, история). null — нет записи.</summary>
    public ChatImageTileVm? GetTile(string imageId)
    {
        var s = _store.Get(imageId);
        return s == null ? null : TileFromStored(s);
    }

    /// <summary>Вернуть тайлы в черновик (отправка не доехала — оверлей закрыт).
    /// Дубли по ImageId пропускаются; перенумерация + persist + событие.</summary>
    public void RestoreTiles(IReadOnlyList<ChatImageTileVm> tiles)
    {
        foreach (var t in tiles)
            if (!_draft.Any(d => d.ImageId == t.ImageId))
                _draft.Add(t);
        RenumberCaptions();
        if (CurrentMeetingId != null) PersistDraft(CurrentMeetingId);
        DraftChanged?.Invoke();
    }

    /// <summary>Есть ли в черновике хотя бы одна картинка.</summary>
    public bool HasDraft => _draft.Count > 0;

    /// <summary>meetingId текущей встречи для привязки черновика (null — встреча не начата;
    /// ленивое создание — через EnsureCurrentMeeting при первой картинке).</summary>
    public string? CurrentMeetingId => _app.MeetingJournal?.CurrentId;

    // ── Вставка / удаление из черновика ─────────────────────────────────────

    /// <summary>
    /// Пользователь нажал Ctrl+V и в буфере есть изображение (BitmapSource или
    /// файл-картинка). Flow сам: проверяет дубль одного действия вставки,
    /// декодирует, сохраняет оригинал (атомарно), готовит превью и send-копию,
    /// кладёт тайл в черновик, сохраняет черновик в БД. Ошибки — тайл Error с
    /// причиной. Вызывается с UI-потока, работа — фоновая.
    /// </summary>
    public void RequestAddDraftFromClipboard()
    {
        var items = ChatImageCodec.GetClipboardImages();
        if (items.Count == 0) { _app.Logger.Info("chat-image: paste — буфер без картинок", "ChatImg"); return; }
        var meetingId = EnsureMeetingForDraft();
        if (meetingId == null) { _app.Logger.Warn("chat-image: paste — нет meeting id, тайл не создан", "ChatImg"); return; } // журнал недоступен — черновику некуда привязаться

        foreach (var (bytes, fileName) in items)
        {
            var tile = new ChatImageTileVm
            {
                ImageId = "",
                FileName = string.IsNullOrWhiteSpace(fileName) ? "clipboard.png" : Path.GetFileName(fileName),
                State = ChatImageTileState.Preparing,
            };
            _draft.Add(tile);
            RenumberCaptions();
            DraftChanged?.Invoke();
            _app.Logger.Info($"chat-image: paste → тайл Preparing ({fileName}, {bytes.Length}Б)", "ChatImg");
            _ = Task.Run(() => PrepareClipboardTileAsync(tile, bytes, meetingId));
        }
    }

    /// <summary>«Добавить к сообщению» из контекстного меню отправленной картинки:
    /// копия ссылки (НЕ перелинковка оригинала) уходит в текущий черновик.</summary>
    public void AddDraftFromStored(string imageId)
    {
        var stored = _store.Get(imageId);
        if (stored == null)
        {
            // Записи нет (GC/битая ссылка) — не молчим: тайл Error с причиной.
            _app.Logger.Warn($"chat-image: «добавить к сообщению» — записи {imageId} нет", "ChatImg");
            _draft.Add(new ChatImageTileVm
            {
                ImageId = imageId,
                State = ChatImageTileState.Error,
                ErrorText = App.TT("L_ImgUnavailable", "Оригинал недоступен"),
            });
            RenumberCaptions();
            if (CurrentMeetingId != null) PersistDraft(CurrentMeetingId);
            DraftChanged?.Invoke();
            return;
        }
        var meetingId = EnsureMeetingForDraft();
        if (meetingId == null) return;
        var tile = TileFromStored(stored);
        _draft.Add(tile);
        // Тайл приходит без Thumb — сразу «Подготовка», превью долетит фоном.
        if (tile.State == ChatImageTileState.Ready && tile.Thumb == null)
            tile.State = ChatImageTileState.Preparing;
        RenumberCaptions();
        PersistDraft(meetingId);
        DraftChanged?.Invoke();
        if (tile.State == ChatImageTileState.Preparing)
            _ = Task.Run(() => HydrateDraftThumbAsync(tile));
    }

    /// <summary>«Убрать из сообщения» у тайла черновика.</summary>
    public void RemoveDraft(string imageId)
    {
        var tile = _draft.FirstOrDefault(t => t.ImageId == imageId);
        if (tile == null) return;
        _draft.Remove(tile);
        RenumberCaptions();
        if (CurrentMeetingId != null) PersistDraft(CurrentMeetingId);
        DraftChanged?.Invoke();
    }

    /// <summary>Восстановить черновик встречи после рестарта/смены встречи
    /// (включая картинки прерванной отправки — как копии ссылок).</summary>
    public void RestoreDraftForCurrentMeeting()
    {
        _draft.Clear();
        DraftNotice = null;
        var meetingId = CurrentMeetingId;
        if (meetingId != null)
        {
            // 1) явный черновик (LoadDraft уже возвращает StoredImage в порядке seq)
            foreach (var s in _store.LoadDraft(meetingId))
                _draft.Add(TileFromStored(s));
            // 2) прерванная отправка: картинки последнего user-сообщения без ответа —
            // возвращаем в черновик как ссылки (без дубля по id).
            var lastUserImgs = _app.MeetingJournal?.FindLastUnansweredImageIds(meetingId);
            if (lastUserImgs != null)
            {
                foreach (var id in lastUserImgs)
                {
                    if (_draft.Any(t => t.ImageId == id)) continue;
                    var stored = _store.Get(id);
                    if (stored != null) _draft.Add(TileFromStored(stored));
                }
                if (lastUserImgs.Count > 0)
                    DraftNotice = App.TT("L_ImgInterruptedNotice",
                        "Отправка была прервана — изображения возвращены в черновик");
            }
        }
        RenumberCaptions();
        if (meetingId != null) PersistDraft(meetingId);
        DraftChanged?.Invoke();
        // Превью тайлов грузим фоново — thumb может отсутствовать в первый кадр.
        _ = Task.Run(LoadMissingThumbsAsync);
    }

    // ── Отправка ────────────────────────────────────────────────────────────

    /// <summary>
    /// «Отправить» из поля ввода. Снапшотит текст+картинки одним неизменяемым
    /// набором (messageId, meetingId, модель, профиль). Без картинок и без текста —
    /// no-op. Только картинки без текста — легальный запрос (LLM просят описать).
    /// Проверки до очистки поля: готовность файлов, поддержка адаптера, LLM.
    /// </summary>
    public async Task SendChatAsync(string text)
    {
        text = text?.Trim() ?? "";
        var ready = _draft.Where(t => t.State == ChatImageTileState.Ready).ToList();
        if (ready.Count == 0 && text.Length == 0) return;

        // «LLM выкл»: черновик НЕ теряем — подсказка вместо тихой отправки.
        if (_app.Config.Ui.LlmDisabled && (ready.Count > 0 || text.Length > 0))
        {
            DraftNotice = App.TT("L_ImgLlmOff", "LLM выключен. Включите LLM, чтобы отправить сообщение");
            DraftChanged?.Invoke();
            _app.PushOverlayToast(ToastKind.Info,
                App.TT("L_Toast_LlmOff", "LLM выключен"), DraftNotice);
            return;
        }

        var meetingId = EnsureMeetingForDraft() ?? "";
        var messageId = Guid.NewGuid().ToString("N");
        var sentSet = new SentImageSet(messageId, meetingId, ready);

        // Гейт по поддержке: Unsupported — не отправляем (текст поля не очищаем,
        // черновик остаётся; «Выбрать модель» открывает настройки — UI-слой
        // читает это же состояние через GetSupportInfo).
        if (ready.Count > 0)
        {
            var support = GetSupportInfo();
            if (support.State == ImageSupportState.Unsupported)
            {
                DraftNotice = App.TT("L_ImgUnsupportedBlocked", "Выбранная модель не принимает изображения");
                DraftChanged?.Invoke();
                return;
            }
            if (support.State == ImageSupportState.Unknown)
                DraftNotice = App.TT("L_ImgDraftUnknown",
                    "Поддержка изображений не подтверждена. Проверим при отправке");
            else
                DraftNotice = null;
            DraftChanged?.Invoke();
        }

        // Снимаем отправленные из черновика ДО async-части (ошибочные тайлы остаются).
        foreach (var t in ready) _draft.Remove(t);
        RenumberCaptions();
        if (CurrentMeetingId != null) PersistDraft(CurrentMeetingId);
        DraftChanged?.Invoke();

        await _app.SendChatWithImagesAsync(text, sentSet);
    }

    // ── Поддержка изображений (ТЗ §8) ───────────────────────────────────────

    private ImageCapabilityKey CurrentKey()
    {
        // Один источник ключа для UI и транспорта: вердикт, который
        // OpenAiClient записал через RecordApiResult(KeyFor), читается здесь
        // же через GetCached(KeyFor). Своя реализация ключа = кэш-промахи.
        return ImageCapabilityService.KeyFor(_app.Config.Api);
    }

    /// <summary>Кэшированное состояние для текущей пары endpoint+модель+профиль.</summary>
    public ImageSupportInfo GetSupportInfo()
        => _capabilities?.GetCached(CurrentKey())
           ?? new ImageSupportInfo(ImageSupportState.Unknown, ImageSupportSource.None, default, null);

    /// <summary>Кнопка «Проверить изображения» в настройках (синтетическая картинка).</summary>
    public async Task<ImageSupportInfo> ProbeImageSupportAsync(CancellationToken ct = default)
    {
        if (_capabilities == null)
            return new ImageSupportInfo(ImageSupportState.Unknown, ImageSupportSource.None, DateTime.UtcNow,
                App.TT("L_ImgProbeNoService", "Проверка недоступна без хранилища"));
        return await _capabilities.ProbeAsync(CurrentKey(), ct);
    }

    // ── Доступ к сохранённым файлам (просмотрщик/история/сохранение) ────────

    public StoredImage? GetStored(string imageId) => _store.Get(imageId);

    /// <summary>Байты оригинала (просмотрщик/«Сохранить как»/буфер). Missing → null.</summary>
    public async Task<byte[]?> LoadOriginalBytesAsync(string imageId)
    {
        var stored = _store.Get(imageId);
        if (stored == null || stored.State == ChatImageState.Missing) return null;
        try { return await File.ReadAllBytesAsync(stored.OriginalPath); }
        catch (Exception ex)
        {
            _app.Logger.Warn($"chat-image: оригинал {imageId} не читается: {ex.Message}", "ChatImg");
            _store.MarkState(imageId, ChatImageState.Missing);
            return null;
        }
    }

    /// <summary>Все картинки встречи по порядку (для секции «Изображения» истории).</summary>
    public IReadOnlyList<StoredImage> GetMeetingImages(string meetingId) => _store.GetForMeeting(meetingId);
    /// <summary>Картинки конкретного сообщения.</summary>
    public IReadOnlyList<StoredImage> GetMessageImages(string meetingId, string messageId)
        => _store.GetForMessage(meetingId, messageId);

    // ── Внутренности ────────────────────────────────────────────────────────

    /// <summary>Черновику нужна встреча: лениво создаём её как при первой реплике.</summary>
    private string? EnsureMeetingForDraft()
    {
        try { return _app.MeetingJournal?.EnsureCurrentMeeting(); }
        catch (Exception ex)
        {
            _app.Logger.Warn($"chat-image: meeting id не получен: {ex.Message}", "ChatImg");
            return null;
        }
    }

    private void RenumberCaptions()
    {
        for (var i = 0; i < _draft.Count; i++)
            _draft[i].Caption = $"Изображение {i + 1}";
    }

    private void PersistDraft(string meetingId)
    {
        try { _store.SaveDraft(meetingId, _draft
            .Where(t => t.ImageId.Length > 0 && t.State != ChatImageTileState.Error)
            .Select(t => t.ImageId).Distinct().ToList()); }
        catch (Exception ex) { _app.Logger.Warn($"chat-image: draft persist: {ex.Message}", "ChatImg"); }
    }

    private static ChatImageTileVm TileFromStored(StoredImage s) => new()
    {
        ImageId = s.Ref.ImageId,
        FileName = s.Ref.FileName,
        OriginalPath = s.OriginalPath,
        PreviewPath = s.PreviewPath,
        SendPath = s.SendPath,
        Mime = s.Ref.MimeType,
        Width = s.Ref.Width,
        Height = s.Ref.Height,
        ByteSize = s.Ref.ByteSize,
        SendByteSize = s.SendByteSize,
        State = s.State == ChatImageState.Missing ? ChatImageTileState.Error : ChatImageTileState.Ready,
        ErrorText = s.State == ChatImageState.Missing
            ? App.TT("L_ImgUnavailable", "Оригинал недоступен") : null,
    };

    /// <summary>Декод → сохранение оригинала → превью + send-копия → Ready.
    /// Любой сбой — тайл в Error с причиной (остаётся в черновике, в отправку не идёт).</summary>
    private async Task PrepareClipboardTileAsync(ChatImageTileVm tile, byte[] bytes, string meetingId)
    {
        try
        {
            var decoded = ChatImageCodec.TryDecode(bytes, tile.FileName);
            if (decoded == null) throw new InvalidDataException("неподдерживаемый формат (нужны PNG/JPEG/BMP/GIF/ICO)");
            var stored = await Task.Run(() =>
                _store.SaveOriginal(bytes, tile.FileName, decoded.Mime, decoded.W, decoded.H));
            var imageId = stored.Ref.ImageId;
            var dir = Path.GetDirectoryName(stored.OriginalPath)!;

            var preview = await Task.Run(() => ChatImageCodec.MakePreviewPng(bytes, decoded.W, decoded.H));
            var previewPath = Path.Combine(dir, "preview.png");
            await File.WriteAllBytesAsync(previewPath, preview);
            _store.RegisterPreview(imageId, previewPath);

            var send = await Task.Run(() => ChatImageCodec.MakeSendCopy(bytes, decoded.Mime, decoded.W, decoded.H));
            string sendPath;
            if (!ReferenceEquals(send.Bytes, bytes) && !send.Bytes.AsSpan().SequenceEqual(bytes))
            {
                var ext = send.Mime == "image/jpeg" ? ".jpg" : ".png";
                sendPath = Path.Combine(dir, "send" + ext);
                await File.WriteAllBytesAsync(sendPath, send.Bytes);
                _store.RegisterSendCopy(imageId, sendPath, send.Mime, send.W, send.H, send.Bytes.LongLength);
            }
            else
            {
                sendPath = stored.OriginalPath; // оригинал уже в лимитах — копия не нужна
                _store.RegisterSendCopy(imageId, sendPath, stored.Ref.MimeType, stored.Ref.Width, stored.Ref.Height, stored.Ref.ByteSize);
            }
            var final = _store.Get(imageId)!;
            _app.Logger.Info($"chat-image: {imageId} decode+store ok, готовлю Ready-своп на UI", "ChatImg");

            var thumb = await Task.Run(() => ChatImageCodec.ToBitmap(preview));
            await _app.Dispatcher.InvokeAsync(() =>
            {
                _draft.Remove(tile);
                _draft.Add(new ChatImageTileVm
                {
                    ImageId = imageId, FileName = tile.FileName,
                    OriginalPath = final.OriginalPath, PreviewPath = previewPath, SendPath = sendPath,
                    Mime = final.Ref.MimeType, Width = final.Ref.Width, Height = final.Ref.Height,
                    ByteSize = final.Ref.ByteSize, SendByteSize = final.SendByteSize,
                    Thumb = thumb, State = ChatImageTileState.Ready,
                });
                RenumberCaptions();
                PersistDraft(meetingId);
                DraftChanged?.Invoke();
            });
            _app.Logger.Info($"chat-image: {imageId} Ready-своп выполнен, тайл обновлён", "ChatImg");
        }
        catch (Exception ex)
        {
            _app.Logger.Warn($"chat-image: подготовка вставки не удалась: {ex.Message}", "ChatImg");
            await _app.Dispatcher.InvokeAsync(() =>
            {
                tile.State = ChatImageTileState.Error;
                tile.ErrorText = ex.Message;
                DraftChanged?.Invoke();
            });
        }
    }

    /// <summary>Поднять превью тайла из хранилища: preview.png, иначе декод оригинала
    /// на лету. Файл недоступен → MarkState(Missing) + тайл Error (не молчим).
    /// Preparing→Ready по факту загрузки; мутации тайла — на UI-потоке.</summary>
    private async Task HydrateDraftThumbAsync(ChatImageTileVm tile)
    {
        BitmapSource? bmp = null;
        var gone = false;
        try
        {
            var preview = tile.PreviewPath;
            if (preview != null && File.Exists(preview))
                bmp = await Task.Run(() => ChatImageCodec.ToBitmap(File.ReadAllBytes(preview)));
            if (bmp == null)
            {
                if (!File.Exists(tile.OriginalPath))
                {
                    gone = true;
                }
                else
                {
                    var bytes = await File.ReadAllBytesAsync(tile.OriginalPath);
                    bmp = await Task.Run(() => ChatImageCodec.TryDecode(bytes, tile.FileName)?.Bitmap);
                    gone = bmp == null;
                }
            }
        }
        catch (Exception ex) { _app.Logger.Debug($"chat-image: thumb {tile.ImageId}: {ex.Message}", "ChatImg"); }

        await _app.Dispatcher.InvokeAsync(() =>
        {
            if (bmp != null) tile.Thumb = bmp;
            if (gone)
            {
                try { _store.MarkState(tile.ImageId, ChatImageState.Missing); }
                catch (Exception ex) { _app.Logger.Debug($"chat-image: mark missing {tile.ImageId}: {ex.Message}", "ChatImg"); }
                tile.State = ChatImageTileState.Error;
                tile.ErrorText ??= App.TT("L_ImgUnavailable", "Оригинал недоступен");
            }
            else if (tile.State == ChatImageTileState.Preparing)
            {
                tile.State = ChatImageTileState.Ready;
            }
            DraftChanged?.Invoke();
        });
    }

    private async Task LoadMissingThumbsAsync()
    {
        foreach (var tile in _draft.Where(t => t.Thumb == null && t.State != ChatImageTileState.Error).ToList())
            await HydrateDraftThumbAsync(tile);
    }
}
