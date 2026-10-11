



namespace BrainstormBuddy.Core.Ocr;

/// <summary>
/// Применяет препроцессинг (ТЗ §2.3) перед Tesseract и возвращает координаты слов
/// в систему ИСХОДНОГО растра.
/// </summary>
/// <remarks>
/// Зачем отдельный декоратор, а не код внутри каждого движка:
///
/// 1. <see cref="RecognitionOptions.EnhanceSmallText"/> и
///    <see cref="RecognitionOptions.AutoInvertDarkThemes"/> — настройки продукта (ТЗ §6).
///    Пока их никто не применял, они были объявлены и молча игнорировались: движки получали
///    сырой растр, и «улучшение мелкого текста» в настройках не делало ничего.
/// 2. Апскейл МЕНЯЕТ КООРДИНАТЫ. Слово, найденное на растре, увеличенном втрое, приходит
///    с координатами втрое больше исходных. Если их не привести обратно, подсветка в окне
///    результата укажет мимо, а колонки TSV разъедутся. Приведение выполняется здесь
///    один раз для основного и повторного проходов.
/// 3. Tesseract при этом получает уже подготовленный растр и отвечает только за распознавание.
/// </remarks>
public sealed class PreprocessingRecognizer : ITextRecognizer, IDisposable
{
    /// <summary>
    /// Средняя уверенность ниже этой — повод повторить препроцессинг с апскейлом (ТЗ §2.1).
    /// Применяется только к движкам, которые уверенность сообщают.
    /// </summary>
    public const double ConfidenceThreshold = 0.75;

    /// <summary>
    /// Доля осмысленных символов ниже этой — тоже повод повторить. Работает там, где
    /// уверенности нет: без явных чисел доля формы — единственный доступный признак «мусор».
    /// </summary>
    public const double QualityThreshold = 0.80;

    /// <summary>Разница уверенностей, ниже которой результаты считаются равными.</summary>
    public const double ConfidenceTolerance = 0.05;

    private readonly ITextRecognizer _inner;

    public PreprocessingRecognizer(ITextRecognizer inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name => _inner.Name;

    public RecognizerAvailability Availability => _inner.Availability;

    public string? UnavailabilityReason => _inner.UnavailabilityReason;

    /// <summary>
    /// Приёмник трассы повторного прохода — только метрики, ни байта распознанного текста.
    /// </summary>
    public Action<string>? Trace { get; set; }

    public Task InitializeAsync(RecognitionLanguages languages, CancellationToken cancellationToken = default)
        => _inner.InitializeAsync(languages, cancellationToken);

    public async Task<RecognitionResult> RecognizeAsync(
        PixelBuffer image,
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var scale = options.EnhanceSmallText ? ImagePreprocessor.RecommendedUpscale(image) : 1;
        var prepared = Prepare(image, options, scale);

        var result = await _inner.RecognizeAsync(prepared, options, cancellationToken).ConfigureAwait(false);
        if (scale != 1) result = Downscale(result, scale);

        // Лестница «сложных случаев» (ТЗ §2.1 называет Tesseract движком для сложных случаев,
        // здесь тот же принцип применён к масштабу). Обычный текст 14–16 px распознаётся без
        // апскейла с CER 0,23 % за ~160 мс на области 800×600 — апскейлить его всегда значит
        // жечь бюджет §2.5 впустую (замер: 1469 мс при бюджете 700). Но смешанные строки RU+EN
        // без апскейла мажут (15,6 % против 0,0 с ×2). Поэтому апскейл делается ВТОРЫМ проходом
        // и только когда первый результат слаб: быстрый путь остаётся быстрым, трудный —
        // точным.
        if (options.EnhanceSmallText && scale == 1 && IsWeak(result))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var upscaled = Prepare(image, options, 2);
            var retry = await _inner.RecognizeAsync(upscaled, options, cancellationToken).ConfigureAwait(false);
            retry = Downscale(retry, 2);

            Trace?.Invoke(
                $"Препроцессинг: повторный проход — первый слабый " +
                $"(уверенность {result.AverageConfidence:0.00}, " +
                $"качество {LineMerger.TextQuality(result.RawText):0.00}), " +
                $"второй — уверенность {retry.AverageConfidence:0.00}, " +
                $"качество {LineMerger.TextQuality(retry.RawText):0.00}, " +
                $"победил {(ChooseRetry(result, retry) ? "повторный" : "первый")}.");

            if (ChooseRetry(result, retry)) result = retry;
        }

        return result;
    }

