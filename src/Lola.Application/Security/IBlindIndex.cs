using Lola.Domain.Classification;

namespace Lola.Application.Security;

/// Deterministic blind index for searchable PII (SSN/phone/email).
/// Values are normalized per <see cref="PiiClass"/> then HMAC'd under a
/// dedicated per-tenant `hmac` transit key — the stored ciphertext stays
/// randomized while `idx:vN:` tokens enable equality search. Index-key
/// rotation changes future tokens; a reindex sweep is required (runbook).
public interface IBlindIndex
{
    /// Normalized `idx:vN:<hmac>` for the current tenant
    /// (<see cref="CipherContext"/>). Deterministic: same input → same
    /// token within a key version.
    Task<string> IndexAsync(PiiClass classification, string value, CancellationToken ct = default);
}
