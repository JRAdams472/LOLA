using Dapper;
using Lola.Application.Audit;
using Lola.Application.Security;
using Lola.ControlPlane;
using Lola.Domain.Classification;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Lola.Tools.CpConvert;

/// Offline control-plane conversion (MPE-287, design §6): streams an
/// unkeyed plaintext-source control.db into a SEPARATE keyed
/// destination, encrypting classified columns and computing blind
/// indexes row-by-row. No plaintext temp export at any point — the
/// only plaintext artifacts are the source itself and the protected
/// `.cpbak` custody copy the operator retains until approved disposal.
///
/// Resumable: the destination carries `cpconvert_state`; a killed run
/// re-continues per table, never rewrites a committed table. The tool
/// never swaps files — the runbook owns the atomic switch.
public sealed class CpConverter(IFieldCipher cipher, IBlindIndex index)
{
    private const string StateTable = "cpconvert_state";

    /// (table, encrypted columns, idx column → source column + scope)
    private static readonly (
        string Table,
        string[] EncCols,
        (string IdxCol, string SrcCol, bool Tenant)[] Idxs,
        bool TenantScoped)[] Classified =
    [
        ("tenants", ["slug", "display_name"],
            [("slug_idx", "slug", false)], false),
        ("users", ["email", "display_name", "org_name"],
            [("email_idx", "email", true)], true),
        ("groups", ["name", "description"],
            [("name_idx", "name", true)], true),
        ("invites", ["email", "org_name"],
            [("email_idx", "email", true)], true),
        ("openiddict_applications", ["client_id", "properties"],
            [("client_id_idx", "client_id", false)], false),
        ("openiddict_authorizations", ["properties"], [], false),
        ("openiddict_tokens", ["properties"], [], false),
    ];

    public sealed record Result(
        IReadOnlyDictionary<string, long> RowCounts,
        bool AuditChainOk, int AuditEvents,
        bool RoundTripOk, bool ForeignKeysOk, IReadOnlyList<string> Resumed);

    public async Task<Result> ConvertAsync(
        string sourcePath, string destPath, string destKeyHex, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source control.db not found.", sourcePath);

        // 1. checkpoint + protected custody copy of the source.
        using (var srcCk = OpenPlain(sourcePath))
            await srcCk.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        File.Copy(sourcePath, sourcePath + ".cpbak", overwrite: true);

        // 2. keyed destination schema (idempotent — resume keeps it).
        var freshDest = !File.Exists(destPath);
        ControlPlaneMigrator.Migrate(
            $"Data Source={destPath}", destPath, destKeyHex);

        using var src = OpenPlain(sourcePath);
        using var dest = (SqliteConnection)ControlKeyedConnectionManager
            .OpenKeyed(destPath, destKeyHex);

        await dest.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {StateTable} " +
            "(table_name TEXT PRIMARY KEY, done INTEGER NOT NULL, rows INTEGER NOT NULL)");
        // Copy order can't respect FK topology; integrity is proven by
        // pragma_foreign_key_check in ValidateAsync after streaming.
        await dest.ExecuteAsync("PRAGMA foreign_keys = OFF");

        var done = (await dest.QueryAsync<string>(
            $"SELECT table_name FROM {StateTable} WHERE done = 1")).ToHashSet();
        var resumed = done.ToList();

