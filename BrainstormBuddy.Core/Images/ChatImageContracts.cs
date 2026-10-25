// Контракты функции «изображения в чате» (ТЗ: .work/feature-chat-images/IMAGE-CHAT-SPEC.md).
// Файл — единственная точка согласования трёх срезов работ:
//   storage (AttachmentStore+журнал), transport (OpenAiClient+капабилити), UI.
// Менять контракты — только согласованно с координатором.

namespace BrainstormBuddy.Core.Images;

/// <summary>
/// Ссылка на сохранённое изображение чата. Ходит в ChatMessage.Images,
/// журнал встречи (imageIds), пузырь истории. Байты картинки сюда НЕ входят —
/// достаются из IChatImageStore по ImageId.
/// </summary>
public sealed record ChatImageRef(
    string ImageId,
    string FileName,
    string MimeType,
    int Width,
    int Height,
    long ByteSize);

/// <summary>Состояние файла оригинала на диске.</summary>
public enum ChatImageState { Ready, Missing }

/// <summary>Три состояния поддержки входных изображений (ТЗ §8).</summary>
public enum ImageSupportState { Supported, Unsupported, Unknown }

/// <summary>Источник сведений о поддержке (ТЗ §8): каталог провайдера,
/// явная проверка запросом, зафиксированный отказ API.</summary>
public enum ImageSupportSource { None, ProviderCatalog, ExplicitProbe, ApiRefusal }

/// <summary>Записанное состояние поддержки + откуда оно взято и когда.</summary>
public sealed record ImageSupportInfo(
    ImageSupportState State,
    ImageSupportSource Source,
    DateTime CheckedUtc,
    string? Note);

/// <summary>
/// Полная запись об изображении в хранилище. Пути — абсолютные под
/// AppDataDir\attachments\images\img-&lt;guid&gt;\. SendPath — зарегистрированная
/// копия для отправки (может совпадать с OriginalPath, если оригинал укладывается
/// в лимиты). SendMime/SendWidth/SendHeight/SendByteSize описывают отправляемую
/// копию, а не оригинал.
/// </summary>
public sealed record StoredImage(
    ChatImageRef Ref,
    string OriginalPath,
    string? PreviewPath,
    string? SendPath,
    string SendMime,
    int SendWidth,
    int SendHeight,
    long SendByteSize,
    ChatImageState State,
    string Sha256,
    DateTime CreatedUtc);

/// <summary>
/// Хранилище изображений чата поверх attachments.db (ТЗ §6).
/// Реализация: AttachmentStore (миграция user_version 1→2, таблицы
/// chat_images/message_image_links/draft_images — идемпотентно).
/// Все методы потокобезопасны, IO никогда не выполняется на UI-потоке
/// вызывающим кодом (координатор оборачивает в Task.Run).
/// </summary>
public interface IChatImageStore
{
    /// <summary>
    /// Сохранить оригинал побайтово: временный файл + атомарное переименование.
    /// SHA-256 считается здесь. Для bitmap из буфера байты — PNG (кодек до вызова).
    /// Регистрирует запись с State=Ready. Отправляемая копия по умолчанию =
    /// оригинал (Send* = исходные размеры/байты), уточняется RegisterSendCopy.
    /// </summary>
    StoredImage SaveOriginal(byte[] bytes, string fileName, string mime, int width, int height);

    /// <summary>Записать/перезаписать preview.png рядом с оригиналом (≤640 по длинной
    /// стороне, создаётся кодеком вызывающего — стор только фиксирует путь).</summary>
    void RegisterPreview(string imageId, string previewPath);

    /// <summary>Зарегистрировать подготовленную копию для отправки (ресайз/JPEG92).
    /// Вызывается когда копия отличается от оригинала.</summary>
    void RegisterSendCopy(string imageId, string sendPath, string sendMime,
        int sendWidth, int sendHeight, long sendByteSize);

    StoredImage? Get(string imageId);
    /// <summary>Изображения сообщения в порядке seq.</summary>
    IReadOnlyList<StoredImage> GetForMessage(string meetingId, string messageId);
    /// <summary>Все изображения встречи (сообщения + черновик), по времени.</summary>
    IReadOnlyList<StoredImage> GetForMeeting(string meetingId);

