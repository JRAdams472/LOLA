using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Fees;

/// PATCH …/applications/{appId}/fees/{itemId} (MPE-184) — the
/// override toggle. `Amount` set → upsert the app-level override
/// keyed by (feeCode, section); `Amount` null → clear and restore
/// the computed value. `Reason` is required either way — every
/// toggle writes `fee_override` / `fee_override_clear` on the audit
/// spine. The id may name a fee_items row OR a fee_overrides row
/// (a stale override's line is still editable — clear is the only
/// sensible action there).
public sealed record OverrideFeeCommand(
    string TenantId, string AppId, string ItemId, string ActorId,
    bool ActorIsBorrower, decimal? Amount, string? Reason);

public sealed record FeeOverrideResult(
    string OverrideId, decimal? Amount, bool Cleared);

public static class OverrideFeeHandler
{
    public static async Task<FeeOverrideResult> Handle(
        OverrideFeeCommand c, IApplicationRepository apps,
        IFeeRepository fees, ITenantAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden(
                "fee overrides are a staff action");
        if (string.IsNullOrWhiteSpace(c.Reason))
            throw DomainException.BadRequest(
                "reason is required for a fee override");

        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);

        // Resolve the id against items first, then overrides — a
        // stale override's line id stays addressable for clear.
        var item = await fees.GetItemAsync(c.ItemId, ct);
        FeeOverrideRecord? existing = null;
        string feeCode, section, variant;
        if (item is not null)
        {
            if (item.ApplicationId != c.AppId)
                throw DomainException.NotFound("fee line not found");
            (feeCode, section, variant) =
                (item.Item.FeeCode, item.Item.Section, item.Variant);
            existing = (await fees.ListOverridesAsync(c.AppId, ct))
                .FirstOrDefault(o => o.FeeCode == feeCode &&
                    o.Section == section && o.Variant == variant &&
                    o.ClearedUtc is null);
        }
        else
        {
            existing = await fees.GetOverrideAsync(c.ItemId, ct);
            if (existing is null || existing.ApplicationId != c.AppId ||
                existing.ClearedUtc is not null)
                throw DomainException.NotFound("fee line not found");
            (feeCode, section, variant) =
                (existing.FeeCode, existing.Section, existing.Variant);
        }

        var utc = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        if (c.Amount is null)
        {
            if (existing is null)
                throw DomainException.NotFound(
                    "no override on this fee line");
            await fees.ClearOverrideAsync(
                existing.OverrideId, utc, ct);
            await audit.WriteAsync(c.ActorId, "fee_override_clear",
                "application", c.AppId, AuditFields.InDoc(app.Doc),
                $"cleared fee override on {feeCode}: {c.Reason}", ct);
            return new FeeOverrideResult(existing.OverrideId, null, true);
        }

        var record = await fees.UpsertOverrideAsync(
            new FeeOverrideRecord(
                existing?.OverrideId ?? $"ovr-{Guid.NewGuid():N}",
                c.AppId, feeCode, section, (double)c.Amount.Value, c.Reason!,
                c.ActorId, utc, 0, null, variant), ct);
        await audit.WriteAsync(c.ActorId, "fee_override",
            "application", c.AppId, AuditFields.InDoc(app.Doc),
            $"overrode {feeCode} → {c.Amount}: {c.Reason}", ct);
        return new FeeOverrideResult(record.OverrideId, c.Amount, false);
    }
}
