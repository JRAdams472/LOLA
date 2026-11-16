"use client";

import Link from "next/link";
import { useState } from "react";

import AppShell from "@/components/AppShell";
import WizardField from "@/components/WizardField";
import { displayValue, errorsBySection, useWizard } from "@/lib/apply";
import { fieldsFor, type SectionId } from "@/lib/field-map";
import { useMe } from "@/lib/me";

const STEPS: { id: SectionId | "review"; label: string }[] = [
  { id: "borrower", label: "Borrower info" },
  { id: "employment", label: "Employment & income" },
  { id: "assets", label: "Assets" },
  { id: "liabilities", label: "Liabilities" },
  { id: "loan", label: "Property & loan" },
  { id: "review", label: "Review" },
];

/**
 * /apply — the MISMO intake wizard. Draft saves on section navigation
 * and via explicit save; masked PII shows "•••"+last-4 confirm and is
 * never echoed into a PATCH; submit errors deep-link to their section.
 */
export default function ApplyPage() {
  const me = useMe();
  const wiz = useWizard(me.status === "ready" ? me.me.tid : undefined);
  const [step, setStep] = useState(0);
  const [submitted, setSubmitted] = useState(false);

  const state = wiz.state;
  const atReview = STEPS[step]?.id === "review";
  const isFirst = step === 0;

  async function goTo(next: number) {
    if (state.kind !== "ready") return;
    // Save-draft on every navigation — the atomic PATCH composes.
    if (state.dirty && !(await wiz.save())) return;
    setStep(next);
  }

  async function onSubmit() {
    const r = await wiz.submit();
    if (r.result === "ok") {
      setSubmitted(true);
      return;
    }
    if (r.result === "errors") {
      // Deep-link to the first section carrying errors — inline
      // messages render on the exact fields; the summary lives on
      // Review for the borrower to come back to.
      const first = errorsBySection(r.errors)[0]?.section;
      const idx = STEPS.findIndex((t) => t.id === first);
      if (idx >= 0) setStep(idx);
    }
  }

  return (
    <AppShell>
      <h1 className="text-xl font-semibold">Your application</h1>

      {state.kind === "loading" && (
        <p className="mt-6 text-sm text-muted">Loading your application…</p>
      )}

      {state.kind === "error" && (
        <div
          role="alert"
          className="mt-6 rounded-lg border border-danger/40 bg-danger/10 p-4 text-sm text-danger"
        >
          We couldn&apos;t load your application. Please try again in a
          moment.
        </div>
      )}

      {state.kind === "none" && (
        <section className="mt-6 rounded-lg border border-border bg-surface p-6">
          <h2 className="text-sm font-medium">Start your application</h2>
          <p className="mt-2 text-sm text-muted">
            Answer a few sections about you, your finances, and the
            property. You can save and come back any time.
          </p>
          <button
            type="button"
            onClick={wiz.startApplication}
            className="mt-4 rounded-md bg-accent-strong px-5 py-2.5 text-sm font-medium text-white hover:bg-accent"
          >
            Begin
          </button>
        </section>
      )}

      {state.kind === "submitted" && !submitted && (
        <section className="mt-6 rounded-lg border border-border bg-surface p-6">
          <h2 className="text-sm font-medium">{state.loan.statusLabel}</h2>
          <p className="mt-2 text-sm text-muted">
            Your application is with your loan officer — no changes are
            needed right now.
          </p>
          <Link
            href="/"
            className="mt-4 inline-block rounded-md border border-border px-4 py-2 text-sm text-text hover:bg-surface-2"
          >
            Back to Home
          </Link>
        </section>
      )}

      {state.kind === "ready" && !submitted && (
        <>
          {/* Section stepper */}
          <ol
            aria-label="Application sections"
            className="mt-5 flex flex-wrap gap-1"
          >
            {STEPS.map((s, i) => (
              <li key={s.id}>
                <button
                  type="button"
                  onClick={() => goTo(i)}
                  aria-current={i === step ? "step" : undefined}
                  className={`rounded-full border px-3 py-1 text-xs ${
                    i === step
                      ? "border-accent bg-accent/15 font-medium text-accent"
                      : i < step
                        ? "border-border bg-surface-2 text-text"
                        : "border-border text-muted hover:text-text"
                  }`}
                >
                  {s.label}
                </button>
              </li>
            ))}
          </ol>

          <section className="mt-4 rounded-lg border border-border bg-surface p-5">
            {!atReview && (
              <>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  {fieldsFor(STEPS[step].id as SectionId).map((spec) => (
                    <WizardField
                      key={spec.path}
                      spec={spec}
                      value={displayValue(state, spec.path)}
                      error={state.fieldErrors[spec.path]}
                      onChange={(p, v) =>
                        wiz.dispatch({ t: "edit", path: p, value: v })
                      }
                    />
                  ))}
                </div>
              </>
            )}

            {atReview && (
              <Review state={state} goToSection={(s) => {
                const idx = STEPS.findIndex((t) => t.id === s);
                if (idx >= 0) setStep(idx);
              }} />
            )}

            <div className="mt-6 flex items-center gap-3 border-t border-border pt-4">
              {!isFirst && (
                <button
                  type="button"
                  onClick={() => goTo(step - 1)}
                  className="rounded-md border border-border px-4 py-2 text-sm text-text hover:bg-surface-2"
                >
                  Back
                </button>
              )}
              <div className="ml-auto flex items-center gap-3">
                {state.saving && (
                  <span className="text-xs text-muted">Saving…</span>
                )}
                {!atReview && (
                  <button
                    type="button"
                    onClick={wiz.save}
                    disabled={state.saving || !state.dirty}
                    className="rounded-md border border-border px-4 py-2 text-sm text-text hover:bg-surface-2 disabled:opacity-40"
                  >
                    Save draft
                  </button>
                )}
                {atReview ? (
                  <button
                    type="button"
                    onClick={onSubmit}
                    disabled={state.saving}
                    className="rounded-md bg-accent-strong px-5 py-2 text-sm font-medium text-white hover:bg-accent disabled:opacity-40"
                  >
                    Submit application
                  </button>
                ) : (
                  <button
                    type="button"
                    onClick={() => goTo(step + 1)}
                    disabled={state.saving}
                    className="rounded-md bg-accent-strong px-5 py-2 text-sm font-medium text-white hover:bg-accent disabled:opacity-40"
                  >
                    {step === STEPS.length - 2 ? "Review" : "Next"}
                  </button>
                )}
              </div>
            </div>
          </section>
        </>
      )}

      {submitted && (
        <section className="mt-6 rounded-lg border border-border bg-surface p-6 text-center">
          <h2 className="text-lg font-semibold text-success">
            Application submitted
          </h2>
          <p className="mt-2 text-sm text-muted">
            Thanks — your application is under review. We&apos;ll update
            its status here as it moves.
          </p>
          <Link
            href="/"
            className="mt-5 inline-block rounded-md bg-accent-strong px-5 py-2.5 text-sm font-medium text-white hover:bg-accent"
          >
            Track your application
          </Link>
        </section>
      )}
    </AppShell>
  );
}

