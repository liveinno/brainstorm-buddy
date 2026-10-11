namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// Чистая логика маппинга diar-сегментов на запрошенный диапазон: суммарное
/// пересечение по спикеру → доминирующий. Вынесена из NemotronDiarizer, чтобы
/// покрываться юнит-тестами без нативной dll. Детерминирована: при равном
/// overlap побеждает меньший номер спикера (фиксированный порядок обхода).
/// </summary>
internal static class SpeakerSegmentMapper
{
    /// <summary>
    /// Доминирующий спикер [onsetSec, endSec) по суммарному пересечению сегментов.
    /// Возвращает (speaker, coverage): coverage = суммарный overlap лучшего / длина range,
    /// в [0..1]. null — нет пересечений, пустой список или вырожденный range.
    /// </summary>
    internal static (int Speaker, double Coverage)? BestOverlapSpeaker(
        NemoDiarSegment[] segments, double onsetSec, double endSec)
    {
        var span = endSec - onsetSec;
        if (span <= 0 || segments.Length == 0)
            return null;

        var overlap = new Dictionary<int, double>();
        foreach (var s in segments)
        {
            var o = Math.Min(s.EndTime, endSec) - Math.Max(s.StartTime, onsetSec);
            if (o <= 0)
                continue;
            overlap[s.Speaker] = (overlap.TryGetValue(s.Speaker, out var cur) ? cur : 0) + o;
        }
        if (overlap.Count == 0)
            return null;

        var best = -1;
        var bestOverlap = 0.0;
        // OrderBy: при равном overlap детерминированно берём меньший speaker-id.
        foreach (var (speaker, o) in overlap.OrderBy(kv => kv.Key))
        {
            if (o > bestOverlap)
            {
                bestOverlap = o;
                best = speaker;
            }
        }
        return (best, Math.Min(1.0, bestOverlap / span));
    }

    /// <summary>
    /// Хронологическая раскладка спикеров ВНУТРИ диапазона [onsetSec, endSec):
    /// режем диапазон по границам пересекающихся сегментов, каждому подынтервалу
    /// — спикер по критериям: больше overlap → при равенстве сегмент, начавшийся
    /// ПОЗЖЕ (спикер, вошедший в разговор последним — случай «женская реплика
    /// внутри монолога S1»); далее — меньший id (детерминизм, как у
    /// BestOverlapSpeaker). Дыры (паузы без сегментов) отдаются предыдущему
    /// подынтервалу, ведущая дыра — первому озвученному: спаны покрывают весь
    /// диапазон без зазоров, соседние подынтервалы одного спикера склеены.
    /// null — нет пересечений, пустой список или вырожденный range.
    /// </summary>
    internal static (int Speaker, double StartSec, double EndSec)[]? SpeakerSpans(
        NemoDiarSegment[] segments, double onsetSec, double endSec)
    {
        if (endSec <= onsetSec || segments.Length == 0)
            return null;

        // Точки разреза — границы пересекающихся сегментов, клипнутые в диапазон.
        var points = new List<double>(segments.Length * 2 + 2) { onsetSec, endSec };
        foreach (var s in segments)
        {
            if (Math.Min(s.EndTime, endSec) - Math.Max(s.StartTime, onsetSec) <= 0)
                continue;
            if (s.StartTime > onsetSec && s.StartTime < endSec) points.Add(s.StartTime);
            if (s.EndTime > onsetSec && s.EndTime < endSec) points.Add(s.EndTime);
        }
        points.Sort();
        if (points.Count <= 2)
            return null; // нет пересекающихся сегментов — дыр и спанов нет

        const double eps = 1e-9;
        var spans = new List<(int Speaker, double StartSec, double EndSec)>();
        var anyVoiced = false;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (b - a <= eps)
                continue; // дубль границы — не рождаем нулевых спанов

            var best = -1;
            var bestO = 0.0;
            var bestStart = 0.0;
            foreach (var s in segments)
            {
                var o = Math.Min(s.EndTime, b) - Math.Max(s.StartTime, a);
                // Порядок критериев: overlap ↓, затем поздний старт (вложенная
                // реплика последнего вошедшего спикера), затем меньший id.
                // best<0 — первый кандидат безусловно (иначе ведущая дыра
                // оставляла бы best=-1 и побеждала бы озвученный интервал).
                var take = best < 0
                    || o > bestO + eps
                    || (o > bestO - eps && s.StartTime > bestStart + eps)
                    || (o > bestO - eps && s.StartTime > bestStart - eps && s.Speaker < best);
                if (take)
                {
                    bestO = o;
                    bestStart = s.StartTime;
                    best = s.Speaker;
                }
            }
            if (bestO <= 0)
            {
                // Дыра: отдаём предыдущему спану (пауза внутри его реплики).
                if (spans.Count > 0)
                    spans[^1] = spans[^1] with { EndSec = b };
                continue;
            }
            anyVoiced = true;
            if (spans.Count > 0 && spans[^1].Speaker == best)
                spans[^1] = spans[^1] with { EndSec = b }; // склейка подряд
            else
                spans.Add((best, a, b));
        }
        if (!anyVoiced)
            return null;
        // Ведущая дыра (пауза до первого озвученного подынтервала) → первому
        // спану: покрытие диапазона остаётся сплошным.
        if (spans.Count > 0 && spans[0].StartSec > onsetSec)
            spans[0] = spans[0] with { StartSec = onsetSec };
        return spans.ToArray();
    }
}
