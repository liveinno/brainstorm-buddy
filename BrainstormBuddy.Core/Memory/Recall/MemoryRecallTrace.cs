namespace BrainstormBuddy.Memory.Recall;

/// <summary>
/// Диагностика одного вызова <see cref="MemoryRecallService.RecallAsync"/>:
/// сколько кандидатов дал FTS и векторная ветка, сколько вернулось, работал ли
/// смысловой поиск. Нужна журналу MEMDIAG: без чисел нельзя отличить «нет
/// данных» от «поиск не состоялся».
/// </summary>
public sealed record MemoryRecallTrace(
    int FtsCandidates,
    int VectorCandidates,
    int Results,
    bool EmbeddingsAvailable,
    bool EmbeddingsFailed);