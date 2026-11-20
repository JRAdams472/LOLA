"use client";

import { useCallback, useEffect, useState } from "react";

import { apiGet } from "@/lib/api";

/**
 * Mirrors the server BorrowerConditionDto — public fields only; the
 * internal detail, linkage, and staff reasons never leave the API.
 * Titles arrive semi-technical ("Replace document — expired_doc"), so
 * `label` maps them to borrower language.
 */
export interface BorrowerCondition {
  conditionId: string;
  kind: string;
  state: string;
  blocking: boolean;
  title: string;
  openedUtc: string;
  closedUtc: string | null;
}

type ConditionsState =
  | { status: "loading" }
  | { status: "error" }
  | { status: "ready"; conditions: BorrowerCondition[]; reload: () => void };

export function useBorrowerConditions(
  tid: string | null | undefined,
  appId: string | undefined,
): ConditionsState {
  const [state, setState] = useState<ConditionsState>({ status: "loading" });
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);
  useEffect(() => {
    if (!tid || !appId) return;
    let dead = false;
    apiGet<BorrowerCondition[]>(
      `/api/tenants/${tid}/applications/${appId}/conditions`,
    )
      .then((r) => {
        if (dead) return;
        setState(
          r.status >= 400 || !Array.isArray(r.data)
            ? { status: "error" }
            : { status: "ready", conditions: r.data, reload },
        );
      })
      .catch(() => {
        if (!dead) setState({ status: "error" });
      });
    return () => {
      dead = true;
    };
  }, [tid, appId, nonce, reload]);
  return state;
}

/** Finding token → borrower language for auto-written titles. */
const REPLACE_REASONS: Record<string, string> = {
  expired_doc: "it looks out of date",
  unreadable: "we couldn't read it",
  kind_mismatch: "it's a different type than expected",
};

/**
 * One line in borrower vocabulary. Auto titles carry the finding
 * token; map them rather than teaching the borrower our vocabulary.
 */
export function conditionLabel(c: BorrowerCondition): string {
  const tail = c.title.split("—").pop()?.trim() ?? "";
  if (c.title.startsWith("Replace document"))
    return `Please send a new copy — ${REPLACE_REASONS[tail] ?? "the last one didn't work"}.`;
  if (c.title.startsWith("Document data"))
    return "We couldn't read all the details — a clearer copy may help.";
  if (c.kind === "distance_check")
    return "We're reviewing where you work versus the home's location.";
  if (c.kind === "doc_required")
    return "We're waiting on a document — check the list below.";
  return c.title;
}
