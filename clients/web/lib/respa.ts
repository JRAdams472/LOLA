"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPost, gql } from "@/lib/api";

/// ——— RESPA servicing/escrow posture (M23 P3) ————————————————————————
/// Reads ride GraphQL over the same staff-scoped handler as REST
/// (ADR-0011) — null for non-staff. Analysis runs and notice
/// transitions POST through the BFF and invalidate the posture;
/// analyses are append-only, so each analyze POST lands a new row.

export interface EscrowAnalysis {
  analysisId: string;
  verdict: string;
  annualDisbursements: number | null;
  cushionTarget: number | null;
  lowPoint: number | null;
  determinationAmount: number | null;
  refundRequired: boolean;
  missingInputs: string[];
  actor: string;
  createdUtc: string | null;
}

export interface ServicingNoticeEvent {
  status: string;
  actor: string;
  createdUtc: string | null;
}

export interface ServicingNotice {
  noticeId: string;
  noticeKind: string;
  transferEffective: string;
  transferorLabel: string;
  transfereeLabel: string;
  sendBy: string;
  status: string;
  overdue: boolean;
  sentLate: boolean;
  sentUtc: string | null;
  receivedUtc: string | null;
  events: ServicingNoticeEvent[];
}

export interface RespaReport {
  latestAnalysis: EscrowAnalysis | null;
  analysisHistory: EscrowAnalysis[];
  afbaDisclosureRequired: boolean;
  notices: ServicingNotice[];
}

const APP = (t: string, a: string) => `/api/tenants/${t}/applications/${a}`;
const GQL_APP = `($t: String!, $id: String!)`;

const ANALYSIS_FIELDS = `
  analysisId verdict annualDisbursements cushionTarget lowPoint
  determinationAmount refundRequired missingInputs actor createdUtc
`;
const NOTICE_FIELDS = `
  noticeId noticeKind transferEffective transferorLabel transfereeLabel
  sendBy status overdue sentLate sentUtc receivedUtc
  events { status actor createdUtc }
`;

export function useRespa(
  tid: string | null | undefined,
  appId: string,
  enabled = true,
) {
  return useQuery({
    queryKey: ["respa", tid, appId],
    enabled: !!tid && !!appId && enabled,
    queryFn: () =>
      gql<{ respa: RespaReport | null }>(
        `query Respa${GQL_APP} {
          respa(tenantId: $t, appId: $id) {
            latestAnalysis { ${ANALYSIS_FIELDS} }
            analysisHistory { ${ANALYSIS_FIELDS} }
            afbaDisclosureRequired
            notices { ${NOTICE_FIELDS} }
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.respa),
  });
}

function useInvalidateRespa(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ["respa", tid, appId] });
}

export function useRunEscrowAnalysis(
  tid: string | null | undefined,
  appId: string,
) {
  const invalidate = useInvalidateRespa(tid, appId);
  return useMutation({
    mutationFn: () => apiPost(`${APP(tid!, appId)}/respa/escrow/analyze`, {}),
    onSuccess: invalidate,
  });
}

export function useRecordTransfer(
  tid: string | null | undefined,
  appId: string,
) {
  const invalidate = useInvalidateRespa(tid, appId);
  return useMutation({
    mutationFn: (body: {
      transferEffective: string;
      transferorLabel: string;
      transfereeLabel: string;
    }) => apiPost(`${APP(tid!, appId)}/respa/servicing/transfer`, body),
    onSuccess: invalidate,
  });
}

export function useMarkNoticeSent(
  tid: string | null | undefined,
  appId: string,
) {
  const invalidate = useInvalidateRespa(tid, appId);
  return useMutation({
    mutationFn: (noticeId: string) =>
      apiPost(
        `${APP(tid!, appId)}/respa/servicing/notices/${noticeId}/sent`,
        {},
      ),
    onSuccess: invalidate,
  });
}

export function useMarkNoticeReceived(
  tid: string | null | undefined,
  appId: string,
) {
  const invalidate = useInvalidateRespa(tid, appId);
  return useMutation({
    mutationFn: (noticeId: string) =>
      apiPost(
        `${APP(tid!, appId)}/respa/servicing/notices/${noticeId}/received`,
        {},
      ),
    onSuccess: invalidate,
  });
}
