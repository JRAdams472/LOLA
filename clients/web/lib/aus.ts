"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPost, gql } from "@/lib/api";

/// ——— AUS underwriting posture (M25 P3) ————————————————————————————
/// Reads ride GraphQL over the same staff-scoped handler as REST —
/// null for non-staff. Submit POSTs through the BFF with the vendor
/// picker (du | lpa); results are append-only, so each submit lands a
/// new lineage entry, never an overwrite. Raw response XML never
/// leaves the tenant boundary — this surface sees normalized data only.

export interface AusFinding {
  vendorCode: string;
  category: string;
  severity: string;
  message: string;
  conditionId: string | null;
}

export interface AusVendorPosture {
  vendor: string;
  recommendation: string;
  riskClass: string | null;
  caseId: string | null;
  createdUtc: string | null;
  findingsCount: number;
  findings: AusFinding[];
}

export interface AusLineageEntry {
  resultId: string;
  vendor: string;
  recommendation: string;
  createdUtc: string | null;
  findingsCount: number;
}

export interface AusReport {
  vendors: AusVendorPosture[];
  lineage: AusLineageEntry[];
}

const APP = (t: string, a: string) => `/api/tenants/${t}/applications/${a}`;

export function useAus(
  tid: string | null | undefined,
  appId: string,
  enabled = true,
) {
  return useQuery({
    queryKey: ["aus", tid, appId],
    enabled: !!tid && !!appId && enabled,
    // AUS completion is asynchronous (outbox drain) — poll while the
    // tab is open so a fresh submission surfaces without a manual
    // reload. Scoped: the hook only runs when the AUS tab is active.
    refetchInterval: 3000,
    queryFn: () =>
      gql<{ aus: AusReport | null }>(
        `query Aus($t: String!, $id: String!) {
          aus(tenantId: $t, appId: $id) {
            vendors {
              vendor recommendation riskClass caseId createdUtc
              findingsCount
              findings {
                vendorCode category severity message conditionId
              }
            }
            lineage {
              resultId vendor recommendation createdUtc findingsCount
            }
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.aus),
  });
}

export function useSubmitAus(
  tid: string | null | undefined,
  appId: string,
) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vendor: "du" | "lpa") =>
      apiPost(`${APP(tid!, appId)}/aus/submit?vendor=${vendor}`, {}),
    onSuccess: () =>
      qc.invalidateQueries({ queryKey: ["aus", tid, appId] }),
  });
}
