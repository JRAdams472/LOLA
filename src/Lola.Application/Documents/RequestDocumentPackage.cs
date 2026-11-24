using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Application.Pricing;

namespace Lola.Application.Documents;

/// POST …/applications/{id}/packages (MPE-190) — assembles the
/// manifest-bounded disclosure payload and enqueues doc+outbox rows;
/// the vendor call itself is deferred to the doc outbox worker —
/// the endpoint never blocks on ISPPr. `le-package` requires an
/// active rate lock and a completed fee eval (the LE prices from
/// them); `adverse-action` needs neither but takes allowlisted
/// reason codes. Idempotent: a repeated request for the same
/// app+kind+payload returns the original doc id.
public sealed record RequestDocumentPackageCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope,
    string Kind, IReadOnlyList<string>? ReasonCodes = null);

public sealed record DocAccepted(string DocId, bool Deduplicated);

public static class RequestDocumentPackageHandler
{
    public static async Task<DocAccepted> Handle(
        RequestDocumentPackageCommand c, IApplicationRepository apps,
        IDocumentRepository docs, IDocumentService adapter,
        IRateLockRepository locks, IFeeRepository fees,
        ITenantRegistry registry, ITenantAuditLog audit,
        Compliance.IToleranceRepository tolerance,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "document generation is a staff action");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        // The vendor customer comes from the tenant's provisioning
        // binding — a tenant with no ISPPr link can't generate.
        var tenant = await registry.FindByIdAsync(c.TenantId, ct)
            ?? throw Errors.DomainException.NotFound(
                "tenant not provisioned");
        var customerId = tenant.IspprCustomerId
            ?? throw Errors.DomainException.Conflict(
                "tenant has no document-vendor binding");

        // le-package inputs: active lock + latest done fee eval —
        // the payload builder re-checks both and 409s as well.
        var lock_ = c.Kind == DisclosurePayloadBuilder.LePackage
            ? await locks.GetActiveAsync(c.AppId, ct) : null;
        FeeBreakdownDto? breakdown = null;
        string? evalId = null;
        if (c.Kind == DisclosurePayloadBuilder.LePackage)
        {
            var evals = await fees.GetLatestBatchEvalsAsync(c.AppId, ct);
            var loanType = app.Doc.Get("loan.product_type")
                ?.GetValue<string>();
            var eval = evals.FirstOrDefault(e =>
                    e.Status == "done" && e.Variant == loanType)
                ?? evals.FirstOrDefault(e => e.Status == "done");
            if (eval is not null)
            {
                evalId = eval.EvalId;
                var items = await fees.ListItemsAsync(eval.EvalId, ct);
                var overrides = await fees.ListOverridesAsync(
                    c.AppId, ct);
                breakdown = FeeAggregator.ToBreakdown(
                    eval, items, overrides);
            }
        }

        var documents = DisclosurePayloadBuilder.Build(
            c.Kind, c.AppId, app.Doc, lock_, breakdown, c.ReasonCodes);

        var docId = $"doc-{Guid.NewGuid():N}";
        var request = new DocPackageRequest(customerId,
            $"{c.Kind}-{c.AppId}", docId, documents);
        var payloadJson = DocWire.PayloadJson(request);
        // The hash must ignore the per-call doc id — two identical
        // package requests dedupe to the original row.
        var hash = DocWire.RequestHash(c.AppId, c.Kind,
            DocWire.PayloadJson(request with { IdempotencyKey = "" }));
        var doc = new DocRecord(docId, c.AppId, c.Kind,
            request.PackageName, adapter.Adapter, null, null,
            "pending", null, c.ActorId, null, null, hash);
        var result = await docs.EnqueueAsync(
            doc, $"ob-{Guid.NewGuid():N}", "generate", payloadJson, ct);
        if (!result.Deduplicated)
        {
            // TRID tolerance (M21): bind this issuance to the eval it
            // rendered. A pending changed circumstance makes this
            // version the rebased baseline for its fees — the repo
            // stamps both facts in one transaction.
            var disclosureKind = Compliance.DisclosureKind.Of(c.Kind);
            if (disclosureKind is not null && evalId is not null)
            {
                var pendingBaseline = (await tolerance.ListVersionsAsync(
                        c.AppId, disclosureKind, ct))
                    .OrderByDescending(v => v.Seq).FirstOrDefault();
                var resets = await tolerance.ListResetsAsync(
                    c.AppId, ct);
                var resetOf = resets.Any(r =>
                        r.RebasedVersionId is null)
                    ? pendingBaseline?.VersionId : null;
                await tolerance.AddVersionAsync(c.AppId,
                    disclosureKind, evalId,
                    result.DocId, resetOf, ct);
            }
            await audit.WriteAsync(c.ActorId, "doc_generate",
                "application", c.AppId,
                AuditFields.InDoc(app.Doc),
                $"{c.Kind} generation requested", ct);
        }
        return new DocAccepted(result.DocId, result.Deduplicated);
    }
}
