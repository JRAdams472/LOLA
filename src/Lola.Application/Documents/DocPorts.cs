namespace Lola.Application.Documents;

/// Document-generation adapter seam (ADR-0007, MPE-72). Core LOS logic
/// talks ONLY to this interface — ISPPr is the first implementation;
/// Docutech-class vendors arrive behind the same contract with zero
/// core changes (same swap story as IPricingService/IFeeService).
///
/// Unlike pricing/fees, this egress legitimately carries PII — a Loan
/// Estimate prints borrower names and the property address. Invariant 2
/// holds as "minimal-template-fields": payloads are assembled by
/// DisclosurePayloadBuilder and bounded by templates/manifest.json —
/// the adapter never sees the application document itself.
public interface IDocumentService
{
    /// Adapter identity stamped onto doc rows ("stub", "isppr").
    string Adapter { get; }

    /// Submit a package for generation. Idempotent on the vendor side
    /// via `IdempotencyKey` — a replay returns the original request.
    Task<DocGeneration> GenerateAsync(DocPackageRequest request,
        CancellationToken ct = default);

    /// Poll a submitted request (202-async path).
    Task<DocGeneration> GetStatusAsync(string requestId,
        CancellationToken ct = default);

    /// A fresh ≤15-min presigned download URL for a COMPLETED package —
    /// minted per call, never persisted.
    Task<string> GetDownloadUrlAsync(string requestId,
        CancellationToken ct = default);
}

/// One generation request — the ISPPr wire shape: a package of
/// `{template_id, payload}` documents bound into Handlebars templates
/// and zipped. `Payload` is a pre-assembled, manifest-bounded JSON
/// object — the ONLY place template-required PII crosses.
public sealed record DocPackageRequest(
    string CustomerId, string PackageName, string IdempotencyKey,
    IReadOnlyList<DocPackageDocument> Documents);

public sealed record DocPackageDocument(
    string TemplateId, System.Text.Json.Nodes.JsonObject Payload,
    bool? PrintReady = null);

/// Normalized generation state — terminal statuses: completed/failed.
/// `Filename` is the vendor's zip name (e.g. `{name}_{requestId}.zip`).
public sealed record DocGeneration(
    string RequestId, string Status, string? Filename,
    string? Error = null);

/// One persisted package row (tenant-file `documents`, MPE-190).
/// Status: pending → generating → completed | failed. Registry of
/// references — LOLA stores the vendor request id + filename; the
/// package bytes stay vendor-side behind presigned URLs.
public sealed record DocRecord(
    string DocId, string ApplicationId, string Kind,
    string PackageName, string Adapter, string? RequestId,
    string? Filename, string Status, string? Error, string Actor,
    string? CreatedUtc, string? CompletedUtc, string RequestHash);

/// One queued outbound generation (tenant-file `doc_outbox`) —
/// same semantics as fee_outbox/pricing_outbox. `RequestId` joins
/// the doc row's vendor request id — non-null once submitted, so the
/// processor polls rather than re-generating. `PayloadJson` is the
/// serialized `DocPackageRequest`; at rest it's `vault:` ciphertext
/// (the disclosure payload carries template-required PII), decrypted
/// by the repository on read.
public sealed record DocOutboxRow(
    string OutboxId, string Kind, string DocId, string RequestHash,
    string PayloadJson, string Status, long Attempts,
    string? NextRetryUtc, string? LastError,
    string? RequestId = null);

/// Enqueue outcome — a duplicated request returns the ORIGINAL doc id.
public sealed record DocEnqueueResult(string DocId, bool Deduplicated);

/// Doc persistence seam (MPE-190). Same transaction discipline as
/// IFeeRepository — enqueue is row+outbox atomically, completion is
/// outbox+doc atomically.
public interface IDocumentRepository
{
    Task<DocEnqueueResult> EnqueueAsync(DocRecord doc,
        string outboxId, string kind, string payloadJson,
        CancellationToken ct = default);

    Task<DocRecord?> GetDocAsync(string docId,
        CancellationToken ct = default);
    Task<IReadOnlyList<DocRecord>> ListDocsAsync(string appId,
        CancellationToken ct = default);

