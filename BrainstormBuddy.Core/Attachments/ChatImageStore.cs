using System.Security.Cryptography;
using BrainstormBuddy.Core.Images;
using Microsoft.Data.Sqlite;

namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Изображения чата поверх attachments.db v2 (контракты — BrainstormBuddy.Core.Images).
/// Файлы: attachments\images\img-&lt;guidN&gt;\original.&lt;ext&gt; + preview.png + send.&lt;ext&gt;.
/// Метаданные — chat_images, связи — message_image_links/draft_images,
/// кэш возможностей моделей — image_capability_cache/catalog_refresh.
///
/// Атомарность SaveOriginal: tmp-файл в каталоге картинки → File.Move в
/// original.&lt;ext&gt; → INSERT под _gate. Краш между move и INSERT оставляет
/// сиротский каталог, который снимает RecoverOnStartup. Файловый IO держим
/// вне _gate, БД — внутри (то же правило, что у документов).
/// </summary>
public sealed partial class AttachmentStore
{
    private const string ImageIdPrefix = "img-";
    private const string ImagesDirName = "images";

    /// <summary>TTL записи кэша возможностей (ТЗ §8: до семи дней).</summary>
    private static readonly TimeSpan CapabilityTtl = TimeSpan.FromDays(7);

    private string ImagesRootDir => Path.Combine(_root, ImagesDirName);
    private string ImageDirFor(string imageId) => Path.Combine(ImagesRootDir, imageId);

    // ---------------- оригиналы и копии ----------------

    /// <inheritdoc cref="IChatImageStore.SaveOriginal"/>
    public StoredImage SaveOriginal(byte[] bytes, string fileName, string mime, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentException.ThrowIfNullOrEmpty(mime);
        ThrowIfDisposed();

        var imageId = ImageIdPrefix + Guid.NewGuid().ToString("N");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var created = DateTime.UtcNow;
        var ext = ImageExtensionFor(mime, fileName);
        var dir = ImageDirFor(imageId);
        var originalPath = Path.Combine(dir, "original" + ext);

        // tmp и оригинал в одном каталоге → File.Move атомарен в пределах тома;
        // процесс умер до INSERT — весь img-* каталог подметёт RecoverOnStartup.
        var tmpPath = Path.Combine(dir, imageId + ".tmp-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(tmpPath, bytes);
            File.Move(tmpPath, originalPath, overwrite: false);

            lock (_gate)
                InsertImageLocked(imageId, fileName, mime, width, height, bytes.Length,
                    sha, originalPath, created);
        }
        catch
        {
            TryDelete(tmpPath);
            TryDeleteOrphanDir(dir);
            throw;
        }

        return new StoredImage(
            new ChatImageRef(imageId, fileName, mime, width, height, bytes.Length),
            OriginalPath: originalPath,
            PreviewPath: null,
            SendPath: originalPath,    // копия ещё не зарегистрирована → send = оригинал
            SendMime: mime,
            SendWidth: width,
            SendHeight: height,
            SendByteSize: bytes.Length,
            State: ChatImageState.Ready,
            Sha256: sha,
            CreatedUtc: created);
    }