function Review({
  state,
  goToSection,
}: {
  state: Extract<ReturnType<typeof useWizard>["state"], { kind: "ready" }>;
  goToSection: (s: SectionId) => void;
}) {
  const groups = errorsBySection(state.submitErrors);
  const SECTION_LABEL: Record<string, string> = Object.fromEntries(
    STEPS.map((s) => [s.id, s.label]),
  );

  return (
    <div>
      <h2 className="text-sm font-medium">Review your answers</h2>
      <p className="mt-1 text-xs text-muted">
        Check each section, then submit. You can go back and change
        anything — your work saves as you move.
      </p>

      {groups.length > 0 && (
        <div
          role="alert"
          className="mt-4 rounded-md border border-danger/40 bg-danger/10 p-3"
        >
          <p className="text-sm font-medium text-danger">
            {state.submitErrors.length} thing
            {state.submitErrors.length === 1 ? "" : "s"} to fix:
          </p>
          <ul className="mt-2 space-y-1">
            {groups.map((g) => (
              <li key={g.section}>
                <button
                  type="button"
                  onClick={() => goToSection(g.section)}
                  className="text-sm text-accent underline-offset-4 hover:underline"
                >
                  {SECTION_LABEL[g.section]} ({g.errors.length})
                </button>
                <span className="ml-2 text-xs text-muted">
                  {g.errors[0].message}
                  {g.errors.length > 1 ? ", …" : ""}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}

      <dl className="mt-4 space-y-4">
        {STEPS.filter((s) => s.id !== "review").map((s) => (
          <div key={s.id}>
            <dt className="flex items-center justify-between border-b border-border pb-1 text-xs font-medium uppercase tracking-wide text-muted">
              {s.label}
              <button
                type="button"
                onClick={() => goToSection(s.id as SectionId)}
                className="text-[11px] normal-case text-accent underline-offset-4 hover:underline"
              >
                Edit
              </button>
            </dt>
            <dd className="mt-2 grid grid-cols-1 gap-x-6 gap-y-1.5 sm:grid-cols-2">
              {fieldsFor(s.id as SectionId).map((spec) => {
                const v = displayValue(state, spec.path);
                if (v === undefined || v === "") return null;
                return (
                  <div key={spec.path} className="flex justify-between gap-3 text-sm">
                    <span className="text-muted">{spec.label}</span>
                    <span className="text-right text-text">{v}</span>
                  </div>
                );
              })}
            </dd>
          </div>
        ))}
      </dl>
    </div>
  );
}
