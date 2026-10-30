namespace Lola.Application.Pricing;

/// One persisted rate lock (tenant-file `rate_locks`, MPE-131).
/// Rows are append-only — re-locking writes a NEW row so history
/// survives; `Status` is active|unlocked.
public sealed record RateLockRecord(
    string LockId, string ApplicationId, string EvalId,
    string ProductCode, decimal Rate, decimal? Price,
    int LockTermDays, string? InvestorName,
    string LockedUtc, string ExpiresUtc, string Actor,
    string Status, IReadOnlyList<string> FrozenPaths,
    string? UnlockReason, string? UnlockedUtc, string? UnlockedBy);

/// Wire-facing lock DTO — rides `ApplicationDto.Lock` and the
/// `GET …/lock` endpoint.
public sealed record RateLockDto(
    string LockId, string EvalId, string ProductCode, decimal Rate,
    decimal? Price, int LockTermDays, string? InvestorName,
    string LockedUtc, string ExpiresUtc, string Status,
    string? UnlockReason, string? UnlockedUtc);

/// Rate-lock persistence seam (MPE-131). The DataPlane impl keeps
/// insert/unlock atomic inside the tenant file.
public interface IRateLockRepository
{
    Task InsertAsync(RateLockRecord row, CancellationToken ct = default);
    Task<RateLockRecord?> GetActiveAsync(string appId,
        CancellationToken ct = default);
    Task<IReadOnlyList<RateLockRecord>> ListAsync(string appId,
        CancellationToken ct = default);
    Task UnlockAsync(string lockId, string reason, string unlockedBy,
        string unlockedUtc, CancellationToken ct = default);
}

public static class RateLockDtos
{
    public static RateLockDto ToDto(RateLockRecord r) => new(
        r.LockId, r.EvalId, r.ProductCode, r.Rate, r.Price,
        r.LockTermDays, r.InvestorName, r.LockedUtc, r.ExpiresUtc,
        r.Status, r.UnlockReason, r.UnlockedUtc);
}
