using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Lola.DataPlane;

/// Encrypted tenant-file backup/restore (MPE-95). Backup runs
/// `VACUUM INTO` into backups/<tenant>/<utc>.db — SQLCipher writes the
/// destination through the same codec, so snapshots stay encrypted
/// under the tenant's own file key (the wrong-key acceptance test
/// proves it). Each snapshot gets a <name>.manifest.json sidecar
/// (tenant, utc, sha256, bytes); restore verifies the manifest AND a
/// keyed integrity_check before checkpointing and swapping the live
/// file. Job surface — explicit TenantRecord, no TenantContext.
public sealed class TenantBackupService(
    IFieldCipher cipher,
    string backupDir = "data/backups") : ITenantBackup
{
    static TenantBackupService() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<TenantBackupInfo> BackupAsync(
        TenantRecord tenant, CancellationToken ct = default)
    {
        var keyHex = await UnwrapKeyHexAsync(tenant, ct);
        var dir = Path.Combine(backupDir, tenant.Id);
        Directory.CreateDirectory(dir);

        string dest;
        string name;
        do
        {
            name = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}.db";
            dest = Path.Combine(dir, name);
        } while (File.Exists(dest));

        using (var conn = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
        {
            // VACUUM INTO takes a literal filename — the path is fully
            // server-derived (backupDir + tenant id + generated name);
            // quote-escaped regardless.
            await conn.ExecuteAsync($"VACUUM INTO '{dest.Replace("'", "''")}'");
        }

        // The snapshot inherits WAL mode and VACUUM INTO leaves copied
        // pages in the -wal — and every later keyed open+close would
        // checkpoint them and re-encrypt pages under fresh IVs, so the
        // .db bytes are never stable. A backup should be one immutable
        // artifact anyway: DELETE-mode folds the wal in and makes the
        // file byte-stable BEFORE the manifest hashes it.
        using (var snap = KeyedSqliteConnectionManager.OpenKeyed(dest, keyHex))
            await snap.ExecuteAsync("PRAGMA journal_mode=DELETE");
        File.Delete(dest + "-wal");
        File.Delete(dest + "-shm");

        return await WriteManifestAsync(tenant.Id, dir, name, ct);
    }

    public async Task<IReadOnlyList<TenantBackupInfo>> ListAsync(
        string tenantId, CancellationToken ct = default)
    {
        var dir = Path.Combine(backupDir, tenantId);
        if (!Directory.Exists(dir)) return [];

        var infos = new List<TenantBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.manifest.json"))
        {
            await using var stream = File.OpenRead(path);
            var info = await JsonSerializer.DeserializeAsync<TenantBackupInfo>(
                stream, JsonOpts, ct);
            if (info is not null) infos.Add(info);
        }
        return infos.OrderByDescending(i => i.Utc).ToList();
    }

    public async Task<TenantBackupInfo> RestoreAsync(
        TenantRecord tenant, string name, CancellationToken ct = default)
    {
        var dir = Path.Combine(backupDir, tenant.Id);
        var backupPath = ResolveBackupPath(dir, name);
        var info = await ReadManifestAsync(dir, name, ct)
            ?? throw new FileNotFoundException($"backup '{name}' not found");
        var keyHex = await UnwrapKeyHexAsync(tenant, ct);

        // Manifest hash on the pristine bytes first, then a READ-ONLY
        // keyed integrity probe — an ordinary open would re-encrypt
        // pages (journal_mode pragma / close-checkpoint) and break the
        // manifest for every later restore. A wrong key still dies here
        // as "file is not a database" before any bytes move.
        var actual = await Sha256Async(backupPath, ct);
        if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"backup '{name}' failed manifest sha256 verification");
        using (var probe = OpenReadonlyKeyed(backupPath, keyHex))
        {
            var check = await probe.ExecuteScalarAsync<string>("PRAGMA integrity_check");
            if (check != "ok")
                throw new InvalidOperationException(
                    $"backup '{name}' failed integrity_check: {check}");
        }

        // Checkpoint the live file so no WAL frames outlive the swap —
        // a busy checkpoint means another connection holds a snapshot
        // and stale frames could replay into the new file; abort with
        // an honest error rather than corrupting the restore.
        using (var live = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
        {
            var busy = await live.ExecuteScalarAsync<int>(
                "PRAGMA wal_checkpoint(TRUNCATE)");
            if (busy != 0)
                throw new InvalidOperationException(
                    "tenant file is busy — restore aborted before the " +
                    "swap; retry in a write-quiet window");
        }
        TryDelete(tenant.DbPath + "-wal");
        TryDelete(tenant.DbPath + "-shm");

        File.Copy(backupPath, tenant.DbPath, overwrite: true);
        using (var verify = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
        {
            var check = await verify.ExecuteScalarAsync<string>("PRAGMA integrity_check");
            if (check != "ok")
                throw new InvalidOperationException(
                    $"restored tenant file failed integrity_check: {check}");
        }
        return info;
    }

    /// A drained -wal/-shm is cosmetic debris — deletion is best-effort
    /// (a still-open connection holds the file on Windows) since the
    /// TRUNCATE checkpoint already proved it empty.
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }

    /// Read-only keyed probe — verifies a snapshot without mutating it:
    /// ReadOnly mode + PRAGMA key only, so no journal pragmas, no -wal,
    /// no close-checkpoint. The file stays byte-identical to manifest.
    private static SqliteConnection OpenReadonlyKeyed(
        string dbPath, string fileKeyHex)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();
        conn.Execute($"PRAGMA key = \"x'{fileKeyHex}'\"");
        return conn;
    }

    /// Backup names are a flat filename inside backups/<tenant>/ —
    /// anything else (separators, traversal, non-.db) is rejected.
    private static string ResolveBackupPath(string dir, string name)
    {
        if (name != Path.GetFileName(name)
            || !name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"invalid backup name '{name}'", nameof(name));
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, name));
        return full.StartsWith(root, StringComparison.Ordinal)
            ? full
            : throw new ArgumentException($"invalid backup name '{name}'", nameof(name));
    }

    private async Task<TenantBackupInfo> WriteManifestAsync(
        string tenantId, string dir, string name, CancellationToken ct)
    {
        var path = Path.Combine(dir, name);
        var info = new TenantBackupInfo(tenantId, name,
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture),
            await Sha256Async(path, ct), new FileInfo(path).Length);
        await File.WriteAllTextAsync(
            path + ".manifest.json", JsonSerializer.Serialize(info, JsonOpts), ct);
        return info;
    }

    private static async Task<TenantBackupInfo?> ReadManifestAsync(
        string dir, string name, CancellationToken ct)
    {
        var path = Path.Combine(dir, name + ".manifest.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TenantBackupInfo>(
            stream, JsonOpts, ct);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private async Task<string> UnwrapKeyHexAsync(TenantRecord tenant, CancellationToken ct)
    {
        var wrapped = tenant.DbKeyWrapped ?? throw new InvalidOperationException(
            $"Tenant '{tenant.Id}' has no data-plane key — provisioning is incomplete.");
        using (CipherContext.Use(tenant.Id))
        {
            var b64 = await cipher.DecryptAsync(wrapped, ct);
            return Convert.ToHexString(Convert.FromBase64String(b64));
        }
    }
}
