"use client";

import { useMe } from "@/lib/me";

/**
 * Portal chrome — a quiet single-purpose header: LOLA wordmark, the
 * borrower's name when resolved, sign-out. No nav drawer: the borrower
 * has one job per visit (finish the application, send documents).
 */
export default function AppShell({
  children,
}: {
  children: React.ReactNode;
}) {
  const me = useMe();
  return (
    <div className="flex min-h-screen flex-col">
      <header className="border-b border-border bg-surface">
        <div className="mx-auto flex h-14 w-full max-w-3xl items-center gap-3 px-4">
          <span className="text-sm font-semibold tracking-wide text-text">
            LOLA <span className="font-normal text-muted">Borrower Portal</span>
          </span>
          <div className="ml-auto flex items-center gap-3">
            {me.status === "ready" && me.me.name && (
              <span
                data-testid="user-name"
                className="rounded-full border border-border bg-surface-2 px-3 py-1 text-xs text-muted"
              >
                {me.me.name}
              </span>
            )}
            <a
              href="/auth/logout"
              className="text-xs text-muted underline-offset-4 hover:text-text hover:underline"
            >
              Sign out
            </a>
          </div>
        </div>
      </header>
      <main className="mx-auto w-full max-w-3xl flex-1 px-4 py-8">
        {children}
      </main>
    </div>
  );
}
