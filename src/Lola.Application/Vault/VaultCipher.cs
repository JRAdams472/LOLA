using System.Buffers.Binary;
using System.Security.Cryptography;
using Lola.Application.Security;

namespace Lola.Application.Vault;

/// App-layer envelope for vault objects (ADR-0004, MPE-200): each
/// object gets a random 32-byte DEK; bytes are sealed as chunked
/// AES-256-GCM so streams stay bounded to one chunk of memory, and
/// the DEK is wrapped by the tenant's transit key (`vault:` string,
/// stored on the row). MinIO — or any store — only ever holds
/// ciphertext; shredding the tenant key bricks every object.
///
/// Wire format v1 (little-endian):
///   "LVL1" | chunk_size u32 | { ct_len u32 | nonce 12 | ct | tag 16 }*
///   | terminator ct_len u32 = 0 (no nonce follows — a length-first
///   frame makes truncation past the final tag detectable)
///
/// Chunk nonces are random 96-bit — uniqueness by construction.
/// Plaintext SHA-256 is taken while sealing: the row's integrity
/// field proves the download round-trips the upload byte-for-byte.
public sealed class VaultCipher(IFieldCipher fields)
{
    internal const int MagicLen = 4;               // "LVL1"
    private const int DefaultChunk = 1 << 20;      // 1 MiB plaintext
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int DekLen = 32;

    /// Pull-seal: hand the returned stream to IObjectVault.PutAsync —
    /// ciphertext is produced lazily as the vault reads. Read
    /// WrappedDek/Sha256Hex/PlaintextLength only after full
    /// consumption. `maxPlaintextBytes` throws VaultTooLargeException
    /// mid-stream (the caller deletes the partial object).
    public SealingStream Seal(Stream plaintext,
        long maxPlaintextBytes, int chunkSize = DefaultChunk) =>
        new(plaintext, fields, maxPlaintextBytes, chunkSize);

    /// Pull-open: plaintext is emitted chunk-at-a-time, each only
    /// after its GCM tag verifies; a tampered or truncated object
    /// throws CryptographicException instead of emitting bad bytes.
    public async Task<OpeningStream> OpenAsync(string wrappedDek,
        Stream ciphertext, CancellationToken ct = default)
    {
        var dek = Convert.FromBase64String(
            await fields.DecryptAsync(wrappedDek, ct));
        return new OpeningStream(ciphertext, dek);
    }

    /// Ciphertext-producing read stream. Only ReadAsync is supported
    /// (the S3 SDK and our copy paths never sync-read).
    public sealed class SealingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IFieldCipher _fields;
        private readonly long _max;
        private readonly int _chunkSize;
        private readonly byte[] _dek = new byte[DekLen];
        private readonly AesGcm _gcm;
        private readonly IncrementalHash _hasher =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _ptBuf;
        private byte[] _outBuf = [];
        private int _outPos;
        private bool _initialized;
        private bool _eof;
        private long _plaintext;

        public string WrappedDek { get; private set; } = "";
        public string Sha256Hex { get; private set; } = "";
        public long PlaintextLength => _plaintext;