    /// <summary>Привязка к сообщению. Идемпотентна по (messageId, imageId):
    /// повторная запись с тем же seq — no-op, с другим — ошибка вызывающего,
    /// дубликата в БД не возникает.</summary>
    void Link(string imageId, string meetingId, string messageId, int seq);

    /// <summary>Снять ссылки сообщения. Оригиналы без ссылок остаются до GC.</summary>
    void UnlinkMessage(string meetingId, string messageId);
    /// <summary>Снять ВСЕ ссылки встречи (удаление встречи). Не трогает файлы —
    /// удалением занимается CollectGarbage.</summary>
    void UnlinkMeeting(string meetingId);

    /// <summary>Черновик вставленных, но неотправленных изображений встречи.
    /// Сохраняет порядок. Переживает рестарт; НЕ переносится на другую встречу.</summary>
    void SaveDraft(string meetingId, IReadOnlyList<string> imageIds);
    IReadOnlyList<StoredImage> LoadDraft(string meetingId);
    /// <summary>Снять черновик после принятия отправки (не удаляет сами записи).</summary>
    void ClearDraft(string meetingId);

    /// <summary>Все imageId, на которые есть хоть одна ссылка (сообщение или черновик).</summary>
    IReadOnlyCollection<string> AllLinkedIds();
    /// <summary>Удалить файлы и записи оригиналов без ссылок. Вызывать вне UI-потока,
    /// пропуская исключённые защитой (активный просмотр/отправка — вызывающий
    /// отфильтровывает ids до вызова через AllLinkedIds).</summary>
    IReadOnlyList<string> CollectGarbage();
    /// <summary>Пометить файл отсутствующим/найденным (проверка при чтении истории).</summary>
    void MarkState(string imageId, ChatImageState state);
}

/// <summary>Ключ кэша поддержки изображений (ТЗ §8). Смена endpoint/модели/
/// настройки авторизации — новый ключ, чужой результат не наследуется.</summary>
public sealed record ImageCapabilityKey(
    string ProviderId,
    string BaseUrlNormalized,
    string ModelId,
    string AdapterProtocol,
    string AuthVersion);

/// <summary>Вердикт транспорта после реального запроса (ТЗ §8: 401/403/429/timeout/
/// 413/произвольный 400 — НЕ Unsupported; только структурированный отказ на image).</summary>
public enum ImageApiVerdict { Accepted, ExplicitImageRejection, Inconclusive }

/// <summary>
/// Кэш состояний поддержки (таблица в attachments.db, реализует storage-срез).
/// Ключ — каноническая строка ImageCapabilityKey.CacheKey().
/// </summary>
public interface IImageCapabilityCache
{
    /// <summary>null — записи нет/протухла (>7 дней по CheckedUtc).</summary>
    ImageSupportInfo? Get(string cacheKey);
    void Set(string cacheKey, ImageSupportInfo info, string keyJson);
    /// <summary>Время последнего обновления каталога scope="providerId|baseUrl"
    /// (рейт-лимит каталогов — раз в 24ч). null — не обновлялся.</summary>
    DateTime? CatalogRefreshedUtc(string scope);
    void MarkCatalogRefreshed(string scope);
}

public static class ImageCapabilityKeyExtensions
{
    /// <summary>Канонический ключ кэша: стабильная строка, baseUrl нормализован
    /// (lower, без trailing slash), modelId как есть.</summary>
    public static string CacheKey(this ImageCapabilityKey k)
        => $"{k.ProviderId}|{k.BaseUrlNormalized.TrimEnd('/').ToLowerInvariant()}|{k.ModelId}|{k.AdapterProtocol}|{k.AuthVersion}";
}

/// <summary>
/// Готовая к отправке картинка: байты копии для запроса (base64 делается
/// в транспорте на момент POST — ТЗ §7). ChatImagePayload ≠ ChatImageRef:
/// тут есть байты и именно отправляемые размеры/MIME.
/// </summary>
public sealed record ChatImagePayload(
    string ImageId, string Mime, byte[] Bytes, int Width, int Height);