    /// <inheritdoc cref="IChatImageStore.RegisterPreview"/>
    public void RegisterPreview(string imageId, string previewPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageId);
        ArgumentException.ThrowIfNullOrEmpty(previewPath);
        ThrowIfDisposed();
        if (!File.Exists(previewPath))
            throw new InvalidOperationException(
                $"preview-файл не найден: {previewPath} — мусор в БД не пишем");

        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE chat_images SET preview_path=$p WHERE image_id=$id;";
            cmd.Parameters.AddWithValue("$p", previewPath);
            cmd.Parameters.AddWithValue("$id", imageId);
            if (cmd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException($"изображение {imageId} не найдено в хранилище");
        }
    }

    /// <inheritdoc cref="IChatImageStore.RegisterSendCopy"/>
    public void RegisterSendCopy(string imageId, string sendPath, string sendMime,
        int sendWidth, int sendHeight, long sendByteSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageId);
        ArgumentException.ThrowIfNullOrEmpty(sendPath);
        ArgumentException.ThrowIfNullOrEmpty(sendMime);
        ThrowIfDisposed();
        if (!File.Exists(sendPath))
            throw new InvalidOperationException(
                $"send-копия не найдена: {sendPath} — мусор в БД не пишем");

        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE chat_images SET send_path=$p, send_mime=$m, send_width=$w,
                    send_height=$h, send_byte_size=$s WHERE image_id=$id;
                """;
            cmd.Parameters.AddWithValue("$p", sendPath);
            cmd.Parameters.AddWithValue("$m", sendMime);
            cmd.Parameters.AddWithValue("$w", sendWidth);
            cmd.Parameters.AddWithValue("$h", sendHeight);
            cmd.Parameters.AddWithValue("$s", sendByteSize);
            cmd.Parameters.AddWithValue("$id", imageId);
            if (cmd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException($"изображение {imageId} не найдено в хранилище");
        }
    }

    // ---------------- чтение ----------------

    // Сигнатура конфликтует с Get(string documentId) → StoredDocument: совпадают
    // имя и параметры → полная запись отдаётся явным методом, интерфейсный
    // Get реализован явно.
    StoredImage? IChatImageStore.Get(string imageId) => GetImage(imageId);

    /// <summary>Полная запись изображения по id (null — не существует).</summary>
    public StoredImage? GetImage(string imageId)
    {
        lock (_gate)
            return GetImageLocked(imageId);
    }

    /// <inheritdoc cref="IChatImageStore.GetForMessage"/>
    public IReadOnlyList<StoredImage> GetForMessage(string meetingId, string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ImgCols("i")} FROM message_image_links l
                JOIN chat_images i ON i.image_id = l.image_id
                WHERE l.meeting_id = $m AND l.message_id = $d
                ORDER BY l.seq;
                """;
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$d", messageId);
            return ReadImages(cmd);
        }
    }

    /// <inheritdoc cref="IChatImageStore.GetForMeeting"/>
    public IReadOnlyList<StoredImage> GetForMeeting(string meetingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ImgCols("i")} FROM chat_images i
                WHERE i.image_id IN (
                    SELECT image_id FROM message_image_links WHERE meeting_id = $m
                    UNION
                    SELECT image_id FROM draft_images WHERE meeting_id = $m)
                ORDER BY i.created_utc, i.image_id;
                """;
            cmd.Parameters.AddWithValue("$m", meetingId);
            return ReadImages(cmd);
        }
    }

    // ---------------- связи с сообщениями ----------------

    /// <inheritdoc cref="IChatImageStore.Link"/>
    public void Link(string imageId, string meetingId, string messageId, int seq)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageId);
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ThrowIfDisposed();

        lock (_gate)
        {
            using (var sel = _db.CreateCommand())
            {
                sel.CommandText = """
                    SELECT meeting_id, seq FROM message_image_links
                    WHERE image_id=$i AND message_id=$d;
                    """;
                sel.Parameters.AddWithValue("$i", imageId);
                sel.Parameters.AddWithValue("$d", messageId);
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    if (r.GetString(0) == meetingId && r.GetInt32(1) == seq)
                        return;    // повторная идентичная привязка — no-op
                    throw new InvalidOperationException(
                        $"изображение {imageId} уже привязано к {messageId} с другой встречей/seq");
                }
            }

            // FK на chat_images: левый imageId отклоняется SqliteException — у
            // привязки без оригинала нет смысла.
            using var ins = _db.CreateCommand();
            ins.CommandText = """
                INSERT OR IGNORE INTO message_image_links(image_id, meeting_id, message_id, seq)
                VALUES($i, $m, $d, $s);
                """;
            ins.Parameters.AddWithValue("$i", imageId);
            ins.Parameters.AddWithValue("$m", meetingId);
            ins.Parameters.AddWithValue("$d", messageId);
            ins.Parameters.AddWithValue("$s", seq);
            ins.ExecuteNonQuery();
        }
    }

    /// <inheritdoc cref="IChatImageStore.UnlinkMessage"/>
    public void UnlinkMessage(string meetingId, string messageId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM message_image_links
                WHERE meeting_id=$m AND message_id=$d;
                """;
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$d", messageId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc cref="IChatImageStore.UnlinkMeeting"/>
    public void UnlinkMeeting(string meetingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        lock (_gate)
        {
            // Снимаем ВСЕ ссылки встречи: связи сообщений + сохранённый черновик
            // (черновик — тоже ссылка на встречу; удалённая встреча не должна
            // восстанавливать черновик и удерживать оригиналы от GC).
            using var links = _db.CreateCommand();
            links.CommandText = "DELETE FROM message_image_links WHERE meeting_id=$m;";
            links.Parameters.AddWithValue("$m", meetingId);
            links.ExecuteNonQuery();

            using var draft = _db.CreateCommand();
            draft.CommandText = "DELETE FROM draft_images WHERE meeting_id=$m;";
            draft.Parameters.AddWithValue("$m", meetingId);
            draft.ExecuteNonQuery();
        }
    }

    // ---------------- черновик ----------------

    /// <inheritdoc cref="IChatImageStore.SaveDraft"/>
    public void SaveDraft(string meetingId, IReadOnlyList<string> imageIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        ArgumentNullException.ThrowIfNull(imageIds);
        ThrowIfDisposed();

        lock (_gate)
        {
            // Полная замена набора одной транзакцией: краш не оставляет
            // полупустой черновик. FK отклоняет imageId без оригинала.
            using var tx = _db.BeginTransaction();
            using var del = _db.CreateCommand();
            del.CommandText = "DELETE FROM draft_images WHERE meeting_id=$m;";
            del.Parameters.AddWithValue("$m", meetingId);
            del.ExecuteNonQuery();

            for (var i = 0; i < imageIds.Count; i++)
            {
                using var ins = _db.CreateCommand();
                ins.CommandText = """
                    INSERT INTO draft_images(meeting_id, image_id, seq)
                    VALUES($m, $i, $s);
                    """;
                ins.Parameters.AddWithValue("$m", meetingId);
                ins.Parameters.AddWithValue("$i", imageIds[i]);
                ins.Parameters.AddWithValue("$s", i);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <inheritdoc cref="IChatImageStore.LoadDraft"/>
    public IReadOnlyList<StoredImage> LoadDraft(string meetingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ImgCols("i")} FROM draft_images d
                JOIN chat_images i ON i.image_id = d.image_id
                WHERE d.meeting_id = $m
                ORDER BY d.seq;
                """;
            cmd.Parameters.AddWithValue("$m", meetingId);
            return ReadImages(cmd);
        }
    }

    /// <inheritdoc cref="IChatImageStore.ClearDraft"/>
    public void ClearDraft(string meetingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(meetingId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM draft_images WHERE meeting_id=$m;";
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- GC ----------------

    /// <inheritdoc cref="IChatImageStore.AllLinkedIds"/>
    public IReadOnlyCollection<string> AllLinkedIds()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT image_id FROM message_image_links
                UNION
                SELECT image_id FROM draft_images;
                """;
            var set = new HashSet<string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                set.Add(r.GetString(0));
            return set;
        }
    }

    /// <inheritdoc cref="IChatImageStore.CollectGarbage"/>
    public IReadOnlyList<string> CollectGarbage()
    {
        ThrowIfDisposed();
        List<string> orphans;
        lock (_gate)
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT image_id FROM chat_images
                    WHERE image_id NOT IN (SELECT image_id FROM message_image_links)
                      AND image_id NOT IN (SELECT image_id FROM draft_images);
                    """;
                orphans = [];
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    orphans.Add(r.GetString(0));
            }
            if (orphans.Count == 0)
                return orphans;

            // Пачка IN-параметров — под лимит SQLITE_MAX_VARIABLE_NUMBER.
            const int chunk = 500;
            for (var off = 0; off < orphans.Count; off += chunk)
            {
                var part = orphans.Skip(off).Take(chunk).ToList();
                var names = string.Join(", ", part.Select((_, i) => $"$id{i}"));
                using var del = _db.CreateCommand();
                del.CommandText = $"DELETE FROM chat_images WHERE image_id IN ({names});";
                for (var i = 0; i < part.Count; i++)
                    del.Parameters.AddWithValue($"$id{i}", part[i]);
                del.ExecuteNonQuery();
            }
        }

        // Каталоги сносим вне lock'а: медленный/битый диск не держит БД;
        // провал оставляет сиротский img-* каталог — подметёт RecoverOnStartup.
        foreach (var id in orphans)
            TryDeleteOrphanDir(ImageDirFor(id));
        return orphans;
    }

    /// <inheritdoc cref="IChatImageStore.MarkState"/>
    public void MarkState(string imageId, ChatImageState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageId);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE chat_images SET state=$s WHERE image_id=$id;";
            cmd.Parameters.AddWithValue("$s", state.ToString());
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- кэш возможностей (IImageCapabilityCache) ----------------

    // Ещё один конфликт сигнатур: Get(string) → StoredDocument уже занято.
    ImageSupportInfo? IImageCapabilityCache.Get(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT state, source, checked_utc, note
                FROM image_capability_cache WHERE cache_key=$k;
                """;
            cmd.Parameters.AddWithValue("$k", cacheKey);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            var state = Enum.TryParse<ImageSupportState>(r.GetString(0), out var st)
                ? st : ImageSupportState.Unknown;
            var source = Enum.TryParse<ImageSupportSource>(r.GetString(1), out var so)
                ? so : ImageSupportSource.None;
            var checkedUtc = ParseIso(r.GetString(2));
            var note = r.IsDBNull(3) ? null : r.GetString(3);
            // Протухшая запись — как будто её нет; строку перезапишет Set.
            if (DateTime.UtcNow - checkedUtc > CapabilityTtl)
                return null;
            return new ImageSupportInfo(state, source, checkedUtc, note);
        }
    }

    void IImageCapabilityCache.Set(string cacheKey, ImageSupportInfo info, string keyJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO image_capability_cache(cache_key, state, source, checked_utc, note, key_json)
                VALUES($k, $st, $so, $t, $n, $j)
                ON CONFLICT(cache_key) DO UPDATE SET
                    state=$st, source=$so, checked_utc=$t, note=$n, key_json=$j;
                """;
            cmd.Parameters.AddWithValue("$k", cacheKey);
            cmd.Parameters.AddWithValue("$st", info.State.ToString());
            cmd.Parameters.AddWithValue("$so", info.Source.ToString());
            cmd.Parameters.AddWithValue("$t", Iso(info.CheckedUtc));
            cmd.Parameters.AddWithValue("$n", (object?)info.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$j", (object?)keyJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    DateTime? IImageCapabilityCache.CatalogRefreshedUtc(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT refreshed_utc FROM catalog_refresh WHERE scope=$s;";
            cmd.Parameters.AddWithValue("$s", scope);
            var v = cmd.ExecuteScalar();
            return v is string s ? ParseIso(s) : null;
        }
    }

    void IImageCapabilityCache.MarkCatalogRefreshed(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO catalog_refresh(scope, refreshed_utc) VALUES($s, $t)
                ON CONFLICT(scope) DO UPDATE SET refreshed_utc=$t;
                """;
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$t", Iso(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- internals ----------------

    private static readonly string[] ImgColsArr =
        ["image_id", "file_name", "mime", "width", "height", "byte_size",
         "sha256", "state", "original_path", "preview_path", "send_path",
         "send_mime", "send_width", "send_height", "send_byte_size", "created_utc"];

    private static string ImgCols(string alias) =>
        string.Join(", ", ImgColsArr.Select(c => alias.Length == 0 ? c : alias + "." + c));

    private void InsertImageLocked(string imageId, string fileName, string mime,
        int width, int height, long byteSize, string sha, string originalPath, DateTime createdUtc)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_images(image_id, file_name, mime, width, height,
                byte_size, sha256, state, original_path, created_utc)
            VALUES($id, $name, $mime, $w, $h, $size, $sha, 'Ready', $path, $created);
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.AddWithValue("$mime", mime);
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$size", byteSize);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$path", originalPath);
        cmd.Parameters.AddWithValue("$created", Iso(createdUtc));
        cmd.ExecuteNonQuery();
    }

    private StoredImage? GetImageLocked(string imageId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT {ImgCols("")} FROM chat_images WHERE image_id=$id;";
        cmd.Parameters.AddWithValue("$id", imageId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadImage(r) : null;
    }

    private bool ImageRowExistsLocked(string imageId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chat_images WHERE image_id=$id;";
        cmd.Parameters.AddWithValue("$id", imageId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private List<StoredImage> ReadImages(SqliteCommand cmd)
    {
        var list = new List<StoredImage>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadImage(r));
        return list;
    }

    private static StoredImage ReadImage(SqliteDataReader r)
    {
        var imageId = r.GetString(0);
        var fileName = r.GetString(1);
        var mime = r.GetString(2);
        var w = r.GetInt32(3);
        var h = r.GetInt32(4);
        var size = r.GetInt64(5);
        var sha = r.GetString(6);
        var state = Enum.TryParse<ChatImageState>(r.GetString(7), out var st)
            ? st : ChatImageState.Missing;
        var originalPath = r.GetString(8);
        var previewPath = r.IsDBNull(9) ? null : r.GetString(9);
        var sendPath = r.IsDBNull(10) ? null : r.GetString(10);
        var sendMime = r.IsDBNull(11) ? null : r.GetString(11);
        var sendW = r.IsDBNull(12) ? 0 : r.GetInt32(12);
        var sendH = r.IsDBNull(13) ? 0 : r.GetInt32(13);
        var sendSize = r.IsDBNull(14) ? 0L : r.GetInt64(14);
        var created = ParseIso(r.GetString(15));

        // send_* не зарегистрированы → отправлять будем сам оригинал.
        var hasSend = sendPath != null;
        return new StoredImage(
            new ChatImageRef(imageId, fileName, mime, w, h, size),
            originalPath,
            previewPath,
            sendPath ?? originalPath,
            hasSend ? sendMime! : mime,
            hasSend ? sendW : w,
            hasSend ? sendH : h,
            hasSend ? sendSize : size,
            state,
            sha,
            created);
    }

    /// <summary>Расширение файла оригинала: mime приоритетнее, иначе из имени
    /// (png/jpg/jpeg/bmp/gif/webp), неизвестное → .bin.</summary>
    private static string ImageExtensionFor(string mime, string fileName)
    {
        var ext = mime.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
            "image/bmp" or "image/x-ms-bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null
        };
        if (ext != null) return ext;

        var fe = Path.GetExtension(fileName).ToLowerInvariant();
        return fe is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"
            ? fe : ".bin";
    }
}
