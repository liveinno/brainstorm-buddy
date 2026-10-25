namespace BrainstormBuddy.Ai;

/// <summary>
/// Поток, который сначала отдаёт уже прочитанный префикс, а затем — остаток
/// исходного потока. Нужен, чтобы заглянуть в первые байты ответа (понять,
/// SSE это или JSON) и не потерять их при чтении тела целиком: буферизовать
/// весь ответ нельзя — поток ответа должен идти в UI по мере генерации.
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly int _prefixLength;
    private readonly Stream _rest;
    private int _pos;

    public PrefixedStream(byte[] prefix, int count, Stream rest)
    {
        _prefix = prefix;
        _prefixLength = count;   // из префикса отдаём ровно count байт
        _pos = 0;
        _rest = rest;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        if (_pos < _prefixLength)
        {
            // Отдаём префикс, затем — остальное (в этом же вызове не продолжаем:
            // проще один источник за раз).
            var take = Math.Min(count, _prefixLength - _pos);
            Array.Copy(_prefix, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }
        return _rest.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        if (_pos < _prefixLength)
        {
            var take = Math.Min(buffer.Length, _prefixLength - _pos);
            _prefix.AsSpan(_pos, take).CopyTo(buffer);
            _pos += take;
            return take;
        }
        return _rest.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return Task.FromResult(0);
        if (_pos < _prefixLength)
        {
            var take = Math.Min(count, _prefixLength - _pos);
            Array.Copy(_prefix, _pos, buffer, offset, take);
            _pos += take;
            return Task.FromResult(take);
        }
        return _rest.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return ValueTask.FromResult(0);
        if (_pos < _prefixLength)
        {
            var take = Math.Min(buffer.Length, _prefixLength - _pos);
            _prefix.AsSpan(_pos, take).CopyTo(buffer.Span);
            _pos += take;
            return ValueTask.FromResult(take);
        }
        return _rest.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _rest.Dispose();
        base.Dispose(disposing);
    }
}
