using System.Text.Json;
using Dapper;
using Lola.Application.Pricing;

namespace Lola.DataPlane;

/// Tenant-file rate-lock store (MPE-131): append-only `rate_locks`
/// rows — insert on lock, status flip on unlock, never a delete.
public sealed class RateLockRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IRateLockRepository
{
    private const string Cols = """
        lock_id AS LockId, application_id AS ApplicationId,
        eval_id AS EvalId, product_code AS ProductCode,
        rate AS Rate, price AS Price, lock_term_days AS LockTermDays,
        investor_name AS InvestorName, locked_utc AS LockedUtc,
        expires_utc AS ExpiresUtc, actor AS Actor, status AS Status,
        frozen_paths AS FrozenPathsJson, unlock_reason AS UnlockReason,
        unlocked_utc AS UnlockedUtc, unlocked_by AS UnlockedBy
        """;

    private sealed class Row
    {
        public string LockId { get; set; } = "";
        public string ApplicationId { get; set; } = "";
        public string EvalId { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public decimal Rate { get; set; }
        public decimal? Price { get; set; }
        public long LockTermDays { get; set; }
        public string? InvestorName { get; set; }
        public string LockedUtc { get; set; } = "";
        public string ExpiresUtc { get; set; } = "";
        public string Actor { get; set; } = "";
        public string Status { get; set; } = "";
        public string FrozenPathsJson { get; set; } = "[]";
        public string? UnlockReason { get; set; }
        public string? UnlockedUtc { get; set; }
        public string? UnlockedBy { get; set; }

        public RateLockRecord ToRecord() => new(
            LockId, ApplicationId, EvalId, ProductCode, Rate, Price,
            (int)LockTermDays, InvestorName, LockedUtc, ExpiresUtc,
            Actor, Status,
            JsonSerializer.Deserialize<IReadOnlyList<string>>(
                FrozenPathsJson) ?? [],
            UnlockReason, UnlockedUtc, UnlockedBy);
    }

    public async Task InsertAsync(RateLockRecord r,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            INSERT INTO rate_locks
                (lock_id, application_id, eval_id, product_code, rate,
                 price, lock_term_days, investor_name, locked_utc,
                 expires_utc, actor, status, frozen_paths)
            VALUES (@lockId, @appId, @evalId, @code, @rate, @price,
                    @term, @investor, @locked, @expires, @actor,
                    'active', @frozen)
            """,
            new
            {
                lockId = r.LockId,
                appId = r.ApplicationId,
                evalId = r.EvalId,
                code = r.ProductCode,
                rate = r.Rate,
                price = r.Price,
                term = r.LockTermDays,
                investor = r.InvestorName,
                locked = r.LockedUtc,
                expires = r.ExpiresUtc,
                actor = r.Actor,
                frozen = JsonSerializer.Serialize(r.FrozenPaths)
            }, ct);

    public async Task<RateLockRecord?> GetActiveAsync(string appId,
        CancellationToken ct = default)
    {
        var row = await QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Cols} FROM rate_locks WHERE application_id=@appId AND status='active'",
            new { appId }, ct);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<RateLockRecord>> ListAsync(
        string appId, CancellationToken ct = default)
        => (await QueryAsync<Row>(
            $"SELECT {Cols} FROM rate_locks WHERE application_id=@appId ORDER BY locked_utc",
            new { appId }, ct)).Select(r => r.ToRecord()).ToList();

    public async Task UnlockAsync(string lockId, string reason,
        string unlockedBy, string unlockedUtc,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE rate_locks
            SET status='unlocked', unlock_reason=@reason,
                unlocked_by=@by, unlocked_utc=@utc
            WHERE lock_id=@lockId AND status='active'
            """,
            new { lockId, reason, by = unlockedBy, utc = unlockedUtc },
            ct);
}
