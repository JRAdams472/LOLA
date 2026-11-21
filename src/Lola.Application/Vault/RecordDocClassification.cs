using System.Text.Json;
using System.Text.RegularExpressions;
using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Tasks;

namespace Lola.Application.Vault;

/// Persist one classification result onto the vault doc row (M18 P2)
/// — the scan drain invokes this after a Clean verdict and before
/// promote. The whole DocClassification is PII-class in the aggregate
/// (extracted fields) so it lands `vault:`-encrypted via the repo;
/// the event + audit write carry kind/confidence/finding NAMES only
/// (invariant 3).
///
/// M18 P3: after persistence, findings map onto the conditions
/// domain — a clean doc whose detected kind matches closes open
/// `doc_required` requirements (linked `doc_kind`), and
/// `expired_doc`/`kind_mismatch`/`unreadable` open blocking
/// replace-doc conditions (linked `vault_doc`), `missing_field` a
/// non-blocking `data_validation`, employer-vs-property state
/// divergence a `distance_check`. Every transition is evented +
/// audited under `auto:classifier`, and a `TaskEvaluator` pass
/// converges the queue in the same handler call.
public sealed record RecordDocClassificationCommand(
    string TenantId, string VaultDocId, byte[] Head);

public sealed record DocClassifiedOutcome(
    string Kind, double Confidence, int FindingCount);

public static class RecordDocClassificationHandler
{
    private const string Actor = "auto:classifier";

    private static readonly JsonSerializerOptions JsonOpts = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// Findings that reject the doc — each opens a blocking
    /// replace-doc condition, and any of them suppress auto-close:
    /// a flagged doc never satisfies a requirement.
    private static readonly IReadOnlySet<string> BlockingFindings =
        new HashSet<string>
        {
            DocFindingKind.ExpiredDoc,
            DocFindingKind.KindMismatch,
            DocFindingKind.Unreadable
        };

