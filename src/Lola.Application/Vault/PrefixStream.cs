namespace Lola.Application.Vault;

/// Prepends already-consumed head bytes back in front of a stream —
/// the upload path reads SniffBytes for magic-byte validation, then
/// the cipher must see those same bytes again (they're part of the
/// plaintext that gets hashed + sealed). Async-read only.
internal sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
{
    private int _pos;

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _pos);
            prefix.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        return await inner.ReadAsync(buffer, ct);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException(
            "PrefixStream is async-read only");
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) =>
        throw new NotSupportedException();
    public override void SetLength(long v) =>
        throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) =>
        throw new NotSupportedException();
}
