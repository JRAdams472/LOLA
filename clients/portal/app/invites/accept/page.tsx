"use client";

import { Suspense, useState } from "react";
import { useSearchParams } from "next/navigation";

const MIN_PASSWORD = 12;
const MAX_NAME = 200;

type State =
  | { kind: "form" }
  | { kind: "submitting" }
  | { kind: "error"; message: string }
  | { kind: "done" };

/**
 * Invite acceptance (M12): the emailed link carries a single-use token;
 * the borrower sets their display name + password, the API creates the
 * account, then they sign in through the normal OIDC flow. Mirrors the
 * server rules (InputValidation): name ≤200, password ≥12.
 */
function AcceptForm() {
  const params = useSearchParams();
  const token = params.get("token") ?? "";
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [state, setState] = useState<State>({ kind: "form" });

  const valid =
    token.length > 0 &&
    displayName.trim().length > 0 &&
    displayName.trim().length <= MAX_NAME &&
    password.length >= MIN_PASSWORD &&
    password === confirm;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!valid) return;
    setState({ kind: "submitting" });
    try {
      const res = await fetch("/api/invite-accept", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ token, displayName: displayName.trim(), password }),
      });
      if (res.ok) {
        setState({ kind: "done" });
        return;
      }
      const problem = await res.json().catch(() => null);
      setState({
        kind: "error",
        message:
          problem?.detail ??
          problem?.title ??
          "That invite link isn't valid — it may have expired or already been used.",
      });
    } catch {
      setState({
        kind: "error",
        message: "Something went wrong — please try again.",
      });
    }
  }

  if (state.kind === "done") {
    return (
      <div className="rounded-lg border border-border bg-surface p-6 text-center">
        <h1 className="text-lg font-semibold text-success">
          Account created
        </h1>
        <p className="mt-2 text-sm text-muted">
          Your account is ready. Sign in with your email and new password to
          start your application.
        </p>
        <a
          href="/auth/login"
          className="mt-5 inline-block rounded-md bg-accent-strong px-5 py-2.5 text-sm font-medium text-white hover:bg-accent"
        >
          Sign in
        </a>
      </div>
    );
  }

  return (
    <div className="rounded-lg border border-border bg-surface p-6">
      <h1 className="text-lg font-semibold">Set up your account</h1>
      <p className="mt-1 text-sm text-muted">
        You&rsquo;ve been invited to complete a loan application. Choose a
        display name and password to continue.
      </p>

      {token.length === 0 && (
        <p role="alert" className="mt-4 rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
          This invite link is missing its token — use the full link from
          your invitation.
        </p>
      )}
      {state.kind === "error" && (
        <p role="alert" className="mt-4 rounded-md border border-danger/40 bg-danger/10 p-3 text-sm text-danger">
          {state.message}
        </p>
      )}

      <form onSubmit={submit} className="mt-5 space-y-4">
        <div>
          <label htmlFor="displayName" className="block text-sm text-muted">
            Your name
          </label>
          <input
            id="displayName"
            name="displayName"
            type="text"
            autoComplete="name"
            required
            maxLength={MAX_NAME}
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            className="mt-1 w-full rounded-md border border-border bg-canvas px-3 py-2 text-sm text-text placeholder:text-faint focus:border-accent"
            placeholder="e.g. Sam Rivera"
          />
        </div>
        <div>
          <label htmlFor="password" className="block text-sm text-muted">
            Password{" "}
            <span className="text-faint">(at least {MIN_PASSWORD} characters)</span>
          </label>
          <input
            id="password"
            name="password"
            type="password"
            autoComplete="new-password"
            required
            minLength={MIN_PASSWORD}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className="mt-1 w-full rounded-md border border-border bg-canvas px-3 py-2 text-sm text-text focus:border-accent"
          />
        </div>
        <div>
          <label htmlFor="confirm" className="block text-sm text-muted">
            Confirm password
          </label>
          <input
            id="confirm"
            name="confirm"
            type="password"
            autoComplete="new-password"
            required
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            className="mt-1 w-full rounded-md border border-border bg-canvas px-3 py-2 text-sm text-text focus:border-accent"
          />
          {confirm.length > 0 && password !== confirm && (
            <p className="mt-1 text-xs text-danger">Passwords don&rsquo;t match.</p>
          )}
        </div>
        <button
          type="submit"
          disabled={!valid || state.kind === "submitting"}
          className="w-full rounded-md bg-accent-strong px-4 py-2.5 text-sm font-medium text-white hover:bg-accent disabled:cursor-not-allowed disabled:opacity-40"
        >
          {state.kind === "submitting" ? "Creating account…" : "Create account"}
        </button>
      </form>
    </div>
  );
}

export default function AcceptPage() {
  return (
    <div className="flex min-h-screen items-center justify-center px-4">
      <div className="w-full max-w-md">
        <div className="mb-6 text-center">
          <span className="text-sm font-semibold tracking-wide">
            LOLA <span className="font-normal text-muted">Borrower Portal</span>
          </span>
        </div>
        <Suspense>
          <AcceptForm />
        </Suspense>
      </div>
    </div>
  );
}