    /// <summary>
    /// Конвейер ТЗ §2.3 в порядке: серый → апскейл → автоинверсия.
    /// </summary>
    /// <remarks>
    /// Бинаризация Оцу здесь НЕ применяется, хотя ТЗ упоминает её как часть «улучшения мелкого
    /// текста». Замер показал, почему: Tesseract работает по градациям серого заметно лучше,
    /// чем по жёстко бинаризованному растру — бинаризация съедает сглаженные края глифов.
    /// Оцу остаётся в <see cref="ImagePreprocessor.BinarizeOtsu"/> как отдельный инструмент
    /// и покрыт тестами, но в продуктовый путь не входит. Решение зафиксировано в README.
    /// </remarks>
    private static PixelBuffer Prepare(PixelBuffer image, RecognitionOptions options, int scale)
    {
        var result = ImagePreprocessor.ToGrayscale(image);

        if (scale > 1) result = ImagePreprocessor.Upscale(result, scale);
        if (options.AutoInvertDarkThemes && ImagePreprocessor.NeedsInversion(result)) result = ImagePreprocessor.Invert(result);

        return result;
    }

    /// <summary>Вернуть координаты слов из увеличенного растра в исходный.</summary>
    private static RecognitionResult Downscale(RecognitionResult result, int scale)
    {
        var lines = new List<RecognizedLine>(result.Lines.Count);

        foreach (var line in result.Lines)
        {
            var words = new List<RecognizedWord>(line.Words.Count);

            foreach (var word in line.Words)
            {
                var b = word.Bounds;
                words.Add(word with
                {
                    Bounds = new PixelRect(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale),
                });
            }

            lines.Add(new RecognizedLine(words));
        }

        return new RecognitionResult(lines, result.EngineName, result.Elapsed);
    }

    /// <summary>
    /// Повод повторить препроцессинг с апскейлом: пустой текст, известная низкая уверенность
    /// либо низкая доля осмысленных символов.
    /// </summary>
    private static bool IsWeak(RecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsEmpty) return true;

        var confidence = result.AverageConfidence;

        if (confidence >= 0) return confidence < ConfidenceThreshold;

        return LineMerger.TextQuality(result.RawText) < QualityThreshold;
    }

    /// <summary>
    /// Стоит ли заменять первый результат повторным: <c>true</c> — повторный выиграл.
    /// Сначала сравнивается уверенность (когда известна обоим результатам), потом доля
    /// осмысленных символов, при равенстве — более полный текст.
    /// </summary>
    private static bool ChooseRetry(RecognitionResult first, RecognitionResult retry)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(retry);

        if (retry.IsEmpty) return false;
        if (first.IsEmpty) return true;

        var firstConfidence = first.AverageConfidence;
        var retryConfidence = retry.AverageConfidence;

        if (firstConfidence >= 0 && retryConfidence >= 0)
        {
            var difference = retryConfidence - firstConfidence;
            if (difference > ConfidenceTolerance) return true;
            if (-difference > ConfidenceTolerance) return false;
        }

        // Измеренный сигнал бьёт неизмеримый: когда один результат уверенность сообщает
        // и она высока, а другой не сообщает, берём того, у кого есть основание.
        if (retryConfidence >= ConfidenceThreshold && firstConfidence < 0) return true;
        if (firstConfidence >= ConfidenceThreshold && retryConfidence < 0) return false;

        var firstQuality = LineMerger.TextQuality(first.RawText);
        var retryQuality = LineMerger.TextQuality(retry.RawText);

        if (retryQuality - firstQuality > LineMerger.QualityTolerance) return true;
        if (firstQuality - retryQuality > LineMerger.QualityTolerance) return false;

        return LineMerger.MeaningfulCharCount(retry.RawText) >
               LineMerger.MeaningfulCharCount(first.RawText);
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}