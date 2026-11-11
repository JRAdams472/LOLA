using System.Buffers.Binary;
using System.Net.Sockets;
using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Real AV seam (MPE-201): ClamAV `zINSTREAM` over a raw TcpClient —
/// no package dependency, the protocol is three frames:
///   → "zINSTREAM\0"
///   → { u32be chunk_len | chunk_bytes }*
///   → u32be 0            (terminator)
///   ← "stream: OK\n" | "stream: <sig> FOUND\n" | "stream: <msg> ERROR\n"
/// The plaintext stream the worker hands in never touches disk —
/// it flows from VaultCipher's opening stream straight onto the wire
/// (invariant 1). A refused/timeout/ERROR reply is `Unavailable`,
/// never a silent clean — scanning failures fail closed.
public sealed class ClamAvScanner(string host, int port,
    TimeSpan? timeout = null) : IVirusScanner
{
    public string Adapter => "clamav";

    private const int ChunkLen = 64 * 1024;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    /// CLAMAV_ADDR is `host:port` — parse once at startup, fail fast.
    public static ClamAvScanner Parse(string address)
    {
        var split = address.LastIndexOf(':');
        if (split < 0 || !int.TryParse(address[(split + 1)..],
                out var port))
            throw new InvalidOperationException(
                $"CLAMAV_ADDR must be host:port — got '{address}'");
        return new ClamAvScanner(address[..split], port);
    }

    public async Task<ScanVerdict> ScanAsync(Stream content,
        CancellationToken ct = default)
    {
        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutCts.Token);
            await using var ns = client.GetStream();
            await ns.WriteAsync("zINSTREAM\0"u8.ToArray(),
                timeoutCts.Token);
            var buf = new byte[ChunkLen];
            var lenB = new byte[4];
            int n;
            while ((n = await content.ReadAsync(buf.AsMemory(),
                       timeoutCts.Token)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(lenB, (uint)n);
                await ns.WriteAsync(lenB, timeoutCts.Token);
                await ns.WriteAsync(buf.AsMemory(0, n), timeoutCts.Token);
            }
            await ns.WriteAsync(new byte[4], timeoutCts.Token);

            var resp = await ReadLineAsync(ns, timeoutCts.Token);
            if (resp.Equals("stream: OK", StringComparison.Ordinal))
                return new ScanVerdict(ScanStatus.Clean, null);
            if (resp.StartsWith("stream: ", StringComparison.Ordinal)
                && resp.EndsWith(" FOUND", StringComparison.Ordinal))
                return new ScanVerdict(ScanStatus.Infected,
                    resp["stream: ".Length..^" FOUND".Length]);
            return new ScanVerdict(ScanStatus.Unavailable,
                $"clamav replied '{resp}'");
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !ct.IsCancellationRequested)
        {
            // Refused, reset, timeout, malformed reply — all mean the
            // scan did not happen: Unavailable, never a silent clean.
            return new ScanVerdict(ScanStatus.Unavailable,
                $"clamav unreachable: {ex.Message}");
        }
    }

    /// zINSTREAM belongs to clamd's null-delimited command family —
    /// the reply is `stream: <verdict>\0`, so the line ends at the
    /// first \n or \0.
    private static async Task<string> ReadLineAsync(Stream ns,
        CancellationToken ct)
    {
        var buf = new byte[512];
        var got = 0;
        while (got < buf.Length)
        {
            var n = await ns.ReadAsync(
                buf.AsMemory(got, buf.Length - got), ct);
            if (n == 0) break;
            got += n;
            var end = got;
            for (var i = 0; i < got; i++)
                if (buf[i] is (byte)'\n' or 0) { end = i; break; }
            if (end < got) { got = end; break; }
        }
        return System.Text.Encoding.ASCII.GetString(buf, 0, got)
            .TrimEnd('\r');
    }
}