/// <summary>
/// Сервис определения поддержки изображений (ТЗ §8). Реализация — transport-срез
/// (Core). Все данные через IImageCapabilityCache; сетевые вызовы — только из
/// RefreshCatalogAsync/ProbeAsync и по явному действию пользователя.
/// </summary>
public interface IImageCapabilityService
{
    /// <summary>Мгновенно из кэша. Никогда не ходит в сеть.</summary>
    ImageSupportInfo GetCached(ImageCapabilityKey key);

    /// <summary>Обновить из каталога провайдера (OpenRouter input_modalities /
    /// Ollama /api/show capabilities / обычный /models → Unknown). Соблюдает
    /// 24-часовой рейт-лимит на scope, если не force.</summary>
    Task<ImageSupportInfo> RefreshCatalogAsync(ImageCapabilityKey key, bool force, CancellationToken ct);

    /// <summary>Явная проверка синтетической картинкой по кнопке пользователя.
    /// Неточный ответ → «не удалось подтвердить» (Unknown), НЕ Unsupported.</summary>
    Task<ImageSupportInfo> ProbeAsync(ImageCapabilityKey key, CancellationToken ct);

    /// <summary>Зафиксировать вердикт реального запроса (вызывает транспорт после
    /// ответа API). ExplicitImageRejection → Unsupported на 7 дней.</summary>
    void RecordApiResult(ImageCapabilityKey key, ImageApiVerdict verdict, string? note);
}

/// <summary>Классификатор ошибок API → вердикт по изображениям (ТЗ §8: только
/// структурированные коды/известные ответы конкретного API, не подстроки).</summary>
public static class ImageApiErrorClassifier
{
    /// <summary>Адаптер openai-chat: error.code/error.type + status. Известные
    /// отказы на image: invalid_image, unsupported_image, image_not_supported,
    /// messages.*.content.*.image_url not allowed и т.п. конкретного API.</summary>
    public static ImageApiVerdict Classify(int? httpStatus, string? errorCode, string? errorType, string? errorMessage)
    {
        if (httpStatus is null or >= 200 and < 300) return ImageApiVerdict.Inconclusive;
        // Известные структурированные коды/типы отказа на изображение у
        // openai-совместимых API. Строковый матч — только внутри известного
        // кода/типа запроса (invalid_request_error и т.п.), не голой подстрокой
        // по всему телу: 400 «invalid request» без указания на image — Inconclusive.
        var s = (errorCode ?? "") + "|" + (errorType ?? "");
        var knownCode = s.Contains("invalid_image", StringComparison.OrdinalIgnoreCase)
            || s.Contains("image_not_supported", StringComparison.OrdinalIgnoreCase)
            || s.Contains("unsupported_image", StringComparison.OrdinalIgnoreCase)
            || s.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
            || s.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || s.Contains("invalid_request_error", StringComparison.OrdinalIgnoreCase)
            || s.Contains("invalid_request", StringComparison.OrdinalIgnoreCase)
            || s.Contains("model_not_supported", StringComparison.OrdinalIgnoreCase)
            || s.Contains("not_supported", StringComparison.OrdinalIgnoreCase);
        if (!knownCode) return ImageApiVerdict.Inconclusive;
        var m = errorMessage ?? "";
        var mentionsImage = m.Contains("image", StringComparison.OrdinalIgnoreCase)
            || m.Contains("image_url", StringComparison.OrdinalIgnoreCase)
            || m.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || m.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
            || m.Contains("изображени", StringComparison.OrdinalIgnoreCase)
            || m.Contains("картинк", StringComparison.OrdinalIgnoreCase);
        // Код уже явно про image (invalid_image и т.п.) — отказ без проверки текста.
        var codeIsImage = s.Contains("image", StringComparison.OrdinalIgnoreCase)
            || s.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || s.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
        return codeIsImage || (mentionsImage && knownCode)
            ? ImageApiVerdict.ExplicitImageRejection
            : ImageApiVerdict.Inconclusive;
    }
}