        internal SealingStream(Stream inner, IFieldCipher fields,
            long maxPlaintextBytes, int chunkSize)
        {
            _inner = inner; _fields = fields; _max = maxPlaintextBytes;
            _chunkSize = chunkSize; _ptBuf = new byte[chunkSize];
            RandomNumberGenerator.Fill(_dek);
            _gcm = new AesGcm(_dek, TagLen);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_outPos >= _outBuf.Length)
            {
                if (_eof) return 0;
                await FillAsync(ct);
            }
            var n = Math.Min(buffer.Length, _outBuf.Length - _outPos);
            _outBuf.AsMemory(_outPos, n).CopyTo(buffer);
            _outPos += n;
            return n;
        }

        private async Task FillAsync(CancellationToken ct)
        {
            using var ms = new MemoryStream();
            if (!_initialized)
            {
                WrappedDek = await _fields.EncryptAsync(
                    Convert.ToBase64String(_dek), ct);
                ms.Write("LVL1"u8);
                var hdr = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(
                    hdr, (uint)_chunkSize);
                ms.Write(hdr);
                _initialized = true;
            }

            if (!_eof)
            {
                var n = await _inner.ReadAsync(
                    _ptBuf.AsMemory(0, _chunkSize), ct);
                if (n == 0)
                {
                    _eof = true;
                    var term = new byte[4]; // ct_len = 0 terminator
                    ms.Write(term);
                    Sha256Hex = Convert.ToHexStringLower(
                        _hasher.GetHashAndReset());
                }
                else
                {
                    _plaintext += n;
                    if (_plaintext > _max)
                        throw new VaultTooLargeException(_max);
                    _hasher.AppendData(_ptBuf, 0, n);
                    var nonce = RandomNumberGenerator.GetBytes(NonceLen);
                    var frame = new byte[n + TagLen];
                    _gcm.Encrypt(nonce, _ptBuf.AsSpan(0, n),
                        frame.AsSpan(0, n), frame.AsSpan(n, TagLen));
                    var len = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        len, (uint)n);
                    ms.Write(len); ms.Write(nonce);
                    ms.Write(frame, 0, n + TagLen);
                }
            }
            _outBuf = ms.ToArray();
            _outPos = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException(
                "SealingStream is async-read only");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
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
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _gcm.Dispose(); _hasher.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// Plaintext-producing read stream — verifies the header and each
    /// chunk's tag as it goes; CryptographicException on any tamper.
    public sealed class OpeningStream : Stream
    {
        private readonly Stream _inner;
        private readonly AesGcm _gcm;
        private int _chunkSize;
        private byte[] _ptBuf = [];
        private int _ptPos;
        private bool _initialized;
        private bool _terminated;

        internal OpeningStream(Stream inner, byte[] dek)
        { _inner = inner; _gcm = new AesGcm(dek, TagLen); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_ptPos >= _ptBuf.Length)
            {
                if (_terminated) return 0;
                await FillAsync(ct);
            }
            var n = Math.Min(buffer.Length, _ptBuf.Length - _ptPos);
            _ptBuf.AsMemory(_ptPos, n).CopyTo(buffer);
            _ptPos += n;
            return n;
        }

        private async Task FillAsync(CancellationToken ct)
        {
            if (!_initialized)
            {
                var magic = await ReadExactAsync(4, ct);
                if (!magic.AsSpan().SequenceEqual("LVL1"u8))
                    throw new CryptographicException(
                        "not an LVL1 vault object");
                var hdr = await ReadExactAsync(4, ct);
                _chunkSize = checked((int)BinaryPrimitives
                    .ReadUInt32LittleEndian(hdr));
                if (_chunkSize is < 1 or > (64 << 20))
                    throw new CryptographicException(
                        "invalid chunk size in vault object");
                _initialized = true;
            }
            var lenB = await ReadExactAsync(4, ct);
            var ctLen = checked((int)BinaryPrimitives
                .ReadUInt32LittleEndian(lenB));
            if (ctLen == 0)
            {
                _terminated = true; _ptBuf = []; _ptPos = 0;
                return;                       // clean EOF marker
            }
            if (ctLen > _chunkSize)
                throw new CryptographicException(
                    "chunk length exceeds declared chunk size");
            var nonce = await ReadExactAsync(NonceLen, ct);
            var frame = await ReadExactAsync(ctLen + TagLen, ct);
            var pt = new byte[ctLen];
            _gcm.Decrypt(nonce, frame.AsSpan(0, ctLen),
                frame.AsSpan(ctLen, TagLen), pt);
            _ptBuf = pt; _ptPos = 0;
        }

        private async Task<byte[]> ReadExactAsync(int n,
            CancellationToken ct)
        {
            var buf = new byte[n];
            var got = 0;
            while (got < n)
            {
                var r = await _inner.ReadAsync(
                    buf.AsMemory(got, n - got), ct);
                if (r == 0)
                    throw new CryptographicException(
                        "truncated vault object");
                got += r;
            }
            return buf;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException(
                "OpeningStream is async-read only");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
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
        protected override void Dispose(bool disposing)
        { if (disposing) _gcm.Dispose(); base.Dispose(disposing); }
    }
}
