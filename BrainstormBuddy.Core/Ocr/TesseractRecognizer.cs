using System.Buffers.Binary;
using System.Diagnostics;




using Tesseract;

namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Режим разбора вёрстки для Tesseract. Собственный тип, чтобы <c>PageSegMode</c> из нативной
/// обёртки не протекал в публичный контракт <c>BrainstormBuddy.Core.Ocr</c>.
/// </summary>
public enum TesseractLayoutMode
{
    /// <summary>Полный автоанализ вёрстки (<c>PSM 3</c>) — то, что Tesseract делает по умолчанию.</summary>
    Auto,

    /// <summary>Один блок текста однородной вёрстки (<c>PSM 6</c>).</summary>
    SingleBlock,

    /// <summary>Ровно одна строка (<c>PSM 7</c>).</summary>
    SingleLine,
}

/// <summary>
/// Единственный продуктовый движок на Tesseract 5. Работает по моделям <c>tessdata</c>,
/// лежащим рядом с исполняемым файлом, и распознаёт RU+EN ОДНИМ проходом (<c>rus+eng</c>).
/// </summary>
/// <remarks>
/// ⛔ Ноль сети (ТЗ §8): модели читаются только с диска, ничего не докачивается — ни при
/// какой отладке. Нет моделей — движок переходит в <see cref="RecognizerAvailability.Unavailable"/>.
///
/// Почему один проход <c>rus+eng</c>, а не два движка по языку: смешанная строка вида
/// «Открыть file_name.txt через Ctrl+Shift+P» (пример ТЗ §2.2) при раздельных проходах
/// разваливается — каждый проход уверенно перевирает чужие символы, и склеивать нечего.
/// Единая модель выбирает символ из общего алфавита и на смешанном тексте надёжнее.
///
/// Tesseract сообщает уверенность по каждому слову, поэтому <see cref="RecognizedWord.Confidence"/>
/// здесь настоящая (0..1), а не <see cref="RecognizedWord.UnknownConfidence"/>.
///
/// ⚠️ <see cref="TesseractEngine"/> держит НАТИВНУЮ память (leptonica + tesseract50) и обязан
/// освобождаться — отсюда <see cref="IDisposable"/>. Повторная инициализация тоже освобождает
/// предыдущий движок, иначе каждая смена языков в настройках подтекает мегабайтами.
///
/// ⚠️ Нативные библиотеки лежат в подкаталогах <c>x64/</c> и <c>x86/</c> рядом со сборкой.
/// Под <c>PublishSingleFile</c> их нужно распаковывать на диск
/// (<c>IncludeAllContentForSelfExtract=true</c>), иначе загрузка нативной библиотеки падает
/// уже в рантайме, а не на сборке.
/// </remarks>
public sealed class TesseractRecognizer : ITextRecognizer, IDisposable
{
    /// <summary>Имя подкаталога с моделями — рядом с исполняемым файлом.</summary>
    public const string TessdataFolderName = "tessdata";

    private const string RussianCode = "rus";
    private const string EnglishCode = "eng";

    /// <summary>Нативный движок не потокобезопасен — все обращения к нему под этим замком.</summary>
    private readonly object _gate = new();

    private TesseractEngine? _engine;
    private bool _disposed;

    /// <param name="tessdataPath">
    /// Каталог с моделями. Пусто — <see cref="DefaultTessdataPath"/>. Параметр нужен тестам:
    /// они проверяют поведение при отсутствующем каталоге, не трогая настоящий.
    /// </param>
    public TesseractRecognizer(string? tessdataPath = null)
    {
        TessdataPath = string.IsNullOrWhiteSpace(tessdataPath)
            ? DefaultTessdataPath
            : Path.GetFullPath(tessdataPath);
    }

