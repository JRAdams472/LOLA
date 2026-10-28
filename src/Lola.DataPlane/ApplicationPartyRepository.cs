using Dapper;
using Lola.Application.Security;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Relational index for parties + blind-index tokens (M5, ADR-0006).
/// The canonical party data lives in the JSON payload; this table
/// carries `vault:` ciphertext columns for queryable fields and
/// `idx:` blind tokens for equality search — plaintext never lands.
/// Sync runs inside the writer's open connection (single tenant file).
public sealed class ApplicationPartyRepository(
    TenantConnectionFactory factory,
    IFieldCipher cipher,
    IBlindIndex blind) : TenantRepository(factory)
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    /// Rebuild the party index + blind rows for an application's doc —
    /// full delete+insert under the writer's transaction boundary.
    public async Task SyncAsync(SqliteConnection conn, string applicationId,
        ApplicationDocument doc, CancellationToken ct)
    {
        await conn.ExecuteAsync(
            "DELETE FROM application_blind_idx WHERE application_id=@id",
            new { id = applicationId });
        await conn.ExecuteAsync(
            "DELETE FROM application_parties WHERE application_id=@id",
            new { id = applicationId });

        for (var i = 0; doc.Get($"party[{i}]") is not null; i++)
        {
            async Task<string?> Ct(string name)
            {
                var v = doc.Get($"party[{i}].{name}")?.GetValue<string>();
                return v is null ? null : await cipher.EncryptAsync(v, ct);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO application_parties
                    (id, application_id, party_index, role,
                     first_name_ct, middle_name_ct, last_name_ct, suffix_ct,
                     ssn_ct, dob_ct, email_ct, phone_ct)
                VALUES (@pid, @id, @i, @role, @fn, @mn, @ln, @sx, @ssn, @dob, @em, @ph)
                """, new
                {
                    pid = $"{applicationId}-p{i}",
                    id = applicationId,
                    i,
                    role = doc.Get($"party[{i}].role")?.GetValue<string>() ?? "borrower",
                    fn = await Ct("first_name"),
                    mn = await Ct("middle_name"),
                    ln = await Ct("last_name"),
                    sx = await Ct("suffix"),
                    ssn = await Ct("ssn"),
                    dob = await Ct("dob"),
                    em = await Ct("email"),
                    ph = await Ct("phone")
                });

            // Blind tokens for this party's searchable fields.
            foreach (var (field, node) in PartyLeaves(doc, i))
            {
                var spec = Catalog.SpecFor($"party[0].{field}");
                if (spec is null or { Index: not "blind" } or { PiiClass: null })
                    continue;
                await InsertBlindAsync(conn, applicationId, i, Trailing(field),
                    spec.PiiClass.Value, node.GetValue<string>(), ct);
            }
        }

        // App-level blind fields (subject-property address) at index -1.
        var street = doc.Get("subject_property.street")?.GetValue<string>();
        if (street is not null)
            await InsertBlindAsync(conn, applicationId, -1, "street",
                PiiClass.Address, street, ct);
    }

    /// Equality search over blind tokens — `field` is the trailing key
    /// ("ssn", "email", "phone", "first_name", "last_name", "street").
    /// Returns matching application ids; no plaintext crosses SQL.
    public async Task<IReadOnlyList<string>> SearchAsync(
        string field, string value, CancellationToken ct = default)
    {
        var spec = Catalog.Fields.FirstOrDefault(f =>
            f.Index == "blind" && f.Path.EndsWith($".{field}", StringComparison.Ordinal));
        if (spec?.PiiClass is null)
            throw new ArgumentException($"'{field}' is not a blind-indexed field", nameof(field));
        var idx = await blind.IndexAsync(spec.PiiClass.Value, value, ct);
        return await QueryAsync<string>(
            "SELECT DISTINCT application_id FROM application_blind_idx WHERE field=@field AND idx=@idx",
            new { field, idx }, ct);
    }

    private async Task InsertBlindAsync(SqliteConnection conn, string applicationId,
        int partyIndex, string field, PiiClass cls, string value, CancellationToken ct)
    {
        var idx = await blind.IndexAsync(cls, value, ct);
        await conn.ExecuteAsync(
            "INSERT OR REPLACE INTO application_blind_idx (application_id, party_index, field, idx) VALUES (@id,@i,@f,@idx)",
            new { id = applicationId, i = partyIndex, f = field, idx });
    }

    /// Scalar leaves under `party[i]` as (path-suffix, node) — walks the
    /// doc's actual leaves so nested structures (address[0]) surface.
    private static IEnumerable<(string Field, System.Text.Json.Nodes.JsonNode Node)>
        PartyLeaves(ApplicationDocument doc, int i)
    {
        var prefix = $"party[{i}].";
        foreach (var (path, node) in doc.Leaves())
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                yield return (path[prefix.Length..], node);
    }

    /// Blind rows key by trailing field name — `address[0].street` → "street".
    private static string Trailing(string field) =>
        field.Contains('.', StringComparison.Ordinal) || field.Contains('[', StringComparison.Ordinal)
            ? field[(field.LastIndexOf('.') + 1)..]
            : field;
}
