namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Препроцессинг перед распознаванием (ТЗ §2.3). Все шаги — чистые функции над
/// <see cref="PixelBuffer"/>: вход не меняется, на выходе всегда новый буфер.
/// </summary>
/// <remarks>
/// Порядок в конвейере важен: сначала перевод в градации серого, потом оценка кегля
/// и апскейл, потом инверсия и только затем бинаризация. Апскейл ПОСЛЕ бинаризации
/// растил бы уже потерянные детали, а инверсия до апскейла ничего не меняет, но заставляет
/// интерполировать втрое больше данных.
/// </remarks>
public static class ImagePreprocessor
{
    /// <summary>
    /// Высота строки, ниже которой текст считается мелким и требует апскейла.
    /// </summary>
    /// <remarks>
    /// ⚠️ Отступление от буквы ТЗ §2.3 (там порог «&lt; 20 px») по замеру, разрешённое ТЗ §10.
    /// Два требования ТЗ конфликтуют на обычном тексте 14–16 px: под порогом 20 он апскейлится
    /// ×2, растр 800×600 раздувается до 1600×1200, и распознавание занимает 1469 мс при бюджете
    /// §2.5 в 700 мс. При этом гейт CER держит 0,00 % на 14–16 px БЕЗ апскейла — там он
    /// страховка, а не необходимость. Порог 13 оставляет апскейл ровно классу «мелкий текст
    /// 10–11 px» из §9 (его порог качества всё ещё выдерживается) и возвращает бюджет: медиана
    /// после правки — см. PerformanceBudgetTests. Оба гейта — качества и времени — стоят на
    /// живых прогонах, так что сдвиг порога в любую сторону теперь ловится автоматически.
    /// </remarks>
    public const int SmallTextLineHeightThreshold = 13;

    /// <summary>
    /// Медианная яркость, ниже которой фон считается тёмным и включается автоинверсия.
    /// 96 из 255 — заметно ниже середины: тёмные темы IDE и терминалы дают 20–60,
    /// а серый фон обычного окна около 240, так что зазор большой и порог не дребезжит.
    /// </summary>
    public const byte DarkBackgroundMedianThreshold = 96;

    /// <summary>
    /// Порог насыщенности (размах каналов max-min), с которого пиксель считается настоящим
    /// цветным текстом и переводится в серое по максимуму каналов, а не по яркости.
    /// 100 отсекает слабо-цветную бахрому ClearType (размах обычно 20–60) и уверенно ловит
    /// насыщенный текст интерфейсов (красный/синий/зелёный, размах 150–255).
    /// </summary>
    public const byte SaturatedTextSpreadThreshold = 100;

    /// <summary>Перевод в градации серого. Уже серый буфер возвращается как есть.</summary>
    public static PixelBuffer ToGrayscale(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Format == PixelFormatKind.Gray8) return source;

        // На ТЁМНОМ фоне яркость (luma) предаёт насыщенный цветной текст: синий 0,0,255
        // даёт luma 29, почти равную тёмному фону, и текст растворяется ещё ДО распознавания
        // (обратная связь пользователя: цветные резисты и урон в игре читались через раз).
        // max(R,G,B) поднимает такой цвет к яркому.
        //
        // ⚠️ Но применять max ко ВСЕМУ тёмному кадру нельзя: ClearType рисует по краям букв
        // слабо-цветную субпиксельную бахрому, и max усиливал её — обычный текст на тёмном
        // фоне деградировал (смешанная строка RU+EN: 1,79 % → 4,06 %, гейт §9 упал).
        // Поэтому max берём ТОЛЬКО для пикселей с высокой насыщенностью (размах каналов
        // выше порога) — это настоящий цветной текст. Бахрома ClearType с её малым размахом
        // и все серые пиксели остаются на luma, то есть монохром и обычный текст не тронуты.
        var darkBackground = MedianLuminance(source) < DarkBackgroundMedianThreshold;

        var result = new byte[source.Width * source.Height];

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var useMax = darkBackground && source.GetChannelSpread(x, y) >= SaturatedTextSpreadThreshold;

