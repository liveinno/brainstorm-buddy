using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Services;

public enum OverlayStatusKind { Ready, Paused, LlmPaused, Preparing, Transcribing, Thinking, SttUnavailable }

public sealed record OverlayStatusInput(
    bool IsAudioPaused,
    bool IsLlmPaused,
    bool SttOk,
    string? SttError,
    bool SttWarmedUp,
    bool Transcribing,
    int? SttHttpStatus,
    SttHealthReason SttErrKind,
    /// <summary>Метка активного LLM-запроса («[secret]», «[screen]», «S1: …»)
    /// — null, когда ответов никто не ждёт. Thinking показываем после
    /// пауз/ошибок STT: они важнее («LLM на паузе» > «Думаю…»).</summary>
    string? ThinkingLabel = null);

public sealed record OverlayStatus(OverlayStatusKind Kind, string Title, string? Detail, bool IsError);

/// <summary>
/// Resolves the single overlay status line from raw app state.
/// Pure logic, no WPF dependencies; tr(resourceKey, ruFallback) supplies localized strings.
/// </summary>
public static class OverlayStatusResolver
{
    public static OverlayStatus Resolve(OverlayStatusInput s, Func<string, string, string> tr)
    {
        if (s.IsAudioPaused)
        {
            return new OverlayStatus(
                OverlayStatusKind.Paused,
                tr("L_Ovl_RecordingPaused", "Запись приостановлена"),
                tr("L_Ovl_PausedDetail", "Захват аудио остановлен"),
                IsError: false);
        }

        if (s.IsLlmPaused)
        {
            return new OverlayStatus(
                OverlayStatusKind.LlmPaused,
                tr("L_St_LlmPaused", "LLM на паузе — аудио записывается"),
                Detail: null,
                IsError: false);
        }

        if (!s.SttOk)
        {
            return new OverlayStatus(
                OverlayStatusKind.SttUnavailable,
                tr("L_Ovl_SttUnavailable", "Распознавание недоступно"),
                ResolveSttErrorDetail(s.SttError, s.SttHttpStatus, s.SttErrKind, tr),
                IsError: true);
        }

        if (!s.SttWarmedUp)
        {
            return new OverlayStatus(
                OverlayStatusKind.Preparing,
                tr("L_Ovl_Preparing", "Подготовка распознавания…"),
                tr("L_Ovl_PreparingDetail", "Загружаю выбранный движок. Это может занять несколько секунд."),
                IsError: false);
        }

        if (s.Transcribing)
        {
            return new OverlayStatus(
                OverlayStatusKind.Transcribing,
                tr("L_Ovl_Transcribing", "Распознаю речь…"),
                Detail: null,
                IsError: false);
        }

        // «Думаю над <label>…» — есть незавершённый LLM-запрос и никакое
        // более приоритетное состояние не показывается. Снимается само при
        // завершении запроса (pending → 0 → Resolve отдаёт Ready).
        if (s.ThinkingLabel != null)
        {
            return new OverlayStatus(
                OverlayStatusKind.Thinking,
                string.Format(tr("L_Ovl_ThinkingFmt", "Думаю над {0}…"), s.ThinkingLabel),
                Detail: null,
                IsError: false);
        }

        return new OverlayStatus(
            OverlayStatusKind.Ready,
            tr("L_Ovl_Ready", "Готов к распознаванию"),
            tr("L_Ovl_ReadyDetail", "Пока вы говорите, ответы появляются ниже"),
            IsError: false);
    }

    private static string? ResolveSttErrorDetail(string? error, int? httpStatus, SttHealthReason kind, Func<string, string, string> tr)
    {
        if (kind != SttHealthReason.None)
        {
            return kind switch
            {
                SttHealthReason.ServerError => tr("L_Ovl_SttServerError", "Сервер распознавания вернул ошибку."),
                SttHealthReason.Timeout => tr("L_Ovl_SttTimeout", "Сервер распознавания не отвечает."),
                _ => tr("L_Ovl_SttUnreachable", "Сервер распознавания недоступен."),
            };
        }

        if (httpStatus is 401 or 403)
            return tr("L_Ovl_Unavailable401", "Проверьте ключ в настройках");

        if (!string.IsNullOrWhiteSpace(error))
            return error;

        return null;
    }
}
