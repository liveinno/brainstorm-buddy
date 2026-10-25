using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BrainstormBuddy.Core.Backup;

/// <summary>
/// Типизированная ошибка формата/расшифровки архива. Мапится в
/// <see cref="ArchiveFailureKind"/> на границе публичного API.
/// </summary>
internal sealed class ArchiveFormatException : Exception
{
    public ArchiveFormatException(ArchiveFailureKind kind, string message)
        : base(message) => Kind = kind;

    public ArchiveFormatException(ArchiveFailureKind kind, string message, Exception inner)
        : base(message, inner) => Kind = kind;

    public ArchiveFailureKind Kind { get; }
}

/// <summary>
/// Переносимый контейнер: plaintext (ZIP) шифруется целиком поточными
/// записями AES-256-GCM. Ключ — PBKDF2-SHA256 от пароля пользователя
/// (перенос между Windows-профилями, без привязки к DPAPI).
///
/// Формат файла:
///   header 32B: magic "BSB-ARCHIVE1" (12) | iterations u32 BE | salt 16B
///   records:   len u32 BE | nonce 12B | tag 16B | ciphertext[len]
///
/// Каждая запись шифрует ≤ <see cref="ChunkPlaintextSize"/> байт открытого
/// текста. AAD записи = magic | index u64 BE | len u32 BE | isLast u8 —
/// связывает порядок, длину и признак финальной записи: перестановка,
/// обрезка и дописывание записей дают ошибку аутентификации.
/// Неверный пароль неотличим от повреждения первой записи; сбой GCM на
/// записи 0 сообщается как WrongPassword, на последующих — как Corrupt
/// (ключ уже доказан первой записью).
/// </summary>
internal static class PortableArchiveCrypto
{
    internal const int DefaultIterations = 210_000;
    internal const int MinIterations = 10_000;
    internal const int MaxIterations = 50_000_000;
    internal const int ChunkPlaintextSize = 1_048_576; // 1 MiB
    private const int HeaderSize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int RecordPrefixSize = 4 + NonceSize + TagSize;

    private static ReadOnlySpan<byte> Magic => "BSB-ARCHIVE1"u8; // 12 байт

    /// <summary>Шифрует <paramref name="source"/> целиком в <paramref name="dest"/>.</summary>
    public static async Task EncryptStreamAsync(
        Stream source, Stream dest, string password, int iterations,
        IProgress<long>? bytesProgress, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        var plain = new byte[ChunkPlaintextSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var header = new byte[HeaderSize];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)iterations);
            salt.CopyTo(header.AsSpan(16));
            await dest.WriteAsync(header, ct).ConfigureAwait(false);

            var cipher = new byte[ChunkPlaintextSize];
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var aad = new byte[Magic.Length + 8 + 4 + 1];
            Magic.CopyTo(aad);
            var prefix = new byte[4];

            long index = 0;
            int n;
            do
            {
                n = await ReadUpToAsync(source, plain, ct).ConfigureAwait(false);
                // Последняя запись — короткая; при длине, кратной чанку (и у пустого
                // входа), финальная запись нулевой длины с флагом isLast=1.
                var isLast = n < ChunkPlaintextSize;
                RandomNumberGenerator.Fill(nonce);
                BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(Magic.Length), (ulong)index);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(Magic.Length + 8), (uint)n);
                aad[^1] = (byte)(isLast ? 1 : 0);

                aes.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), tag, aad);

                BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)n);
                await dest.WriteAsync(prefix, ct).ConfigureAwait(false);
                await dest.WriteAsync(nonce, ct).ConfigureAwait(false);
                await dest.WriteAsync(tag, ct).ConfigureAwait(false);
                if (n > 0)
                    await dest.WriteAsync(cipher.AsMemory(0, n), ct).ConfigureAwait(false);

                index++;
                bytesProgress?.Report(n);
            }
            while (n == ChunkPlaintextSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Расшифровывает <paramref name="source"/> в <paramref name="dest"/>.
    /// Бросает <see cref="ArchiveFormatException"/> с типизированной причиной.
    /// </summary>
    public static async Task DecryptStreamAsync(
        Stream source, Stream dest, string password,
        IProgress<long>? bytesProgress, CancellationToken ct)
    {
        // isLast вычисляется по позиции — нужен seekable источник.
        if (!source.CanSeek)
            throw new ArgumentException("Источник архива должен поддерживать позиционирование.",
                nameof(source));
        var header = new byte[HeaderSize];
        try
        {
            await source.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new ArchiveFormatException(
                ArchiveFailureKind.InvalidFormat, "Файл слишком короткий — это не архив профиля.");
        }

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new ArchiveFormatException(
                ArchiveFailureKind.InvalidFormat, "Сигнатура файла не совпадает — это не архив BrainstormBuddy.");

        var iterations = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (iterations < MinIterations || iterations > MaxIterations)
            throw new ArchiveFormatException(
                ArchiveFailureKind.Corrupt, "Повреждённые параметры ключа в заголовке архива.");

        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, header.AsSpan(16, SaltSize), (int)iterations,
            HashAlgorithmName.SHA256, KeySize);
        var plain = new byte[ChunkPlaintextSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var cipher = new byte[ChunkPlaintextSize];
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var prefix = new byte[4];
            var aad = new byte[Magic.Length + 8 + 4 + 1];
            Magic.CopyTo(aad);

            long index = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Конец файла на границе записи допустим только после записи с isLast=1.
                var got = await ReadUpToAsync(source, prefix, ct).ConfigureAwait(false);
                if (got == 0)
                {
                    if (index == 0)
                        throw new ArchiveFormatException(
                            ArchiveFailureKind.Corrupt, "В архиве нет ни одной записи данных.");
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Corrupt, "Файл оборван: последняя запись не помечена финальной.");
                }
                if (got < prefix.Length)
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Corrupt, "Файл оборван в заголовке записи.");

                var len = BinaryPrimitives.ReadUInt32BigEndian(prefix);
                if (len > ChunkPlaintextSize)
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Corrupt, "Повреждённая длина записи в архиве.");

                try
                {
                    await source.ReadExactlyAsync(nonce, ct).ConfigureAwait(false);
                    await source.ReadExactlyAsync(tag, ct).ConfigureAwait(false);
                    if (len > 0)
                        await source.ReadExactlyAsync(cipher.AsMemory(0, (int)len), ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Corrupt, "Файл оборван внутри записи данных.");
                }

                // Запись финальная, если после неё файл кончается.
                var isLast = source.Position >= source.Length;
                BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(Magic.Length), (ulong)index);
                BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(Magic.Length + 8), len);
                aad[^1] = (byte)(isLast ? 1 : 0);

                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, (int)len), tag, plain.AsSpan(0, (int)len), aad);
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw new ArchiveFormatException(
                        index == 0 ? ArchiveFailureKind.WrongPassword : ArchiveFailureKind.Corrupt,
                        index == 0
                            ? "Пароль не подходит (или повреждено начало архива)."
                            : "Запись архива повреждена или изменена.");
                }

                if (len > 0)
                    await dest.WriteAsync(plain.AsMemory(0, (int)len), ct).ConfigureAwait(false);
                bytesProgress?.Report(len);
                index++;

                if (isLast)
                    break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Читает до заполнения буфера, возвращая число прочитанных байт.</summary>
    private static async Task<int> ReadUpToAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
