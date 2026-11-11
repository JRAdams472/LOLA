using Lola.Application.Audit;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Vault;

/// Registry-scoped vault-scan drain (MPE-201) — one pass over every
/// active tenant file, executing due `vault_scan_outbox` rows against
/// the configured object vault + scanner. Per row: claim the outbox
/// row → GET the quarantined ciphertext → open-stream decrypt →
/// zINSTREAM → verdict:
///   clean       → CopyObject promote to clean/, row `clean`,
///                 quarantined source deleted, `vault_scan_clean`
///   infected    → row `infected` + signature, quarantined object
///                 deleted, `vault_scan_infected`
///   unavailable → backoff reschedule → MaxAttempts → `unavailable`
///                 DLQ, `vault_scan_unavailable`
/// Plaintext exists only inside the decrypt→INSTREAM stream — never
/// buffered to disk or durable storage (invariant 1). Attempts count
/// executions like the doc outbox so a stalled scan can't hold a
/// row's claim forever without consuming budget.
public sealed class VaultScanProcessor(
    IServiceProvider services, ITenantRegistry registry,
    TimeProvider clock)
{
    private const int BatchSize = 25;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan ClaimWindow =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BaseBackoff =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff =
        TimeSpan.FromMinutes(5);

    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var drained = 0;
        foreach (var tenant in await registry.ListAsync(ct))
        {
            if (tenant.Status != "active" || tenant.DbKeyWrapped is null)
                continue;
            drained += await DrainTenantAsync(tenant, ct);
        }
        return drained;
    }

    public async Task<int> DrainTenantAsync(TenantRecord tenant,
        CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IVaultDocRepository>();
        var store = scope.ServiceProvider
            .GetRequiredService<IObjectVault>();
        var scanner = scope.ServiceProvider
            .GetRequiredService<IVirusScanner>();
        var cipher = scope.ServiceProvider
            .GetRequiredService<VaultCipher>();
        var audit = scope.ServiceProvider
            .GetRequiredService<ITenantAuditLog>();
        var processed = 0;
        using (TenantContext.Use(TenantScope.From(tenant)))
        using (CipherContext.Use(tenant.Id))
        {
            var rows = await repo.ListDueScansAsync(Now(), BatchSize, ct);
            ct.ThrowIfCancellationRequested();
            await Task.WhenAll(rows.Select(row =>
                ProcessRowAsync(repo, store, scanner, cipher, audit,
                    row, ct)));
            processed += rows.Count;
        }
        return processed;
    }

    private async Task ProcessRowAsync(IVaultDocRepository repo,
        IObjectVault store, IVirusScanner scanner, VaultCipher cipher,
        ITenantAuditLog audit, VaultScanOutboxRow row,
        CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        var doc = await repo.GetAsync(row.VaultDocId, ct);
        if (doc is null)
        {
            // Outbox row without its doc — nothing to scan; DLQ the
            // orphan rather than retrying it forever.
            await repo.MarkUnavailableAsync(row.OutboxId,
                row.VaultDocId, "vault doc row missing", ct);
            return;
        }
        if (attempts > MaxAttempts)
        {
            await repo.MarkUnavailableAsync(row.OutboxId,
                row.VaultDocId, "scan attempts exhausted", ct);
            await AuditAsync(audit, doc, "vault_scan_unavailable",
                "scan attempts exhausted", ct);
            return;
        }

        try
        {
            // Re-claim under the poll window before the slow part —
            // a stalled scan must not leave the row looking due.
            await repo.MarkScanningAsync(
                row.OutboxId, NowPlus(ClaimWindow), ct);

            await using var ciphertext =
                await store.GetAsync(doc.S3Key, ct);
            await using var plaintext =
                await cipher.OpenAsync(doc.WrappedDek, ciphertext, ct);
            var verdict = await scanner.ScanAsync(plaintext, ct);

            switch (verdict.Status)
            {
                case ScanStatus.Clean:
                    {
                        var cleanKey = $"clean/{doc.VaultDocId}";
                        await store.CopyAsync(doc.S3Key, cleanKey, ct);
                        await repo.CompleteScanAsync(row.OutboxId,
                            doc.VaultDocId, cleanKey, Now(), ct);
                        // Promote, don't duplicate — a lingering quarantine
                        // copy is wasted ciphertext, not a leak, but the
                        // registry points only at clean/.
                        try
                        {
                            await store.DeleteAsync(doc.S3Key, ct);
                        }
                        catch { /* best effort — ciphertext only */ }
                        await AuditAsync(audit, doc, "vault_scan_clean",
                            $"scan clean: {doc.VaultDocId}", ct);
                        break;
                    }
                case ScanStatus.Infected:
                    {
                        await repo.MarkInfectedAsync(row.OutboxId,
                            doc.VaultDocId,
                            verdict.Signature ?? "malware detected",
                            Now(), ct);
                        // The infected ciphertext is destroyed — nothing
                        // in clean/ ever carried it.
                        try
                        {
                            await store.DeleteAsync(doc.S3Key, ct);
                        }
                        catch { /* best effort — ciphertext only */ }
                        await AuditAsync(audit, doc, "vault_scan_infected",
                            $"scan infected: {doc.VaultDocId}", ct);
                        break;
                    }
                default:
                    {
                        if (attempts >= MaxAttempts)
                        {
                            await repo.MarkUnavailableAsync(row.OutboxId,
                                doc.VaultDocId,
                                verdict.Signature ?? "scanner unavailable",
                                ct);
                            await AuditAsync(audit, doc,
                                "vault_scan_unavailable",
                                verdict.Signature ?? "scanner unavailable",
                                ct);
                        }
                        else
                        {
                            await repo.RescheduleScanAsync(row.OutboxId,
                                attempts, NowPlus(Backoff(attempts)),
                                verdict.Signature ?? "scanner unavailable",
                                ct);
                        }
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempts >= MaxAttempts)
            {
                await repo.MarkUnavailableAsync(row.OutboxId,
                    doc.VaultDocId, $"unexpected: {ex.Message}", ct);
                await AuditAsync(audit, doc, "vault_scan_unavailable",
                    $"unexpected: {ex.Message}", ct);
            }
            else
            {
                await repo.RescheduleScanAsync(row.OutboxId, attempts,
                    NowPlus(Backoff(attempts)),
                    $"unexpected: {ex.Message}", ct);
            }
        }
    }

    private static Task AuditAsync(ITenantAuditLog audit,
        VaultDocRecord doc, string action, string detail,
        CancellationToken ct)
        // The scan is system-driven; attribute to the uploader so the
        // access-review spine ties the verdict to their document.
        // Signature/filename never enter the detail — the doc id is
        // enough to attribute (invariant 3).
        => audit.WriteAsync(doc.Actor, action, "application",
            doc.ApplicationId, ["vault_docs"], detail, ct);

    private static TimeSpan Backoff(long attempts)
    {
        var scaled = TimeSpan.FromTicks(
            BaseBackoff.Ticks * (1L << (int)Math.Min(attempts - 1, 10)));
        return scaled > MaxBackoff ? MaxBackoff : scaled;
    }

    private string Now() => clock.GetUtcNow()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private string NowPlus(TimeSpan delta) => clock.GetUtcNow()
        .Add(delta).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