        var tables = (await src.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type='table' " +
                "AND name NOT LIKE 'sqlite_%' AND name <> @state " +
                "AND LOWER(name) <> 'schemaversions' ORDER BY name",
                new { state = StateTable }))
            .ToList();

        var classified = Classified.ToDictionary(t => t.Table, StringComparer.Ordinal);

        foreach (var table in tables)
        {
            if (done.Contains(table)) continue;
            var dstCols = (await dest.QueryAsync<string>(
                    $"SELECT name FROM pragma_table_info(@t)", new { t = table }))
                .ToList();
            var srcCols = (await src.QueryAsync<string>(
                    $"SELECT name FROM pragma_table_info(@t)", new { t = table }))
                .ToHashSet(StringComparer.Ordinal);

            var writes = dstCols.Where(c =>
                srcCols.Contains(c) ||
                (classified.TryGetValue(table, out var spec) &&
                 spec.Idxs.Any(i => i.IdxCol == c))).ToList();

            var rows = await src.QueryAsync(
                $"SELECT * FROM \"{table}\"");

            long n = 0;
            using var tx = dest.BeginTransaction();
            // Fresh-migrated dest may carry schema-seed rows (e.g.
            // role_visibility) — the source is authoritative; wipe and
            // replay so a source-only divergence still fails counts.
            await dest.ExecuteAsync($"DELETE FROM \"{table}\"", transaction: tx);
            foreach (IDictionary<string, object?> row in rows)
            {
                var values = new Dictionary<string, object?>();
                foreach (var col in writes)
                    if (srcCols.Contains(col))
                        values[col] = row.TryGetValue(col, out var v) ? v : null;

                if (classified.TryGetValue(table, out var spec))
                {
                    var tenant = spec.TenantScoped
                        ? row.TryGetValue("tenant_id", out var tv)
                            ? tv?.ToString() ?? "" : ""
                        : "";
                    foreach (var col in spec.EncCols)
                    {
                        if (!values.TryGetValue(col, out var v)) continue;
                        var plain = v?.ToString();
                        values[col] = spec.TenantScoped
                            ? await CpCrypto.EncryptAsync(cipher, tenant, plain, ct)
                            : await CpCrypto.EncryptGlobalAsync(cipher, plain, ct);
                    }
                    foreach (var (idxCol, srcCol, _) in spec.Idxs)
                    {
                        var plain = row.TryGetValue(srcCol, out var v)
                            ? v?.ToString() : null;
                        if (plain is null) { values[idxCol] = null; continue; }
                        var cls = srcCol is "email" ? PiiClass.Contact : PiiClass.Other;
                        values[idxCol] = spec.TenantScoped
                            ? await CpCrypto.TenantIndexAsync(index, tenant, cls, plain, ct)
                            : await CpCrypto.GlobalIndexAsync(index, cls, plain, ct);
                    }
                }

                var cols = string.Join(',', writes.Select(c => $"\"{c}\""));
                var pars = string.Join(',', writes.Select(c => "@" + c));
                await dest.ExecuteAsync(
                    $"INSERT INTO \"{table}\" ({cols}) VALUES ({pars})",
                    values, tx);
                n++;
            }
            await dest.ExecuteAsync(
                $"INSERT INTO {StateTable} (table_name, done, rows) " +
                "VALUES (@t, 1, @n) ON CONFLICT(table_name) " +
                "DO UPDATE SET done=1, rows=@n",
                new { t = table, n }, tx);
            tx.Commit();
        }

        return await ValidateAsync(src, dest, tables, resumed, ct);
    }

    private async Task<Result> ValidateAsync(
        SqliteConnection src, SqliteConnection dest,
        List<string> tables, List<string> resumed, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>();
        foreach (var t in tables)
        {
            var s = await src.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM \"{t}\"");
            var d = await dest.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM \"{t}\"");
            if (s != d)
                throw new InvalidOperationException(
                    $"cpconvert validation failed: {t} source {s} != dest {d}");
            counts[t] = d;
        }

        var fkBad = await dest.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_foreign_key_check");
        if (fkBad != 0)
            throw new InvalidOperationException(
                $"cpconvert validation failed: {fkBad} foreign-key violations");

        // Audit rows copy verbatim — re-verify the untouched chain.
        var auditRows = (await dest.QueryAsync(
                "SELECT seq, event_id, actor, action, entity, entity_id, " +
                "field_classes, reason, prev_hash, hash, created_utc " +
                "FROM audit_events ORDER BY seq"))
            .Select(r => new AuditEvent(
                (long)r.seq, (string)r.event_id, (string)r.actor,
                (string)r.action, (string)r.entity,
                (string?)r.entity_id,
                JsonSerializer.Deserialize<List<string>>(
                    (string)r.field_classes) ?? [],
                (string?)r.reason, (string)r.prev_hash,
                (string)r.hash, (string)r.created_utc))
            .ToList();
        var broken = HashChain.FirstBroken(auditRows);
        if (broken is not null)
            throw new InvalidOperationException(
                $"cpconvert validation failed: audit chain broken at seq {broken}");

        // Round-trip proof: decrypt every encrypted cell back to the
        // source plaintext — any mismatch fails the conversion.
        foreach (var (table, encCols, _, tenantScoped) in Classified)
        {
            if (!tables.Contains(table)) continue;
            var srcRows = await src.QueryAsync(
                $"SELECT * FROM \"{table}\"");
            var dstRows = (await dest.QueryAsync(
                $"SELECT * FROM \"{table}\"")).ToList();
            var i = 0;
            foreach (IDictionary<string, object?> srow in srcRows)
            {
                var drow = (IDictionary<string, object?>)dstRows[i++];
                var tenant = tenantScoped
                    ? srow.TryGetValue("tenant_id", out var tv)
                        ? tv?.ToString() ?? "" : ""
                    : "";
                foreach (var col in encCols)
                {
                    if (!srow.TryGetValue(col, out var sv)) continue;
                    if (sv is null or DBNull) continue;
                    var cv = drow.TryGetValue(col, out var dv)
                        ? dv?.ToString() : null;
                    var back = tenantScoped
                        ? await CpCrypto.DecryptTenantAsync(cipher, tenant, cv, ct)
                        : await CpCrypto.DecryptGlobalAsync(cipher, cv, ct);
                    if (back != sv.ToString())
                        throw new InvalidOperationException(
                            $"cpconvert validation failed: {table}.{col} " +
                            "does not decrypt back to the source value");
                }
            }
        }

        return new Result(counts, true, auditRows.Count, true, true, resumed);
    }

    private static SqliteConnection OpenPlain(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }
}
