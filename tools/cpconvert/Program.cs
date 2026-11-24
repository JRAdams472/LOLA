using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;
using Lola.Tools.CpConvert;
using Microsoft.Extensions.Options;

// cpconvert <source.db> <dest.db> --key <hex|@file>
// Offline control-plane conversion (MPE-287). Operator-run only —
// never a web path, never a plaintext temp export.

var args2 = args;
if (args2.Length < 4 || args2[2] != "--key")
{
    Console.Error.WriteLine(
        "usage: cpconvert <source.db> <dest.db> --key <hex|@file>");
    return 2;
}

var keyArg = args2[3];
var destKey = (keyArg.StartsWith('@')
    ? (await File.ReadAllTextAsync(keyArg[1..])).Trim()
    : keyArg).ToUpperInvariant();
if (destKey.Length != 64)
{
    Console.Error.WriteLine("destination key must be 64 hex chars");
    return 2;
}

var addr = Environment.GetEnvironmentVariable("OPENBAO_ADDR")
    ?? "http://localhost:8300";
var token = Environment.GetEnvironmentVariable("OPENBAO_TOKEN")
    ?? (Environment.GetEnvironmentVariable("OPENBAO_TOKEN_FILE") is { } f
        ? (await File.ReadAllTextAsync(f)).Trim() : null)
    ?? throw new InvalidOperationException(
        "OPENBAO_TOKEN or OPENBAO_TOKEN_FILE required — conversion " +
        "encrypts through transit, never plaintext-fallback.");

var http = new HttpClient { BaseAddress = new Uri(addr) };
var transit = new OpenBaoTransitClient(http,
    Options.Create(new OpenBaoOptions { Address = addr, Token = token }));
var converter = new CpConverter(
    new TransitFieldCipher(transit), new TransitBlindIndex(transit));

Console.Error.WriteLine(
    $"cpconvert: {args2[0]} → {args2[1]} (custody backup → .cpbak)");
var result = await converter.ConvertAsync(args2[0], args2[1], destKey);
foreach (var (t, n) in result.RowCounts)
    Console.WriteLine($"  {t}: {n} rows");
Console.WriteLine(
    $"audit chain ok ({result.AuditEvents} events) · " +
    $"round-trip ok · fk ok" +
    (result.Resumed.Count > 0
        ? $" · resumed {result.Resumed.Count} table(s)" : ""));
Console.WriteLine(
    "VALIDATED — switch is an operator step (see " +
    "docs/control-plane-encryption-runbook.md): stop writers, " +
    "atomic-rename dest over source, restart.");
return 0;
