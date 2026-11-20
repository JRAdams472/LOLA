namespace Lola.Application.Vault;

/// Pass-through stream that captures the first `maxBytes` of a
/// streamed read — the scan drain tees plaintext decrypt→INSTREAM,
/// so the classifier gets a bounded head without buffering the whole
/// document (25 MiB cap × a 25-row batch stays bounded).
public sealed class HeadCapture(Stream inner, int maxBytes) : Stream
{
    private readonly MemoryStream _captured = new();

    public byte[] Head => _captured.ToArray();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        Capture(buffer.Span[..n]);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        Capture(buffer.AsSpan(offset, n));
        return n;
    }

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        if (_captured.Length >= maxBytes) return;
        _captured.Write(
            bytes[..(int)Math.Min(bytes.Length,
                maxBytes - _captured.Length)]);
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
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();
    public override void SetLength(long value)
        => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}
