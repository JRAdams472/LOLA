"use client";

import { useEffect, useState } from "react";

import { gql } from "@/lib/api";

/**
 * Borrower-safe projections only — these interfaces mirror
 * PortalLoanView/PortalLoanMeta. Officer fields (pipelineStep,
 * currentVersion, borrowerUserId, trid*) don't exist on this type, so
 * they can't render even by accident.
 */
export interface PortalStage {
  key: string;
  label: string;
  done: boolean;
  current: boolean;
}

export interface PortalUploadItem {
  vaultDocId: string;
  classification: string;
  statusLabel: string;
  statusKey: string;
  filename: string;
  sizeBytes: number;
  uploadedUtc: string | null;
}

export interface PortalDocItem {
  docId: string;
  packageName: string;
  kindLabel: string;
  ready: boolean;
  filename: string | null;
  completedUtc: string | null;
}

export interface PortalLoanMeta {
  id: string;
  statusLabel: string;
  statusStage: number;
}

export interface PortalLoan extends PortalLoanMeta {
  statusStages: PortalStage[];
  docJson: string;
  documents: PortalDocItem[];
  uploads: PortalUploadItem[];
}

type LoansState =
  | { status: "loading" }
  | { status: "error"; error: string }
  | { status: "ready"; loans: PortalLoan[] };

const LOAN_FIELDS = `
  id statusLabel statusStage
  statusStages { key label done current }
  docJson
  uploads { vaultDocId classification statusLabel statusKey
            filename sizeBytes uploadedUtc }
  documents { docId packageName kindLabel ready filename completedUtc }`;

/** Own applications only — the server filters by the session's sub. */
export function usePortalLoans(tid: string | null | undefined): LoansState {
  const [state, setState] = useState<LoansState>({ status: "loading" });
  useEffect(() => {
    if (!tid) return; // `me` still resolving — stay loading, never flash empty
    const ctrl = new AbortController();
    gql<{ portalLoans: PortalLoanMeta[] }>(
      `query PortalLoans($t: String!) {
         portalLoans(tenantId: $t) { id statusLabel statusStage } }`,
      { t: tid },
    )
      .then((d) =>
        Promise.all(
          d.portalLoans.map((m) =>
            gql<{ portalLoan: PortalLoan | null }>(
              `query PortalLoan($t: String!, $id: String!) {
                 portalLoan(tenantId: $t, appId: $id) { ${LOAN_FIELDS} } }`,
              { t: tid, id: m.id },
            ).then((r) => r.portalLoan),
          ),
        ),
      )
      .then((loans) => {
        if (!ctrl.signal.aborted)
          setState({
            status: "ready",
            loans: loans.filter((l): l is PortalLoan => l !== null),
          });
      })
      .catch((e) => {
        if (!ctrl.signal.aborted)
          setState({ status: "error", error: `${e}` });
      });
    return () => ctrl.abort();
  }, [tid]);
  return state;
}

/** Borrower-facing name from the masked doc — renders "•••lace" when
 *  PII is masked, which is the point: the dashboard shows what the
 *  server allowed, never a client-side guess. */
export function maskedName(docJson: string): string | null {
  try {
    const party = (JSON.parse(docJson) as {
      party?: { first_name?: string; last_name?: string }[];
    }).party;
    const first = party?.[0]?.first_name ?? "";
    const last = party?.[0]?.last_name ?? "";
    const name = `${first} ${last}`.trim();
    return name || null;
  } catch {
    return null;
  }
}

/** One-line "what happens next" per stage — vocabulary lives in
 *  BorrowerStatusMap server-side; this only picks the hint. */
export function nextActionFor(loan: PortalLoan): string {
  if (loan.uploads.some((u) => u.statusKey === "rejected"))
    return "Check your uploads — one needs attention.";
  switch (loan.statusStage) {
    case 0:
      return "Finish and submit your application.";
    case 1:
      return "We're reviewing your application — nothing needed right now.";
    case 2:
      return "Your decision is ready — check your documents.";
    default:
      return "";
  }
}
