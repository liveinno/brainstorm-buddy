namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>
/// Провайдер-заглушка при EmbeddingsMode=disabled (дефолт): recall работает
/// только по FTS. Всегда недоступен, EmbedAsync → null.
/// </summary>
public sealed class DisabledEmbeddingProvider : IMemoryEmbeddingProvider
{
    public static readonly DisabledEmbeddingProvider Instance = new();

    public string Name => "disabled";
    public bool IsAvailable => false;

    public Task<float[]?> EmbedAsync(string text, bool isQuery, CancellationToken ct)
        => Task.FromResult<float[]?>(null);
}
