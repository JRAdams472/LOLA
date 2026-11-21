using Dapper;
using Lola.Application.Applications;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Security;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using System.Text.Json.Nodes;

namespace Lola.DataPlane;

/// Real consumer of the tenant repository seam (M5): the canonical JSON1
/// payload plus append-only versions (ADR-0006). The crypto boundary is
/// here — the stored payload and index columns hold `vault:` ciphertext
/// for every catalog PII path; `GetAsync` returns a decrypted doc to
/// the application layer, which is already inside the tenant's
/// CipherContext.
public sealed class ApplicationRepository(
    TenantConnectionFactory factory,
    IFieldCipher cipher,
    ApplicationPartyRepository parties,
    IBlindIndex blind,
    LoanEventLog events) : TenantRepository(factory), IApplicationRepository
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

    /// Draft merge-patch (MPE-216): the whole read→merge→append runs
    /// inside one BEGIN IMMEDIATE write transaction — same atomic
    /// read+write discipline as the audit chain — so concurrent PATCHes
    /// serialize on the tenant file and merge against current state
    /// instead of last-writer-wins dropping disjoint fields. The draft
    /// status check rides inside the lock so a racing submit can't be
    /// followed by a patch that flips status back. Crypto boundary is
    /// unchanged: reveal/merge/protect happens in memory and the
    /// payload column still carries only `vault:` ciphertext.
    public async Task<(int Version, ApplicationDocument Doc)> PatchDraftAsync(
        string id, IReadOnlyDictionary<string, string> patch,
        string writtenBy, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var row = await conn.QuerySingleOrDefaultAsync<Row>(
                """
                SELECT a.id AS Id, a.status AS Status, a.pipeline_step AS PipelineStep,
                       a.borrower_user_id AS BorrowerUserId, a.current_version AS CurrentVersion,
                       a.trid_clock_start AS TridClockStart,
                       v.payload AS Payload, v.submitted_utc AS SubmittedUtc
                FROM applications a
                JOIN application_versions v
                  ON v.application_id = a.id AND v.version = a.current_version
                WHERE a.id = @id
                """, new { id });
            if (row is null)
                throw new KeyNotFoundException($"application '{id}' not found");
            if (row.Status != "draft")
                throw DomainException.BadRequest("only drafts can be patched");

            var doc = await RevealAsync(ApplicationDocument.Parse(row.Payload), ct);
            // Mutation event log (M15): per-path field_change rows ride
            // this same tx — before-values come from the doc just
            // revealed inside the lock, so they can't be raced; a
            // rollback below takes the events with it.
            var drafts = await LoanEventWriter.FieldChangesAsync(
                id, writtenBy, doc, patch, blind, ct);
            foreach (var d in drafts)
                await events.WriteOnConnectionAsync(d, conn, ct);
            foreach (var (path, value) in patch)
                doc.Set(path, value);
            var protected_ = await ProtectAsync(doc, ct);

            var version = (int)row.CurrentVersion + 1;
            await conn.ExecuteAsync(
                """
                INSERT INTO application_versions
                    (id, application_id, version, status_at_write, payload,
                     prev_version_id, written_by)
                VALUES (@vid, @id, @version, @status, @payload, @prev, @writtenBy)
                """, new
                {
                    vid = $"{id}-v{version}",
                    id,
                    version,
                    status = row.Status,
                    payload = protected_.ToJson(),
                    prev = $"{id}-v{row.CurrentVersion}",
                    writtenBy
                });
            await conn.ExecuteAsync(
                """
                UPDATE applications SET current_version=@version, status=@status,
                       updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE id=@id
                """, new { version, status = row.Status, id });
            await parties.SyncAsync(conn, id, doc, ct);
            await conn.ExecuteAsync("COMMIT");
            return (version, doc);
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
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

    /// Pipeline rows (M11): party[0]'s name columns ride the join as
    /// ciphertext and decrypt through the same transit boundary as the
    /// doc payload — one batch round-trip, never plaintext at rest.
    /// Masking happens in the handler (VisibilityContext), not here.
    public async Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default)
    {
        var rows = await QueryAsync<PipelineRow>(
            """
            SELECT a.id AS Id, a.status AS Status, a.pipeline_step AS PipelineStep,
                   a.borrower_user_id AS BorrowerUserId, a.current_version AS CurrentVersion,
                   a.trid_clock_start AS TridClockStart, a.updated_utc AS UpdatedUtc,
                   p.first_name_ct AS FirstNameCt, p.last_name_ct AS LastNameCt
            FROM applications a
            LEFT JOIN application_parties p
              ON p.application_id = a.id AND p.party_index = 0
            ORDER BY a.created_utc DESC
            """, ct: ct);

        var ciphertexts = rows
            .SelectMany(r => new[] { r.FirstNameCt, r.LastNameCt })
            .Where(c => c is not null).Select(c => c!).Distinct().ToList();
        IReadOnlyList<string> plaintexts = ciphertexts.Count == 0
            ? Array.Empty<string>()
            : await cipher.DecryptAsync(ciphertexts, ct);
        var plain = ciphertexts.Zip(plaintexts)
            .ToDictionary(x => x.First, x => x.Second);

        return rows.Select(r =>
        {
            var name = string.Join(' ', new[]
                {
                    r.FirstNameCt is null ? null : plain[r.FirstNameCt],
                    r.LastNameCt is null ? null : plain[r.LastNameCt]
                }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return new ApplicationMeta(r.Id, r.Status, r.PipelineStep,
                r.BorrowerUserId, r.CurrentVersion, r.TridClockStart,
                name.Length == 0 ? null : name, null, r.UpdatedUtc);
        }).ToList();
    }

    /// Reg B retention input (MPE-118) — id + created_utc, no payload.
    public async Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
        CancellationToken ct = default)
        => await QueryAsync<ApplicationRetentionRow>(
            """
            SELECT id AS Id, created_utc AS CreatedUtc
            FROM applications ORDER BY created_utc
            """, ct: ct);

    /// Status-only transition — never touches version rows.
    public async Task UpdateStatusAsync(string id, string status,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE applications SET status=@status,
                   updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE id=@id
            """, new { status, id }, ct);

    /// Once-only stamp — the NULL guard makes repeats no-ops so the
    /// first co-presence timestamp is immutable.
    public async Task EnsureTridClockStartAsync(string id,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE applications SET trid_clock_start=strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                   updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE id=@id AND trid_clock_start IS NULL
            """, new { id }, ct);

    public async Task UpdatePipelineStepAsync(string id, string step,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE applications SET pipeline_step=@step,
                   updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE id=@id
            """, new { step, id }, ct);

    /// Append-only step event — ids come from the application id +
    /// a monotonic suffix derived inside the row insert.
    public async Task RecordStepEventAsync(string id, string step,
        string event_, string actor, string? reason,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            INSERT INTO application_step_events
                (id, application_id, step, event, actor, reason)
            VALUES (@eventId, @id, @step, @event, @actor, @reason)
            """,
            new
            {
                eventId = $"{id}-step-{Guid.NewGuid():N}",
                id,
                step,
                @event = event_,
                actor,
                reason
            }, ct);

    public async Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
        string id, CancellationToken ct = default)
        => await QueryAsync<StepEventDto>(
            """
            SELECT id AS Id, step AS Step, event AS Event, actor AS Actor,
                   reason AS Reason, created_utc AS CreatedUtc
            FROM application_step_events
            WHERE application_id=@id ORDER BY created_utc, rowid
            """, new { id }, ct);

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

    /// M19 partner row-scope read — an ACTIVE link only; revoked rows
    /// are provenance, not scope.
    public async Task<bool> IsPartnerLinkedAsync(
        string id, string partnerUserId, CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<long>(
            """
            SELECT COUNT(1) FROM application_partners
            WHERE app_id = @id AND partner_user_id = @partnerUserId
              AND revoked_utc IS NULL
            """, new { id, partnerUserId }, ct) == 1;

    /// The partner-scoped list (M19) — ids only; the caller intersects
    /// with the already-decrypted pipeline list.
    public async Task<IReadOnlyList<string>> LinkedAppIdsForPartnerAsync(
        string partnerUserId, CancellationToken ct = default)
        => (await QueryAsync<string>(
            """
            SELECT app_id FROM application_partners
            WHERE partner_user_id = @partnerUserId AND revoked_utc IS NULL
            """, new { partnerUserId }, ct)).ToList();

    private sealed record Row(
        string Id, string Status, string PipelineStep, string? BorrowerUserId,
        long CurrentVersion, string? TridClockStart, string Payload, string? SubmittedUtc);

    private sealed record MetaRow(long CurrentVersion, string VersionId);

    private sealed record PipelineRow(
        string Id, string Status, string PipelineStep,
        string? BorrowerUserId, long CurrentVersion, string? TridClockStart,
        string? UpdatedUtc, string? FirstNameCt, string? LastNameCt);
}
