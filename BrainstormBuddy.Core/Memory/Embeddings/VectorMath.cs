using System.Buffers.Binary;

namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>
/// Векторная математика для memory-эмбеддингов: косинусное сходство и
/// little-endian сериализация float[] ↔ byte[] (формат столбца
/// mem_embedding.vec в SQLite — переиспользуется SqliteMemoryStore).
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Косинусное сходство векторов. Разная размерность или нулевая норма → 0.
    /// Для уже L2-нормализованных векторов (E5, OpenAI) это просто dot product.
    /// </summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0.0;

        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na <= 0 || nb <= 0) return 0.0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>L2-норма вектора (0 для пустого/нулевого).</summary>
    public static double Norm(float[] v)
    {
        double acc = 0;
        for (int i = 0; i < v.Length; i++) acc += (double)v[i] * v[i];
        return Math.Sqrt(acc);
    }

    /// <summary>L2-нормализация in-place; нулевой вектор остаётся нулевым.</summary>
    public static void NormalizeInPlace(float[] v)
    {
        var n = Norm(v);
        if (n <= 0) return;
        var inv = (float)(1.0 / n);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    /// <summary>L2-нормализованная копия вектора (нулевой → копия нулей).</summary>
    public static float[] Normalize(float[] v)
    {
        var copy = (float[])v.Clone();
        NormalizeInPlace(copy);
        return copy;
    }

    /// <summary>float[] → byte[], little-endian (4 байта на элемент).</summary>
    public static byte[] ToBytes(float[] vec)
    {
        var bytes = new byte[vec.Length * sizeof(float)];
        for (int i = 0; i < vec.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), vec[i]);
        return bytes;
    }

    /// <summary>byte[] → float[], little-endian; хвост &lt;4 байт игнорируется.</summary>
    public static float[] FromBytes(byte[] bytes)
    {
        var vec = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < vec.Length; i++)
            vec[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        return vec;
    }
}
