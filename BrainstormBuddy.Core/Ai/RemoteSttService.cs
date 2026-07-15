namespace BrainstormBuddy.Ai;

/// <summary>
/// Внешний STT: делегирует OpenAI-совместимому клиенту (текущий Docker LocalSttBridge /
/// удалённый сервер по SttBaseUrl). Поведение 1:1 с прежним прямым вызовом TranscribeAsync.
/// </summary>
public sealed class RemoteSttService : ISttEngine
{
    private readonly IApiClient _client;

    public RemoteSttService(IApiClient client) => _client = client;

    public string Name => "remote";

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
        => _client.TranscribeAsync(wavAudio, ct);

    // Проверка доступности сервера семейства (LLM/STT). STT-специфичный health добавим
    // на этапе нативного движка; для внешнего пути этого достаточно.
    public Task<bool> HealthAsync(CancellationToken ct = default)
        => _client.CheckConnectionAsync(ct);
}