    public static async Task<DocClassifiedOutcome> Handle(
        RecordDocClassificationCommand c, IDocClassifier classifier,
        IVaultDocRepository vault, IConditionRepository conditions,
        IApplicationRepository apps, ITenantAuditLog audit,
        ILoanEventLog events, TaskEvaluator eval,
        TimeProvider clock, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var doc = await vault.GetAsync(c.VaultDocId, ct)
            ?? throw Errors.DomainException.NotFound(
                $"vault doc {c.VaultDocId}");
        var result = await classifier.ClassifyAsync(
            doc, c.Head, ct);

        var utc = clock.GetUtcNow()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await vault.SaveClassificationAsync(c.VaultDocId,
            JsonSerializer.Serialize(result, JsonOpts), utc, ct);

        // Metadata only — kind, confidence and finding NAMES; the
        // extracted values never ride the spines (invariant 3).
        var findings = result.Findings.Count == 0
            ? "none"
            : string.Join(",",
                result.Findings.Select(f => f.Kind));
        // entity=application mirrors vault_scan_clean — the whole
        // quarantine→scan→classify arc stays on one app spine.
        await audit.WriteAsync(Actor, "doc_classified", "application",
            doc.ApplicationId, ["vault_docs"],
            $"doc={c.VaultDocId} kind={result.DocKind} " +
            $"confidence={result.Confidence:0.00} findings={findings}",
            ct);
        await events.WriteAsync(new LoanEventDraft(
            doc.ApplicationId, LoanEventKind.DocClassified, Actor,
            "vault_doc", c.VaultDocId,
            FieldPath: "vault_docs.classification",
            AfterVal: result.DocKind), ct);

        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, doc.ApplicationId, Actor, ActorScope.Staff, ct);
        await ApplyConditionsAsync(doc, result, app, conditions,
            vault, audit, events, AuditFields.InDoc(app.Doc), ct);
        // Queue convergence — closed conditions resolve tasks, new
        // blockers derive them, in the same pass (M16/M17 semantics).
        await eval.EvaluateApplicationAsync(c.TenantId,
            doc.ApplicationId, ct);
        return new DocClassifiedOutcome(result.DocKind,
            result.Confidence, result.Findings.Count);
    }

    private static async Task ApplyConditionsAsync(
        VaultDocRecord doc, DocClassification result,
        ApplicationRecord app, IConditionRepository conditions,
        IVaultDocRepository vault, ITenantAuditLog audit,
        ILoanEventLog events, IReadOnlyList<string> docClasses,
        CancellationToken ct)
    {
        var open = await conditions.ListByApplicationAsync(
            doc.ApplicationId, ConditionState.Open, ct);
        var rejected = result.Findings.Any(
            f => BlockingFindings.Contains(f.Kind));

        // Auto-close — a clean doc whose detected kind matches. The
        // `doc_kind` convention is the checklist requirement ("need a
        // w2"); a `vault_doc`-linked replace condition also closes
        // when the replacement detects as the flagged doc's declared
        // kind — otherwise a satisfied replacement deadlocks the
        // borrower-visible condition forever.
        if (!rejected)
        {
            foreach (var cond in open.Where(cond =>
                cond.Kind == ConditionKind.DocRequired
                && (cond.LinkedEntity == ConditionLink.DocKind
                    && DocKind.Normalize(cond.LinkedEntityId) == result.DocKind
                 || cond.LinkedEntity == ConditionLink.VaultDoc
                    && cond.LinkedEntityId != doc.VaultDocId)))
            {
                if (cond.LinkedEntity == ConditionLink.VaultDoc)
                {
                    var flagged = await vault.GetAsync(
                        cond.LinkedEntityId!, ct);
                    if (DocKind.Normalize(flagged?.Classification)
                            != result.DocKind)
                        continue;
                }
                var next = await conditions.TransitionAsync(
                    cond.ConditionId, ConditionState.Closed,
                    $"satisfied by {doc.VaultDocId}", ct);
                await audit.WriteAsync(Actor, "condition_closed",
                    "condition", cond.ConditionId, docClasses,
                    $"classified {result.DocKind} satisfied by {doc.VaultDocId}",
                    ct);
                await events.WriteAsync(new LoanEventDraft(
                    doc.ApplicationId, LoanEventKind.ConditionClosed,
                    Actor, "condition", cond.ConditionId,
                    FieldPath: $"conditions.{next.Kind}",
                    BeforeVal: ConditionState.Open,
                    AfterVal: next.State), ct);
            }
        }

        // Finding → condition — deduped against open rows by
        // (kind, linked doc, title) so reclassifies and replays
        // converge instead of accumulating.
        foreach (var kind in result.Findings
            .Select(f => f.Kind).Distinct())
        {
            var blocking = BlockingFindings.Contains(kind);
            var condKind = blocking
                ? ConditionKind.DocRequired
                : kind == DocFindingKind.MissingField
                    ? ConditionKind.DataValidation
                    : null;
            if (condKind is null) continue;
            var title = blocking
                ? $"Replace document — {kind}"
                : $"Document data — {kind}";
            if (open.Any(oc => oc.Kind == condKind
                && oc.LinkedEntity == ConditionLink.VaultDoc
                && oc.LinkedEntityId == doc.VaultDocId
                && oc.Title == title)) continue;
            var created = await conditions.OpenAsync(
                new ConditionDraft(doc.ApplicationId, condKind, title,
                    Detail: $"auto:classifier {kind} on {doc.VaultDocId}",
                    Blocking: blocking,
                    LinkedEntity: ConditionLink.VaultDoc,
                    LinkedEntityId: doc.VaultDocId,
                    OpenedBy: Actor), ct);
            await audit.WriteAsync(Actor, "condition_opened",
                "condition", created.ConditionId, docClasses,
                $"classifier {kind} on {doc.VaultDocId}", ct);
            await events.WriteAsync(new LoanEventDraft(
                doc.ApplicationId, LoanEventKind.ConditionOpened,
                Actor, "condition", created.ConditionId,
                FieldPath: $"conditions.{created.Kind}",
                AfterVal: created.State), ct);
        }

        // distance_check — heuristic-grade: the employer's state
        // parsed from extracted address text vs. the application's
        // subject-property state. Divergence opens one review
        // condition (non-blocking — coarse signal, not a gate);
        // the actual state codes never enter audit/events.
        if (result.Fields.TryGetValue("employer_address",
                out var employerAddr)
            && EmployerLocality.StateOf(employerAddr) is { } empState)
        {
            var propState = EmployerLocality.StateOf(
                app.Doc.Get("subject_property.state")
                    ?.GetValue<string>());
            if (propState is not null && propState != empState
                && !open.Any(oc =>
                    oc.Kind == ConditionKind.DistanceCheck))
            {
                var created = await conditions.OpenAsync(
                    new ConditionDraft(doc.ApplicationId,
                        ConditionKind.DistanceCheck,
                        "Review — employer locality differs from subject property",
                        Detail: "auto:classifier state heuristic "
                            + $"(doc {doc.VaultDocId})",
                        Blocking: false,
                        LinkedEntity: ConditionLink.Application,
                        LinkedEntityId: doc.ApplicationId,
                        OpenedBy: Actor), ct);
                await audit.WriteAsync(Actor, "condition_opened",
                    "condition", created.ConditionId, docClasses,
                    $"distance_check on {doc.VaultDocId}", ct);
                await events.WriteAsync(new LoanEventDraft(
                    doc.ApplicationId, LoanEventKind.ConditionOpened,
                    Actor, "condition", created.ConditionId,
                    FieldPath: $"conditions.{created.Kind}",
                    AfterVal: created.State), ct);
            }
        }
    }
}

