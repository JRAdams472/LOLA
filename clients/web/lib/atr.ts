"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPost, gql } from "@/lib/api";

/// ——— ATR/QM battery (M22 P3) ————————————————————————————————————
/// Reads ride GraphQL over the same staff-scoped handler as REST
/// (ADR-0011) — null for non-staff. Evaluate is an explicit re-run:
/// results are append-only, so each POST lands a new row + event.

export interface AtrVerdict {
  test: string;
  verdict: string;
  detail: string;
}

export interface AtrEvaluation {
  resultId: string;
  evalId: string | null;
  dti: number | null;
  apr: number | null;
  apor: number | null;
  verdicts: AtrVerdict[];
  actor: string;
  createdUtc: string | null;
}

export interface AtrReport {
  latest: AtrEvaluation | null;
  history: AtrEvaluation[];
}

const APP = (t: string, a: string) => `/api/tenants/${t}/applications/${a}`;
const GQL_APP = `($t: String!, $id: String!)`;

const EVAL_FIELDS = `
  resultId evalId dti apr apor actor createdUtc
  verdicts { test verdict detail }
`;

export function useAtr(
  tid: string | null | undefined,
  appId: string,
  enabled = true,
) {
  return useQuery({
    queryKey: ["atr", tid, appId],
    enabled: !!tid && !!appId && enabled,
    queryFn: () =>
      gql<{ atr: AtrReport | null }>(
        `query Atr${GQL_APP} {
          atr(tenantId: $t, appId: $id) {
            latest { ${EVAL_FIELDS} }
            history { ${EVAL_FIELDS} }
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.atr),
  });
}

export function useEvaluateAtr(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost(`${APP(tid!, appId)}/atr/evaluate`, {}),
    onSuccess: () =>
      qc.invalidateQueries({ queryKey: ["atr", tid, appId] }),
  });
}