                result[y * source.Width + x] = useMax
                    ? source.GetMaxChannel(x, y)
                    : source.GetLuminance(x, y);
            }
        }

        return new PixelBuffer(source.Width, source.Height, PixelFormatKind.Gray8, result);
    }

    /// <summary>Медианная яркость по всему растру. По ней принимается решение об инверсии.</summary>
    public static byte MedianLuminance(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Гистограмма вместо сортировки: 256 корзин против сортировки миллиона значений.
        var histogram = new int[256];

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                histogram[source.GetLuminance(x, y)]++;
            }
        }

        var total = source.Width * source.Height;
        var half = total / 2;
        var seen = 0;

        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen > half) return (byte)value;
        }

        return 255;
    }

    /// <summary>Тёмный ли фон — то есть нужна ли инверсия (ТЗ §2.3).</summary>
    public static bool NeedsInversion(PixelBuffer source)
        => MedianLuminance(source) < DarkBackgroundMedianThreshold;

    /// <summary>Инверсия яркости. Светлый текст на тёмном превращается в тёмный на светлом.</summary>
    public static PixelBuffer Invert(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var gray = ToGrayscale(source);
        var pixels = gray.Pixels;
        var result = new byte[pixels.Length];

        for (var i = 0; i < pixels.Length; i++) result[i] = (byte)(255 - pixels[i]);

        return new PixelBuffer(gray.Width, gray.Height, PixelFormatKind.Gray8, result);
    }

    /// <summary>
    /// Оценка высоты строки текста в пикселях. По ней решается, нужен ли апскейл (ТЗ §2.3).
    /// </summary>
    /// <remarks>
    /// Метод — по горизонтальной проекции: считаем в каждой строке пикселей долю «чернил»
    /// (пикселей темнее середины между самым тёмным и самым светлым). Строки текста дают
    /// полосы с чернилами, межстрочные промежутки — почти пустые. Медиана высот полос
    /// и есть оценка кегля.
    ///
    /// Медиана, а не среднее: одна слипшаяся пара строк или рамка таблицы дают выброс,
    /// который среднее утащит вверх, и апскейл не включится там, где он нужен.
    /// Чернил нет вовсе — возвращается 0: вызывающий поймёт, что текста не найдено,
    /// и не станет делить на ноль.
    /// </remarks>
    public static int EstimateLineHeight(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var gray = ToGrayscale(source);

        // Однотонный растр (сплошная заливка любого цвета) строчной структуры не имеет.
        // Без этой проверки середина между min и max совпадает с самим значением, «чернилами»
        // оказывается КАЖДЫЙ пиксель, и высотой строки объявляется высота всего растра —
        // апскейл выключился бы ровно там, где картинка пустая.
        var (min, max) = LuminanceRange(gray);
        if (min == max) return 0;

        var threshold = (byte)((min + max) / 2);

        var runs = new List<int>();
        var currentRun = 0;

        for (var y = 0; y < gray.Height; y++)
        {
            var hasInk = false;

            for (var x = 0; x < gray.Width; x++)
            {
                if (gray.GetLuminance(x, y) > threshold) continue;

                hasInk = true;
                break;
            }

            if (hasInk)
            {
                currentRun++;
            }
            else if (currentRun > 0)
            {
                runs.Add(currentRun);
                currentRun = 0;
            }
        }

        if (currentRun > 0) runs.Add(currentRun);
        if (runs.Count == 0) return 0;

        runs.Sort();
        return runs[runs.Count / 2];
    }

    /// <summary>
    /// Во сколько раз увеличивать растр. Текст крупный — 1 (апскейл не нужен),
    /// иначе ×2 или ×3 так, чтобы строка дотянула до <see cref="SmallTextLineHeightThreshold"/>.
    /// </summary>
    public static int RecommendedUpscale(PixelBuffer source)
    {
        var lineHeight = EstimateLineHeight(source);

        // Текста не нашли — увеличивать нечего, и вслепую растить картинку вчетверо
        // по памяти и времени незачем.
        if (lineHeight <= 0) return 1;
        if (lineHeight >= SmallTextLineHeightThreshold) return 1;

        return lineHeight * 2 >= SmallTextLineHeightThreshold ? 2 : 3;
    }

    /// <summary>
    /// Увеличение целым коэффициентом фильтром Ланцоша (a = 3) — ТЗ §2.3 называет апскейл
    /// главным рычагом качества на экранном тексте.
    /// </summary>
    /// <remarks>
    /// Ланцош, а не билинейная интерполяция: билинейная размывает края глифов, и мелкий
    /// текст после неё распознаётся хуже, чем без апскейла вообще. Ланцош сохраняет
    /// контраст на границе штриха ценой лёгкого «звона», который OCR не мешает.
    /// </remarks>
    public static PixelBuffer Upscale(PixelBuffer source, int factor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);

        if (factor == 1) return ToGrayscale(source);

        var gray = ToGrayscale(source);
        var width = gray.Width * factor;
        var height = gray.Height * factor;
        var result = new byte[width * height];

        // Parallel.For по строкам: каждый выходной пиксель зависит только от входного
        // буфера (чистая функция), поэтому параллелизм не меняет результат и держит
        // контракт детерминизма препроцессинга. Без этого апскейл 1920×1080×2
        // (8,3 МП × 49-точечный Ланцош) ел ~7 с на тик наблюдателя.
        Parallel.For(0, height, y =>
        {
            var sourceY = (y + 0.5) / factor - 0.5;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sourceX = (x + 0.5) / factor - 0.5;
                result[row + x] = SampleLanczos(gray, sourceX, sourceY);
            }
        });

        return new PixelBuffer(width, height, PixelFormatKind.Gray8, result);
    }

    /// <summary>
    /// Бинаризация методом Оцу: порог выбирается так, чтобы максимально разделить
    /// два класса яркости. Опция «улучшение мелкого текста» (ТЗ §2.3, §6).
    /// </summary>
    public static PixelBuffer BinarizeOtsu(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var gray = ToGrayscale(source);
        var threshold = OtsuThreshold(gray);
        var pixels = gray.Pixels;
        var result = new byte[pixels.Length];

        for (var i = 0; i < pixels.Length; i++) result[i] = pixels[i] <= threshold ? (byte)0 : (byte)255;

        return new PixelBuffer(gray.Width, gray.Height, PixelFormatKind.Gray8, result);
    }

    /// <summary>Порог Оцу для уже серого буфера — вынесен для тестов.</summary>
    public static byte OtsuThreshold(PixelBuffer gray)
    {
        ArgumentNullException.ThrowIfNull(gray);

        var histogram = new int[256];
        for (var y = 0; y < gray.Height; y++)
        {
            for (var x = 0; x < gray.Width; x++) histogram[gray.GetLuminance(x, y)]++;
        }

        var total = gray.Width * gray.Height;
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)histogram[i];

        double sumBackground = 0;
        var weightBackground = 0;
        var bestVariance = -1.0;
        byte bestThreshold = 0;

        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0) continue;

            var weightForeground = total - weightBackground;
            if (weightForeground == 0) break;

            sumBackground += t * (double)histogram[t];

            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sum - sumBackground) / weightForeground;
            var between = (double)weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);

            if (between <= bestVariance) continue;

            bestVariance = between;
            bestThreshold = (byte)t;
        }

        return bestThreshold;
    }

    /// <summary>
    /// Добавить однотонные поля вокруг растра. Цвет полей берётся из угла — то есть с фона,
    /// а не выдумывается: на тёмной теме белая рамка сама стала бы «текстом» для движка.
    /// </summary>
    /// <remarks>
    /// Поля сохраняют контекст глифов, когда пользователь выделил область вплотную к тексту.
    /// Цвет берётся из самого кадра, поэтому операция одинаково безопасна для светлой и
    /// тёмной темы и не создаёт искусственную рамку.
    /// </remarks>
    public static PixelBuffer Pad(PixelBuffer source, int padding)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        if (padding == 0) return ToGrayscale(source);

        var gray = ToGrayscale(source);
        var background = gray.GetLuminance(0, 0);

        var width = gray.Width + padding * 2;
        var height = gray.Height + padding * 2;
        var pixels = new byte[width * height];
        Array.Fill(pixels, background);

        var source_ = gray.Pixels;
        for (var y = 0; y < gray.Height; y++)
        {
            source_.Slice(y * gray.Width, gray.Width)
                .CopyTo(pixels.AsSpan((y + padding) * width + padding, gray.Width));
        }

        return new PixelBuffer(width, height, PixelFormatKind.Gray8, pixels);
    }

    /// <summary>Полный конвейер по параметрам распознавания (ТЗ §2.3).</summary>
    public static PixelBuffer Prepare(PixelBuffer source, bool enhanceSmallText, bool autoInvert)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = ToGrayscale(source);

        if (enhanceSmallText) result = Upscale(result, RecommendedUpscale(result));
        if (autoInvert && NeedsInversion(result)) result = Invert(result);
        if (enhanceSmallText) result = BinarizeOtsu(result);

        return result;
    }

    /// <summary>
    /// Самый тёмный и самый светлый пиксель. Граница «чернил» берётся как середина между
    /// ними: фиксированный порог не годится — растр приходит и со светлой темы, и с тёмной.
    /// </summary>
    private static (byte Min, byte Max) LuminanceRange(PixelBuffer gray)
    {
        byte min = 255;
        byte max = 0;

        for (var y = 0; y < gray.Height; y++)
        {
            for (var x = 0; x < gray.Width; x++)
            {
                var value = gray.GetLuminance(x, y);
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        return (min, max);
    }

    /// <summary>
    /// Уменьшить растр целочисленным бокс-даунскейлом (усреднение блока divisor×divisor).
    /// Работает с любым форматом (Gray8/Bgra32), каналы усредняются независимо.
    /// </summary>
    /// <remarks>
    /// Зачем: полный экран 4K (3840×2160) распознаётся 5–7 с — OCR квадратично дороже
    /// по площади. Даунскейл /2 сокращает проход до ~1–2 с при сохранной читаемости
    /// текста (буквы 10+ px остаются ≥5 px, что Tesseract ещё берёт после наших гейтов).
    /// Бокс-усреднение против nearest-neighbour: при /2+ NN выбрасывает 3/4 пикселей и
    /// ломает тонкие штрихи букв, усреднение сохраняет их энергию.
    /// ⚠️ Координаты <see cref="RecognizedWord.Bounds"/> в результате относятся к
    /// УМЕНЬШЕННОМУ растру — экранная подсветка их не использует.
    /// </remarks>
    /// <param name="divisor">Целый делитель (2 = вдвое меньше). ≤1 — возвращается исходник.</param>
    public static PixelBuffer Downscale(PixelBuffer source, int divisor)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (divisor <= 1) return source;

        var width = source.Width / divisor;
        var height = source.Height / divisor;
        if (width < 1 || height < 1) return source;

        var bpp = source.BytesPerPixel;
        var stride = source.Stride;
        var src = source.Pixels;
        var pixels = new byte[width * height * bpp];

        for (var y = 0; y < height; y++)
        {
            var srcRow = y * divisor * stride;
            var dstRow = y * width * bpp;

            for (var x = 0; x < width; x++)
            {
                var sx = srcRow + x * divisor * bpp;
                var dx = dstRow + x * bpp;

                for (var c = 0; c < bpp; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < divisor; dy++)
                    {
                        var row = sx + dy * stride + c;
                        for (var ddx = 0; ddx < divisor; ddx++)
                        {
                            sum += src[row + ddx * bpp];
                        }
                    }
                    pixels[dx + c] = (byte)(sum / (divisor * divisor));
                }
            }
        }

        return new PixelBuffer(width, height, source.Format, pixels);
    }

    /// <summary>
    /// Ужать растр до ширины не больше <paramref name="maxWidth"/> целым делителем.
    /// Вписывающийся кадр возвращается как есть; ≤0 или шире исходника порог — тоже.
    /// </summary>
    public static PixelBuffer LimitWidth(PixelBuffer source, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (maxWidth <= 0 || source.Width <= maxWidth) return source;

        // Целый делитель: 3840 при лимите 2560 → /2 (1920), дробный масштаб не нужен —
        // box-даунскейл по целому делителю дешевле билинейки и не порождает муар.
        var divisor = (int)Math.Ceiling(source.Width / (double)maxWidth);
        return Downscale(source, divisor);
    }

    private const int LanczosRadius = 3;

    private static byte SampleLanczos(PixelBuffer gray, double sourceX, double sourceY)
    {
        var centerX = (int)Math.Floor(sourceX);
        var centerY = (int)Math.Floor(sourceY);

        double accumulated = 0;
        double weightSum = 0;

        for (var dy = -LanczosRadius + 1; dy <= LanczosRadius; dy++)
        {
            var y = Math.Clamp(centerY + dy, 0, gray.Height - 1);
            var weightY = Lanczos(sourceY - (centerY + dy));

            for (var dx = -LanczosRadius + 1; dx <= LanczosRadius; dx++)
            {
                var x = Math.Clamp(centerX + dx, 0, gray.Width - 1);
                var weight = weightY * Lanczos(sourceX - (centerX + dx));

                accumulated += gray.GetLuminance(x, y) * weight;
                weightSum += weight;
            }
        }

        if (weightSum == 0) return 0;

        return (byte)Math.Clamp(Math.Round(accumulated / weightSum), 0, 255);
    }

    private static double Lanczos(double x)
    {
        if (x == 0) return 1;
        if (Math.Abs(x) >= LanczosRadius) return 0;

        var piX = Math.PI * x;
        return LanczosRadius * Math.Sin(piX) * Math.Sin(piX / LanczosRadius) / (piX * piX);
    }
}
