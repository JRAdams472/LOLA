"use client";

import Link from "next/link";

import AppShell from "@/components/AppShell";
import { useMe } from "@/lib/me";
import { usePartnerLoans } from "@/lib/partner";

/**
 * Partner home — linked loan applications only. The server's partner
 * scope decides what's linked; revoked links disappear on reload.
 */
export default function PartnerHomePage() {
  const me = useMe();
  const loans = usePartnerLoans(me.status === "ready" ? me.me.tid : undefined);

  return (
    <AppShell>
      <h1 className="text-xl font-semibold">Partner portal</h1>
      {me.status === "ready" && (
        <p className="mt-1 text-sm text-muted">
          Welcome{me.me.name ? `, ${me.me.name}` : ""}. Applications
          you&apos;re linked to appear below.
        </p>
      )}

      <div className="mt-6">
        {loans.status === "loading" && (
          <p className="text-sm text-muted">Loading linked loans…</p>
        )}

        {loans.status === "error" && (
          <div
            role="alert"
            className="rounded-lg border border-danger/40 bg-danger/10 p-4 text-sm text-danger"
          >
            We couldn&apos;t load your linked loans. Please try again in a
            moment.
          </div>
        )}

        {loans.status === "ready" && loans.data.length === 0 && (
          <section
            aria-label="Linked loans"
            className="rounded-lg border border-border bg-surface p-5"
          >
            <h2 className="text-sm font-medium">Linked loans</h2>
            <p className="mt-2 text-sm text-muted">
              When a loan officer links you to an application, it will
              appear here with its status and next steps.
            </p>
          </section>
        )}

        {loans.status === "ready" && loans.data.length > 0 && (
          <div className="flex flex-col gap-3">
            {loans.data.map((loan) => (
              <Link
                key={loan.id}
                href={`/partner/${loan.id}`}
                className="rounded-lg border border-border bg-surface p-4 transition-colors hover:border-accent"
              >
                <div className="flex items-baseline justify-between">
                  <span className="font-mono text-sm text-accent">
                    {loan.id}
                  </span>
                  <span className="rounded-full border border-border px-2 py-0.5 text-xs text-muted">
                    {loan.status}
                  </span>
                </div>
                <p className="mt-1 text-xs text-muted">
                  Step: {loan.pipelineStep}
                </p>
              </Link>
            ))}
          </div>
        )}
      </div>
    </AppShell>
  );
}
