namespace BrainstormBuddy.Ai;

/// <summary>
/// Внешний STT: делегирует OpenAI-совместимому клиенту (текущий Docker LocalSttBridge /
/// удалённый сервер по SttBaseUrl). Поведение 1:1 с прежним прямым вызовом TranscribeAsync.
/// </summary>
public sealed class RemoteSttService : ISttEngine, IDetailedSttEngine
{
    private readonly IApiClient _client;

    public RemoteSttService(IApiClient client) => _client = client;

    public string Name => "remote";

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
        => _client.TranscribeAsync(wavAudio, ct);

    /// <summary>
    /// Детальный путь: клиент умеет различать исходы → доверяем его TranscribeDetailedAsync;
    /// клиент реализует только старый TranscribeAsync (null = пусто ИЛИ ошибка, не различить)
    /// → пустой/ошибочный ответ помечаем NoSpeech, чтобы UI/замер не считали каждый null сбоем.
    /// </summary>
    public async Task<SttTranscribeResult> TranscribeDetailedAsync(byte[] wavAudio, CancellationToken ct = default)
    {
        if (_client is IDetailedSttEngine detailed)
            return await detailed.TranscribeDetailedAsync(wavAudio, ct);

        var text = await _client.TranscribeAsync(wavAudio, ct);
        return text is { Length: > 0 }
            ? SttTranscribeResult.Recognized(text)
            : SttTranscribeResult.NoSpeechResult("движок не различает пустой ответ и ошибку");
    }

    // Проверка доступности сервера семейства (LLM/STT). STT-специфичный health добавим
    // на этапе нативного движка; для внешнего пути этого достаточно.
    public Task<bool> HealthAsync(CancellationToken ct = default)
        => _client.CheckConnectionAsync(ct);
}
