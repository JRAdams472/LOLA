using Dapper;
using Lola.Application.Security;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using System.Text.Json.Nodes;

namespace Lola.DataPlane;

/// One application row plus its current plaintext document — the shape
/// handlers consume. `SubmittedUtc` non-null marks an immutable snapshot
/// (invariant 6); TridClockStart is stamped once (P4).
public sealed record ApplicationRecord(
    string Id,
    string Status,
    string PipelineStep,
    string? BorrowerUserId,
    int CurrentVersion,
    string? TridClockStart,
    string? SubmittedUtc,
    ApplicationDocument Doc);

/// Real consumer of the tenant repository seam (M5): the canonical JSON1
/// payload plus append-only versions (ADR-0006). The crypto boundary is
/// here — the stored payload and index columns hold `vault:` ciphertext
/// for every catalog PII path; `GetAsync` returns a decrypted doc to
/// the application layer, which is already inside the tenant's
/// CipherContext.
public sealed class ApplicationRepository(
    TenantConnectionFactory factory,
    IFieldCipher cipher,
    ApplicationPartyRepository parties) : TenantRepository(factory)
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public async Task InsertAsync(string id, ApplicationDocument doc,
        string status, string? borrowerUserId, string writtenBy,
        CancellationToken ct = default)
    {
        var protected_ = await ProtectAsync(doc, ct);
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO applications (id, status, borrower_user_id, current_version)
            VALUES (@id, @status, @borrowerUserId, 1)
            """, new { id, status, borrowerUserId });
        var versionId = $"{id}-v1";
        await conn.ExecuteAsync(
            """
            INSERT INTO application_versions
                (id, application_id, version, status_at_write, payload, written_by)
            VALUES (@vid, @id, 1, @status, @payload, @writtenBy)
            """, new { vid = versionId, id, status, payload = protected_.ToJson(), writtenBy });
        await parties.SyncAsync(conn, id, doc, ct);
    }

    /// Append a NEW version — submitted rows are never updated
    /// (invariant 6). Returns the new version number.
    public async Task<int> WriteVersionAsync(string id, ApplicationDocument doc,
        string status, string writtenBy, bool submitted,
        CancellationToken ct = default)
    {
        var current = await GetMetaAsync(id, ct)
            ?? throw new KeyNotFoundException($"application '{id}' not found");
        var version = (int)current.CurrentVersion + 1;
        var protected_ = await ProtectAsync(doc, ct);
        var versionId = $"{id}-v{version}";
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO application_versions
                (id, application_id, version, status_at_write, payload,
                 submitted_utc, prev_version_id, written_by)
            VALUES (@vid, @id, @version, @status, @payload,
                    CASE WHEN @submitted THEN strftime('%Y-%m-%dT%H:%M:%fZ','now') END,
                    @prev, @writtenBy)
            """, new
            {
                vid = versionId,
                id,
                version,
                status,
                payload = protected_.ToJson(),
                submitted,
                prev = current.VersionId,
                writtenBy
            });
        await conn.ExecuteAsync(
            """
            UPDATE applications SET current_version=@version, status=@status,
                   updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE id=@id
            """, new { version, status, id });
        await parties.SyncAsync(conn, id, doc, ct);
        return version;
    }

    /// Current version with the payload DECRYPTED for the app layer.
    /// Catalog PII paths are the only members touched.
    public async Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        var row = await QuerySingleOrDefaultAsync<Row>(
            """
            SELECT a.id AS Id, a.status AS Status, a.pipeline_step AS PipelineStep,
                   a.borrower_user_id AS BorrowerUserId, a.current_version AS CurrentVersion,
                   a.trid_clock_start AS TridClockStart,
                   v.payload AS Payload, v.submitted_utc AS SubmittedUtc
            FROM applications a
            JOIN application_versions v
              ON v.application_id = a.id AND v.version = a.current_version
            WHERE a.id = @id
            """, new { id }, ct);
        if (row is null) return null;
        var doc = await RevealAsync(ApplicationDocument.Parse(row.Payload), ct);
        return new ApplicationRecord(row.Id, row.Status, row.PipelineStep,
            row.BorrowerUserId, (int)row.CurrentVersion, row.TridClockStart,
            row.SubmittedUtc, doc);
    }

    public async Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default)
        => await QueryAsync<ApplicationMeta>(
            """
            SELECT id AS Id, status AS Status, pipeline_step AS PipelineStep,
                   current_version AS CurrentVersion, trid_clock_start AS TridClockStart
            FROM applications ORDER BY created_utc DESC
            """, ct: ct);

    private async Task<MetaRow?> GetMetaAsync(string id, CancellationToken ct)
    {
        var row = await QuerySingleOrDefaultAsync<MetaRow>(
            """
            SELECT a.current_version AS CurrentVersion,
                   a.id || '-v' || a.current_version AS VersionId
            FROM applications a WHERE a.id = @id
            """, new { id }, ct);
        return row;
    }

    /// Encrypt every catalog PII leaf in a doc copy — walks the doc's
    /// actual leaves (party[0..n] all covered via SpecFor's normalized
    /// lookup) so the written payload carries `vault:` ciphertext,
    /// never plaintext.
    public async Task<ApplicationDocument> ProtectAsync(
        ApplicationDocument doc, CancellationToken ct)
    {
        var copy = ApplicationDocument.FromObject(doc.CloneRoot());
        foreach (var (path, node) in copy.Leaves())
        {
            if (Catalog.SpecFor(path)?.PiiClass is null) continue;
            copy.Set(path, await cipher.EncryptAsync(LeafText(node), ct));
        }
        return copy;
    }

    /// Decrypt catalog PII leaves in place — returns the same doc for
    /// fluent use; callers inside CipherContext see plaintext.
    public async Task<ApplicationDocument> RevealAsync(
        ApplicationDocument doc, CancellationToken ct)
    {
        foreach (var (path, node) in doc.Leaves())
        {
            if (Catalog.SpecFor(path)?.PiiClass is null) continue;
            if (node is not JsonValue jv || !jv.TryGetValue<string>(out var value)) continue;
            if (!value.StartsWith("vault:", StringComparison.Ordinal)) continue;
            doc.Set(path, await cipher.DecryptAsync(value, ct));
        }
        return doc;
    }

    /// PII leaves can be non-string JSON (e.g. income decimals) —
    /// encrypt their text form either way.
    private static string LeafText(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : node.ToJsonString();

    private sealed record Row(
        string Id, string Status, string PipelineStep, string? BorrowerUserId,
        long CurrentVersion, string? TridClockStart, string Payload, string? SubmittedUtc);

    private sealed record MetaRow(long CurrentVersion, string VersionId);
}

public sealed record ApplicationMeta(
    string Id, string Status, string PipelineStep, long CurrentVersion, string? TridClockStart);
