"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPost, gql } from "@/lib/api";

/// ——— HMDA reporting posture (M24 P3) ———————————————————————————————
/// Reads ride GraphQL over the same staff-scoped handler as REST
/// (ADR-0011) — null for non-staff. Assembly POSTs through the BFF and
/// invalidates the posture; rows are append-only, so each assemble
/// lands a new lineage entry. Export + freeze are tenant-level REST —
/// the export downloads pipe-delimited bytes straight from the API.

export interface HmdaEdit {
  editId: string;
  tier: string;
  result: string;
  detail: string;
}

export interface HmdaLarRow {
  larRowId: string;
  uli: string;
  reportingYear: number;
  fieldsJson: string;
  edits: HmdaEdit[];
  actor: string;
  createdUtc: string | null;
}

export interface HmdaFigures {
  actionTaken: string | null;
  actionTakenDate: string | null;
  rateSpread: string | null;
  hoepaStatus: string | null;
  failCount: number;
  warnCount: number;
}

export interface HmdaReport {
  latest: HmdaLarRow | null;
  lineage: HmdaLarRow[];
  figures: HmdaFigures;
}

const APP = (t: string, a: string) => `/api/tenants/${t}/applications/${a}`;

const ROW_FIELDS = `
  larRowId uli reportingYear fieldsJson actor createdUtc
  edits { editId tier result detail }
`;

export function useHmda(
  tid: string | null | undefined,
  appId: string,
  enabled = true,
) {
  return useQuery({
    queryKey: ["hmda", tid, appId],
    enabled: !!tid && !!appId && enabled,
    queryFn: () =>
      gql<{ hmda: HmdaReport | null }>(
        `query Hmda($t: String!, $id: String!) {
          hmda(tenantId: $t, appId: $id) {
            latest { ${ROW_FIELDS} }
            lineage { ${ROW_FIELDS} }
            figures {
              actionTaken actionTakenDate rateSpread hoepaStatus
              failCount warnCount
            }
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.hmda),
  });
}

function useInvalidateHmda(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ["hmda", tid, appId] });
}

export function useAssembleLar(
  tid: string | null | undefined,
  appId: string,
) {
  const invalidate = useInvalidateHmda(tid, appId);
  return useMutation({
    mutationFn: () => apiPost(`${APP(tid!, appId)}/hmda/assemble`, {}),
    onSuccess: invalidate,
  });
}

/// The export is a file download, not a JSON mutation — the panel
/// opens it through the browser so the bytes land as a download.
export function exportUrl(tid: string, year: number): string {
  return `/api/tenants/${tid}/hmda/export?year=${year}`;
}

export function useFreezeSnapshot(tid: string | null | undefined) {
  return useMutation({
    mutationFn: (year: number) =>
      apiPost(`/api/tenants/${tid}/hmda/freeze?year=${year}`, {}),
  });
}
