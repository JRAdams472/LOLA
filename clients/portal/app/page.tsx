"use client";

import AppShell from "@/components/AppShell";
import { useMe } from "@/lib/me";

/**
 * Borrower home — Phase 1 lands the authenticated shell; the status
 * tracker and loan card arrive with the portal read model in Phase 2.
 */
export default function HomePage() {
  const me = useMe();
  return (
    <AppShell>
      <h1 className="text-xl font-semibold">Home</h1>
      {me.status === "ready" && (
        <p className="mt-1 text-sm text-muted">
          Welcome{me.me.name ? `, ${me.me.name}` : ""}.
        </p>
      )}
      <section
        aria-label="Your application"
        className="mt-6 rounded-lg border border-border bg-surface p-5"
      >
        <h2 className="text-sm font-medium">Your application</h2>
        <p className="mt-2 text-sm text-muted">
          When your loan officer starts your application, its status and next
          steps will appear here.
        </p>
      </section>
    </AppShell>
  );
}
