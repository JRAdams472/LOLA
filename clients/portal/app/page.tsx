"use client";

import { useEffect } from "react";

import AppShell from "@/components/AppShell";
import LoanCard from "@/components/LoanCard";
import { usePortalLoans } from "@/lib/loans";
import { useMe } from "@/lib/me";

/**
 * Borrower home — the loan dashboard: status tracker, next action, and
 * document/upload summaries for the borrower's own applications only.
 */
export default function HomePage() {
  const me = useMe();
  // M19 partner roles land on their own surface — no borrower
  // dashboard for a Broker/Realtor session.
  const isPartner =
    me.status === "ready" &&
    me.me.roles.some((r) => r === "Broker" || r === "Realtor");
  const loans = usePortalLoans(
    me.status === "ready" && !isPartner ? me.me.tid : undefined,
  );

  useEffect(() => {
    if (isPartner) window.location.assign("/partner");
  }, [isPartner]);

  if (isPartner) return null;

  return (
    <AppShell>
      <h1 className="text-xl font-semibold">Home</h1>
      {me.status === "ready" && (
        <p className="mt-1 text-sm text-muted">
          Welcome{me.me.name ? `, ${me.me.name}` : ""}.
        </p>
      )}

      <div className="mt-6">
        {loans.status === "loading" && (
          <p className="text-sm text-muted">Loading your application…</p>
        )}

        {loans.status === "error" && (
          <div
            role="alert"
            className="rounded-lg border border-danger/40 bg-danger/10 p-4 text-sm text-danger"
          >
            We couldn&apos;t load your application. Please try again in a
            moment.
          </div>
        )}

        {loans.status === "ready" && loans.loans.length === 0 && (
          <section
            aria-label="Your application"
            className="rounded-lg border border-border bg-surface p-5"
          >
            <h2 className="text-sm font-medium">Your application</h2>
            <p className="mt-2 text-sm text-muted">
              When your loan officer starts your application, its status
              and next steps will appear here.
            </p>
          </section>
        )}

        {loans.status === "ready" && (
          <div className="flex flex-col gap-4">
            {loans.loans.map((loan) => (
              <LoanCard key={loan.id} loan={loan} />
            ))}
          </div>
        )}
      </div>
    </AppShell>
  );
}