/// Coarse US-state parser for the `distance_check` heuristic (M18 P3)
/// — a trailing "ST" abbrev or an embedded full state name normalizes
/// to the two-letter code. Heuristic-grade by design (no geocode
/// service — an ADR-0007 vendor decision if it ever earns one).
internal static class EmployerLocality
{
    private static readonly Regex TrailingAbbrev = new(
        @"\b([A-Za-z]{2})\b\s*(?:\d{5}(?:-\d{4})?)?\s*$",
        RegexOptions.Compiled);

    private static readonly string[] Abbrevs =
    [
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID",
        "IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS",
        "MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK",
        "OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV",
        "WI","WY","DC"
    ];

    private static readonly (string Name, string Ab)[] Names =
    [
        ("ALABAMA","AL"),("ALASKA","AK"),("ARIZONA","AZ"),
        ("ARKANSAS","AR"),("CALIFORNIA","CA"),("COLORADO","CO"),
        ("CONNECTICUT","CT"),("DELAWARE","DE"),("FLORIDA","FL"),
        ("GEORGIA","GA"),("HAWAII","HI"),("IDAHO","ID"),
        ("ILLINOIS","IL"),("INDIANA","IN"),("IOWA","IA"),
        ("KANSAS","KS"),("KENTUCKY","KY"),("LOUISIANA","LA"),
        ("MAINE","ME"),("MARYLAND","MD"),("MASSACHUSETTS","MA"),
        ("MICHIGAN","MI"),("MINNESOTA","MN"),("MISSISSIPPI","MS"),
        ("MISSOURI","MO"),("MONTANA","MT"),("NEBRASKA","NE"),
        ("NEVADA","NV"),("NEW HAMPSHIRE","NH"),("NEW JERSEY","NJ"),
        ("NEW MEXICO","NM"),("NEW YORK","NY"),
        ("NORTH CAROLINA","NC"),("NORTH DAKOTA","ND"),("OHIO","OH"),
        ("OKLAHOMA","OK"),("OREGON","OR"),("PENNSYLVANIA","PA"),
        ("RHODE ISLAND","RI"),("SOUTH CAROLINA","SC"),
        ("SOUTH DAKOTA","SD"),("TENNESSEE","TN"),("TEXAS","TX"),
        ("UTAH","UT"),("VERMONT","VT"),("VIRGINIA","VA"),
        ("WASHINGTON","WA"),("WEST VIRGINIA","WV"),("WISCONSIN","WI"),
        ("WYOMING","WY"),("DISTRICT OF COLUMBIA","DC")
    ];

    public static string? StateOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = TrailingAbbrev.Match(text.Trim());
        if (m.Success)
        {
            var ab = m.Groups[1].Value.ToUpperInvariant();
            if (Abbrevs.Contains(ab)) return ab;
        }
        var upper = text.ToUpperInvariant();
        foreach (var (name, ab) in Names)
            if (upper.Contains(name)) return ab;
        return null;
    }
}
