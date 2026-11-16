import Link from "next/link";

import StatusTracker from "@/components/StatusTracker";
import { maskedName, nextActionFor, type PortalLoan } from "@/lib/loans";

/**
 * One application, borrower view: friendly status, progress tracker,
 * next-action hint, and document/upload counts. Everything rendered
 * here is server-shaped borrower vocabulary.
 */
export default function LoanCard({ loan }: { loan: PortalLoan }) {
  const name = maskedName(loan.docJson);
  const readyDocs = loan.documents.filter((d) => d.ready).length;
  const uploads = loan.uploads.length;
  const hint = nextActionFor(loan);

  return (
    <section
      aria-label={`Application ${loan.id}`}
      className="rounded-lg border border-border bg-surface p-5"
    >
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="text-sm font-medium text-text">
          {name ?? "Your application"}
        </h2>
        <span className="rounded-full border border-accent/40 bg-accent/10 px-2.5 py-0.5 text-[11px] font-medium text-accent">
          {loan.statusLabel}
        </span>
      </div>

      <StatusTracker stages={loan.statusStages} />

      {hint && (
        <p className="mt-4 text-sm text-muted" role="status">
          {hint}
        </p>
      )}

      {loan.statusStage === 0 && (
        <Link
          href="/apply"
          className="mt-3 inline-block rounded-md bg-accent-strong px-4 py-2 text-sm font-medium text-white hover:bg-accent"
        >
          Continue application
        </Link>
      )}

      <dl className="mt-4 flex gap-6 border-t border-border pt-4 text-xs text-muted">
        <div>
          <dt className="inline">Documents</dt>{" "}
          <dd className="inline font-medium text-text">
            {readyDocs > 0 ? `${readyDocs} ready` : "None yet"}
          </dd>
        </div>
        <div>
          <dt className="inline">Uploads</dt>{" "}
          <dd className="inline font-medium text-text">
            {uploads > 0 ? `${uploads} received` : "None yet"}
          </dd>
        </div>
      </dl>
    </section>
  );
}