    Task<IReadOnlyList<DocOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default);

    /// Generation submitted — outbox stays pending (rescheduled for
    /// the next poll), doc goes `generating` with the vendor request
    /// id stamped for polling.
    Task MarkGeneratingAsync(string outboxId, string docId,
        string requestId, string nextRetryUtc,
        CancellationToken ct = default);

    /// Terminal success — outbox done + doc completed/filename. The
    /// vendor request id is stamped here too: an immediately-completed
    /// generate never passes through `MarkGeneratingAsync`, and the
    /// download path needs it to mint presigned URLs.
    Task CompleteAsync(string outboxId, string docId,
        string requestId, string? filename, string completedUtc,
        CancellationToken ct = default);

    Task RescheduleAsync(string outboxId, string docId, long attempts,
        string nextRetryUtc, string error, CancellationToken ct = default);

    Task DeadLetterAsync(string outboxId, string docId, long attempts,
        string error, CancellationToken ct = default);
}

/// Wire-facing registry row.
public sealed record DocDto(
    string DocId, string ApplicationId, string Kind,
    string PackageName, string Status, string? Filename,
    string? Error, string? CreatedUtc, string? CompletedUtc);

/// Per-tenant vendor credential resolution (MPE-190). The adapter
/// never sees the registry row — it asks for the API key under the
/// ambient TenantContext, and the impl returns the plaintext for the
/// single outbound call. NEVER log the returned value.
public interface IDocumentCredentialSource
{
    /// The current tenant's plaintext vendor API key, or null when
    /// the tenant isn't provisioned for documents.
    Task<string?> ApiKeyAsync(CancellationToken ct = default);
}

/// Vendor-side admin surface (ISPPr management API) — session-auth,
/// used ONLY by provisioning/erasure flows, never request paths.
/// Per-tenant ensure methods are idempotent so provisioning retries
/// converge instead of duplicating.
public interface IDocVendorAdmin
{
    /// Client id for `name`, creating it when absent.
    Task<string> EnsureClientAsync(string name,
        CancellationToken ct = default);

    /// Customer id under `clientId` keyed by `externalRef`.
    Task<string> EnsureCustomerAsync(string clientId, string name,
        string externalRef, CancellationToken ct = default);

    /// Mint an API key — plaintext is returned exactly once; callers
    /// wrap + persist immediately and drop the plaintext.
    Task<(string KeyId, string PlaintextKey)> CreateApiKeyAsync(
        string clientId, string label, IReadOnlyList<string> scopes,
        CancellationToken ct = default);

    /// Upload a template master — false when it already exists
    /// (vendor 409 = already provisioned, not an error).
    Task<bool> UploadTemplateAsync(string clientId, string name,
        string html, CancellationToken ct = default);

    /// Revoke a vendor key by id (tenant erasure, MPE-191).
    Task RevokeApiKeyAsync(string keyId, CancellationToken ct = default);
}

/// Provisioning step seam (MPE-190): wires a tenant's ISPPr
/// client/customer/api-key/templates into the registry row.
/// Re-entrant like every other provisioning step — a partial run
/// resumes where it stopped.
public interface ITenantDocProvisioner
{
    Task EnsureAsync(Domain.Identity.TenantRecord tenant,
        CancellationToken ct = default);
}

/// Typed adapter failure — Retryable drives outbox reschedule-vs-DLQ
/// (mirrors FeeAdapterException semantics).
public sealed class DocAdapterException(
    DocErrorKind kind, string message, bool retryable)
    : Exception(message)
{
    public DocErrorKind Kind { get; } = kind;
    public bool Retryable { get; } = retryable;

    public static DocAdapterException Auth(string m) =>
        new(DocErrorKind.Auth, m, false);
    public static DocAdapterException Rejected(string m) =>
        new(DocErrorKind.Rejected, m, false);
    public static DocAdapterException Unavailable(string m) =>
        new(DocErrorKind.Unavailable, m, true);
    public static DocAdapterException Contract(string m) =>
        new(DocErrorKind.Contract, m, false);
}

public enum DocErrorKind { Auth, Rejected, Unavailable, Contract }
