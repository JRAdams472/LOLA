namespace Lola.Application.Vault;

/// Document intelligence seam (M18, ADR-0007) — deterministic and
/// in-process, inside the tenant boundary: the classifier sees a
/// bounded plaintext head captured mid-scan, never durable bytes, and
/// nothing leaves LOLA (invariant 2). `DOC_CLASSIFIER` selects the
/// impl; a model or rules engine is a later swap behind this port.
public interface IDocClassifier
{
    string Adapter { get; }

    /// `doc` carries uploader metadata (declared classification,
    /// filename, sniffed content type); `head` is a bounded plaintext
    /// prefix — the first ~2 MiB tee'd off the decrypt→scan stream.
    Task<DocClassification> ClassifyAsync(VaultDocRecord doc,
        ReadOnlyMemory<byte> head, CancellationToken ct = default);
}

/// What the classifier decided about one vault doc. PII-class in the
/// aggregate — `Fields` hold extracted values — so the whole result
/// persists `vault:`-encrypted on the doc row (invariant 1) and
/// audit/events/UI see kind/confidence/finding names only (invariant 3).
public sealed record DocClassification(
    string DocKind,      // DocKind.* — detected, not declared
    double Confidence,   // 0..1
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<DocFinding> Findings);

/// One validation finding the classifier raised — `Kind` is a
/// DocFindingKind.* token; `Detail` is short metadata (never content).
public sealed record DocFinding(string Kind, string? Detail = null);

/// Detected kinds — matches the uploader-declared classification
/// vocabulary so detected-vs-declared can produce `kind_mismatch`.
public static class DocKind
{
    public const string W2 = "w2";
    public const string Paystub = "paystub";
    public const string BankStatement = "bank_statement";
    public const string Id = "id";
    public const string FloodCert = "flood_cert";
    public const string Misc = "misc";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { W2, Paystub, BankStatement, Id,
            FloodCert, Misc };

    /// Upload-declared vocab uses `bank-statement` (borrower language,
    /// UploadAllowlist.Classifications); detected kinds use
    /// `bank_statement`. Normalize before comparing so a correctly
    /// declared doc never false-fires `kind_mismatch`.
    public static string? Normalize(string? kind)
        => kind?.Replace('-', '_');
}

/// Finding tokens the classifier may raise. `expired_doc` and
/// `kind_mismatch` open replace-doc conditions (P3); `missing_field`
/// is advisory; `unreadable` flags docs the rule engine cannot read
/// (image-only scans — an honest limit, not a gap).
public static class DocFindingKind
{
    public const string ExpiredDoc = "expired_doc";
    public const string Unreadable = "unreadable";
    public const string KindMismatch = "kind_mismatch";
    public const string MissingField = "missing_field";
}