    /// <summary>
    /// Каталог моделей по умолчанию: <c>tessdata</c> рядом с исполняемым файлом.
    /// Именно этот путь попадает в <see cref="UnavailabilityReason"/>, чтобы пользователю
    /// было куда положить модели, а не «движок недоступен, разбирайтесь сами».
    /// </summary>
    public static string DefaultTessdataPath
    {
        get
        {
            // ⚠️ В single-file publish AppContext.BaseDirectory указывает на каталог
            // РАСПАКОВКИ в %TEMP%\.net\…, а не на папку exe. Модели лежат рядом с exe;
            // при поиске только через BaseDirectory Tesseract не запускался. Поймано живым
            // E2E. Порядок проб: папка exe (боевой single-file) → BaseDirectory (обычная
            // сборка и тесты — им модели копирует CopyToOutputDirectory).
            var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);

            if (!string.IsNullOrEmpty(exeDirectory))
            {
                var nearExe = Path.Combine(exeDirectory, TessdataFolderName);
                if (Directory.Exists(nearExe)) return nearExe;
            }

            return Path.Combine(AppContext.BaseDirectory, TessdataFolderName);
        }
    }

    /// <summary>Фактический каталог моделей этого экземпляра.</summary>
    public string TessdataPath { get; }

    /// <summary>
    /// Режим разбора вёрстки. По умолчанию — <see cref="TesseractLayoutMode.SingleBlock"/>.
    /// </summary>
    /// <remarks>
    /// Почему не <see cref="TesseractLayoutMode.Auto"/>, который стоит в Tesseract по умолчанию:
    /// «Авто» запускает полный анализ вёрстки со сегментацией на колонки. На выделенном
    /// пользователем фрагменте экрана колонок нет, зато есть шанс, что анализатор разрежет
    /// одну строку на «колонки» по широкому пробелу и переставит куски. Замер на корпусе
    /// подтвердил: SingleBlock даёт заметно меньший CER, чем Auto (цифры — в тесте-замере).
    /// </remarks>
    public TesseractLayoutMode Layout { get; init; } = TesseractLayoutMode.SingleBlock;

    /// <summary>
    /// Подсказка движку о разрешении растра, точек на дюйм. <c>0</c> — не задавать (умолчание).
    /// </summary>
    /// <remarks>
    /// ⚠️ Правдоподобная настройка, которая на замере ВРЕДИТ, — потому умолчание нулевое.
    /// Рассуждение «экранный растр не несёт DPI, Tesseract ругается и берёт 70» верное, но
    /// подсказка 300 dpi конфликтует с апскейлом препроцессинга: мелкий текст 10–11 px
    /// приходит уже увеличенным втрое, и по «300 dpi» движок считает глифы гигантскими.
    /// Замер на наборе «мелкий текст»: без подсказки 2.01 %, с <c>user_defined_dpi=300</c> —
    /// 8.26 %, то есть вчетверо хуже и мимо порога 8 %. Оставлено настройкой, а не выпилено:
    /// без апскейла подсказка может пригодиться.
    /// </remarks>
    public int UserDefinedDpi { get; init; }

    /// <summary>
    /// Подключать ли словари и языковую модель Tesseract. По умолчанию — <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Ради смешанного текста (ТЗ §2.2). Русский словарь «исправляет» латинские слова
    /// в русском окружении: «otchet.txt» превращался в «окнемх», «readme.md» — в «readmemd».
    /// Отключение <c>load_system_dawg</c> и <c>load_freq_dawg</c> лишает движок соблазна
    /// подгонять токен под словарную форму. Переменные ИНИЦИАЛИЗАЦИОННЫЕ: <c>SetVariable</c>
    /// после создания движка на них уже не действует, поэтому они идут в конструктор.
    /// </remarks>
    public bool UseLanguageDictionaries { get; init; }

    /// <summary>
    /// Ставить ли <c>eng</c> первым в строке языков. По умолчанию <c>true</c> — «eng+rus».
    /// </summary>
    /// <remarks>
    /// Первый язык комбинации Tesseract считает основным. Порядок «rus+eng» отдавал приоритет
    /// русской модели, и она перевирала латиницу в русском окружении: «otchet.txt» → «окнемх»,
    /// «Ctrl+Shift+P» → «ctrL+shiftee». Замер на наборах гейта (средний CER):
    /// светлая 0.88 → 0.00 %, тёмная 0.44 → 0.00 %, смешанная строка 5.63 → 2.19 %.
    /// Плата — мелкий текст 2.01 → 2.26 %, в пределах порога 8 %.
    /// </remarks>
    public bool EnglishFirst { get; init; } = true;

    public string Name => "Tesseract";

    public RecognizerAvailability Availability { get; private set; } = RecognizerAvailability.Unknown;

    public string? UnavailabilityReason { get; private set; }

    /// <summary>Коды языков, с которыми движок реально поднялся (<c>rus</c>, <c>eng</c>). Пусто, если недоступен.</summary>
    public IReadOnlyList<string> ReadyLanguages { get; private set; } = [];

    /// <summary>Имя файла модели для кода языка — <c>rus</c> → <c>rus.traineddata</c>.</summary>
    public static string DataFileName(string languageCode) => languageCode + ".traineddata";

    public Task InitializeAsync(RecognitionLanguages languages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            DisposeEngine();
            ReadyLanguages = [];

            var codes = CodesFor(languages);

            if (codes.Count == 0)
            {
                MarkUnavailable($"Не запрошен ни один язык распознавания. Модели ожидаются в каталоге {TessdataPath}.");
                return Task.CompletedTask;
            }

            var expected = string.Join(", ", codes.Select(DataFileName));

            if (!Directory.Exists(TessdataPath))
            {
                MarkUnavailable($"Каталог моделей Tesseract не найден: {TessdataPath}. Положите в него файлы: {expected}.");
                return Task.CompletedTask;
            }

            var missing = codes
                .Where(code => !File.Exists(Path.Combine(TessdataPath, DataFileName(code))))
                .Select(DataFileName)
                .ToList();

            if (missing.Count > 0)
            {
                MarkUnavailable($"В каталоге моделей Tesseract {TessdataPath} не хватает файлов: {string.Join(", ", missing)}.");
                return Task.CompletedTask;
            }

            try
            {
                // load_system_dawg / load_freq_dawg — ИНИЦИАЛИЗАЦИОННЫЕ переменные: после
                // создания движка SetVariable на них уже не подействует, отсюда конструктор
                // с initialOptions. setOnlyNonDebugVariables: false — иначе обёртка
                // отфильтрует часть переменных, и отключение словарей молча не доедет.
                var initialOptions = new Dictionary<string, object>
                {
                    ["load_system_dawg"] = UseLanguageDictionaries ? "1" : "0",
                    ["load_freq_dawg"] = UseLanguageDictionaries ? "1" : "0",
                };

                _engine = new TesseractEngine(
                    TessdataPath,
                    LanguageString(codes),
                    EngineMode.Default,
                    Array.Empty<string>(),
                    initialOptions,
                    setOnlyNonDebugVariables: false);

                _engine.DefaultPageSegMode = ToPageSegMode(Layout);

                if (UserDefinedDpi > 0)
                {
                    _engine.SetVariable("user_defined_dpi", UserDefinedDpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                ReadyLanguages = codes;
                Availability = RecognizerAvailability.Available;
                UnavailabilityReason = null;
            }
            catch (Exception ex)
            {
                // Ловим широко СОЗНАТЕЛЬНО: нативная часть падает разными типами —
                // DllNotFoundException и BadImageFormatException (нет x64/tesseract50.dll
                // или разрядность не та), TypeInitializationException (сорвался статический
                // загрузчик нативки), TesseractException (битая модель). Молчать нельзя:
                // без причины в UnavailabilityReason разбирательство начинается с нуля.
                MarkUnavailable(
                    $"Не удалось инициализировать Tesseract с моделями из {TessdataPath}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public Task<RecognitionResult> RecognizeAsync(
        PixelBuffer image,
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (_disposed || Availability != RecognizerAvailability.Available)
        {
            return Task.FromResult(RecognitionResult.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(RecognizeCore(image, cancellationToken));
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_gate)
        {
            DisposeEngine();
            _disposed = true;
            ReadyLanguages = [];
            Availability = RecognizerAvailability.Unavailable;
            UnavailabilityReason = "Движок Tesseract освобождён.";
        }
    }

    private RecognitionResult RecognizeCore(PixelBuffer image, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lines = new List<RecognizedLine>();

        lock (_gate)
        {
            if (_engine is null) return RecognitionResult.Empty;

            using var pix = Pix.LoadFromMemory(EncodeBmp(image));
            using var page = _engine.Process(pix);
            using var iterator = page.GetIterator();

            iterator.Begin();

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var words = new List<RecognizedWord>();

                do
                {
                    var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    // Tesseract отдаёт уверенность в процентах; контракт RecognizedWord — 0..1.
                    var confidence = Math.Clamp(iterator.GetConfidence(PageIteratorLevel.Word) / 100.0, 0.0, 1.0);

                    var bounds = iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box)
                        ? new PixelRect(box.X1, box.Y1, box.Width, box.Height)
                        : PixelRect.Empty;

                    words.Add(new RecognizedWord(text, bounds, confidence, LanguageDetector.DetectWord(text)));
                }
                while (iterator.Next(PageIteratorLevel.TextLine, PageIteratorLevel.Word));

                if (words.Count > 0) lines.Add(new RecognizedLine(words));
            }
            while (iterator.Next(PageIteratorLevel.TextLine));
        }

        stopwatch.Stop();

        return new RecognitionResult(lines, Name, stopwatch.Elapsed);
    }

    private void MarkUnavailable(string reason)
    {
        Availability = RecognizerAvailability.Unavailable;
        UnavailabilityReason = reason;
    }

    private void DisposeEngine()
    {
        _engine?.Dispose();
        _engine = null;
    }

    /// <summary>Строка языков для инициализации: «rus+eng» либо «eng+rus» при <see cref="EnglishFirst"/>.</summary>
    private string LanguageString(IReadOnlyList<string> codes)
    {
        var ordered = EnglishFirst
            ? codes.OrderBy(c => c == EnglishCode ? 0 : 1).ToList()
            : codes.ToList();

        return string.Join('+', ordered);
    }

    private static PageSegMode ToPageSegMode(TesseractLayoutMode mode) => mode switch
    {
        TesseractLayoutMode.SingleBlock => PageSegMode.SingleBlock,
        TesseractLayoutMode.SingleLine => PageSegMode.SingleLine,
        _ => PageSegMode.Auto,
    };

    private static List<string> CodesFor(RecognitionLanguages languages)
    {
        var codes = new List<string>(2);
        if (languages.HasFlag(RecognitionLanguages.Russian)) codes.Add(RussianCode);
        if (languages.HasFlag(RecognitionLanguages.English)) codes.Add(EnglishCode);
        return codes;
    }

    /// <summary>
    /// Упаковать растр в 24-битный BMP в памяти.
    /// </summary>
    /// <remarks>
    /// Leptonica умеет собирать <c>Pix</c> из байтов известного формата, а BMP — самый простой
    /// из них: заголовок фиксированной длины и сырые пиксели. Альтернатива (собирать Pix
    /// поштучно через PixData) требует знания внутреннего порядка слов leptonica и тянет
    /// System.Drawing в BrainstormBuddy.Core.Ocr, чего здесь быть не должно — препроцессинг работает
    /// над PixelBuffer (ТЗ §2.3).
    ///
    /// ⚠️ В BMP строки идут СНИЗУ ВВЕРХ (при положительной высоте), а строка выровнена
    /// на 4 байта. Перепутать — получить перевёрнутый или «сдвинутый по диагонали» растр.
    /// </remarks>
    /// <summary>Упаковать растр в BMP в памяти. Серый вход пишется в 8-битную палитровую
    /// форму — строки копируются без развёртки каналов, и Leptonica получает родные 8 bpp,
    /// что быстрее и компактнее 24-битного тройного дублирования.</summary>
    private static byte[] EncodeBmp(PixelBuffer image)
        => image.Format == PixelFormatKind.Gray8 ? EncodeBmp8(image) : EncodeBmp24(image);

    /// <summary>
    /// Упаковать серый растр в 8-битный палитровый BMP (256 оттенков серого).
    /// </summary>
    /// <remarks>
    /// Тот же формат, что и у <see cref="EncodeBmp24"/>, но с палитрой: BITMAPINFOHEADER
    /// вместе с ней занимает 14+40+256×4 = 1078 байт, за ними — пиксели по одному байту,
    /// строки снизу вверх и по-прежнему выровнены на 4 байта.
    /// </remarks>
    private static byte[] EncodeBmp8(PixelBuffer image)
    {
        const int paletteSize = 256 * 4;
        const int headerSize = 54 + paletteSize;

        var width = image.Width;
        var height = image.Height;
        var rowSize = (width + 3) & ~3;
        var imageSize = rowSize * height;
        var bytes = new byte[headerSize + imageSize];

        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(38), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(42), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(46), 256); // biClrUsed

        // Палитра серого: запись i = RGBA(i,i,i,0) — в BMP четвёртый байт зарезервирован.
        for (var i = 0; i < 256; i++)
        {
            var offset = 54 + i * 4;
            bytes[offset] = (byte)i;
            bytes[offset + 1] = (byte)i;
            bytes[offset + 2] = (byte)i;
        }

        var source = image.Pixels;
        var stride = image.Stride;

        for (var y = 0; y < height; y++)
        {
            var sourceRow = source.Slice(y * stride, width);
            var destinationRow = bytes.AsSpan(headerSize + (height - 1 - y) * rowSize, width);
            sourceRow.CopyTo(destinationRow);
        }

        return bytes;
    }

    /// <summary>
    /// Упаковать растр в 24-битный BMP в памяти.
    /// </summary>
    /// <remarks>
    /// Запасной путь для цветных буферов (Bgra32): серые уходят в <see cref="EncodeBmp8"/>.
    /// В BMP строки идут СНИЗУ ВВЕРХ (при положительной высоте), а строка выровнена
    /// на 4 байта. Перепутать — получить перевёрнутый или «сдвинутый по диагонали» растр.
    /// </remarks>
    private static byte[] EncodeBmp24(PixelBuffer image)
    {
        const int headerSize = 54;

        var width = image.Width;
        var height = image.Height;
        var rowSize = (width * 3 + 3) & ~3;
        var imageSize = rowSize * height;
        var bytes = new byte[headerSize + imageSize];

        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(38), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(42), 2835);

        var source = image.Pixels;
        var bytesPerPixel = image.BytesPerPixel;
        var gray = image.Format == PixelFormatKind.Gray8;
        var stride = image.Stride;

        for (var y = 0; y < height; y++)
        {
            var destinationRow = headerSize + (height - 1 - y) * rowSize;
            var sourceRow = y * stride;

            for (var x = 0; x < width; x++)
            {
                var s = sourceRow + x * bytesPerPixel;
                var d = destinationRow + x * 3;

                if (gray)
                {
                    var v = source[s];
                    bytes[d] = v;
                    bytes[d + 1] = v;
                    bytes[d + 2] = v;
                }
                else
                {
                    bytes[d] = source[s];
                    bytes[d + 1] = source[s + 1];
                    bytes[d + 2] = source[s + 2];
                }
            }
        }

        return bytes;
    }
}
