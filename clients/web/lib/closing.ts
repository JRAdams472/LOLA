"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPatch, apiPost, gql } from "@/lib/api";
import type { FieldError } from "@/lib/loans";

/// ——— Closing Costs & Docs (MPE-211) ——————————————————————————————
/// All reads ride GraphQL over the same handlers as REST (ADR-0011);
/// mutations hit the REST application endpoints through the BFF.
/// 202 enqueue endpoints (price/fees/packages) resolve asynchronously
/// via the outbox worker — callers poll the matching GET until the
/// latest row's status settles (done | failed | submitted).

export interface RateOffer {
  rate: number;
  price: number | null;
  margin: number | null;
  lockTermDays: number | null;
  investorName: string | null;
}

export interface PricedProduct {
  productCode: string;
  productName: string | null;
  eligible: boolean;
  ineligibilityReasons: string[];
  offers: RateOffer[];
}

export interface PricingEval {
  evalId: string;
  applicationId: string;
  adapter: string;
  asOf: string | null;
  status: string;
  result: { products: PricedProduct[] } | null;
  error: string | null;
  actor: string;
  createdUtc: string | null;
  completedUtc: string | null;
}

export interface RateLock {
  lockId: string;
  evalId: string;
  productCode: string;
  rate: number;
  price: number | null;
  lockTermDays: number;
  investorName: string | null;
  lockedUtc: string;
  expiresUtc: string;
  status: string;
  unlockReason: string | null;
  unlockedUtc: string | null;
}

const APP = (t: string, a: string) => `/api/tenants/${t}/applications/${a}`;
const GQL_APP = `($t: String!, $id: String!)`;

export function usePricing(
  tid: string | null | undefined,
  appId: string,
  poll = false,
) {
  return useQuery({
    queryKey: ["pricing", tid, appId],
    enabled: !!tid && !!appId,
    refetchInterval: poll ? 1500 : false,
    queryFn: () =>
      gql<{ pricing: PricingEval | null }>(
        `query Pricing${GQL_APP} {
          pricing(tenantId: $t, appId: $id) {
            evalId applicationId adapter asOf status error actor
            createdUtc completedUtc
            result { products {
              productCode productName eligible ineligibilityReasons
              offers { rate price margin lockTermDays investorName }
            } }
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.pricing),
  });
}

export function useRateLock(tid: string | null | undefined, appId: string) {
  return useQuery({
    queryKey: ["rateLock", tid, appId],
    enabled: !!tid && !!appId,
    queryFn: () =>
      gql<{ rateLock: RateLock | null }>(
        `query RateLock${GQL_APP} {
          rateLock(tenantId: $t, appId: $id) {
            lockId evalId productCode rate price lockTermDays
            investorName lockedUtc expiresUtc status
            unlockReason unlockedUtc
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.rateLock),
  });
}

export interface FeeLine {
  itemId: string;
  feeCode: string;
  label: string | null;
  base: number;
  computed: number;
  overrideAmount: number | null;
  overrideReason: string | null;
  effective: number;
}

export interface FeeSection {
  section: string;
  items: FeeLine[];
  computedTotal: number;
  effectiveTotal: number;
}

export interface StaleOverride {
  overrideId: string;
  feeCode: string;
  section: string;
  amount: number;
  reason: string;
  actor: string;
  createdUtc: string | null;
  variant: string;
}

export interface FeeBreakdown {
  evalId: string;
  variant: string | null;
  applicationId: string;
  adapter: string;
  status: string;
  error: string | null;
  sections: FeeSection[];
  computedTotal: number;
  effectiveTotal: number;
  staleOverrides: StaleOverride[];
  createdUtc: string | null;
  completedUtc: string | null;
}

export function useFees(
  tid: string | null | undefined,
  appId: string,
  poll = false,
) {
  return useQuery({
    queryKey: ["fees", tid, appId],
    enabled: !!tid && !!appId,
    refetchInterval: poll ? 1500 : false,
    queryFn: () =>
      gql<{ fees: { batchId: string; evals: FeeBreakdown[] } | null }>(
        `query Fees${GQL_APP} {
          fees(tenantId: $t, appId: $id) {
            batchId
            evals {
              evalId variant applicationId adapter status error
              computedTotal effectiveTotal createdUtc completedUtc
              sections { section computedTotal effectiveTotal
                items { itemId feeCode label base computed
                  overrideAmount overrideReason effective } }
              staleOverrides { overrideId feeCode section amount
                reason actor createdUtc variant }
            }
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.fees),
  });
}

export interface DocDto {
  docId: string;
  applicationId: string;
  kind: string;
  packageName: string;
  status: string;
  filename: string | null;
  error: string | null;
  createdUtc: string | null;
  completedUtc: string | null;
}

export function useDocuments(
  tid: string | null | undefined,
  appId: string,
  poll = false,
) {
  return useQuery({
    queryKey: ["documents", tid, appId],
    enabled: !!tid && !!appId,
    refetchInterval: poll ? 1500 : false,
    queryFn: () =>
      gql<{ documents: DocDto[] }>(
        `query Documents${GQL_APP} {
          documents(tenantId: $t, appId: $id) {
            docId applicationId kind packageName status filename
            error createdUtc completedUtc
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.documents),
  });
}

export interface VaultDoc {
  vaultDocId: string;
  applicationId: string;
  classification: string;
  filename: string;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  status: string;
  error: string | null;
  createdUtc: string | null;
  scannedUtc: string | null;
}

export function useUploads(
  tid: string | null | undefined,
  appId: string,
  poll = false,
) {
  return useQuery({
    queryKey: ["uploads", tid, appId],
    enabled: !!tid && !!appId,
    refetchInterval: poll ? 1500 : false,
    queryFn: () =>
      gql<{ uploads: VaultDoc[] }>(
        `query Uploads${GQL_APP} {
          uploads(tenantId: $t, appId: $id) {
            vaultDocId applicationId classification filename
            contentType sizeBytes sha256 status error
            createdUtc scannedUtc
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.uploads),
  });
}

/// Mutations — all return {status, data}; 202s carry the accepted id
/// and the caller polls the read surface.

const invalidateClosing = (
  qc: ReturnType<typeof useQueryClient>,
  tid: string | null | undefined,
  appId: string,
) =>
  Promise.all([
    qc.invalidateQueries({ queryKey: ["pricing", tid, appId] }),
    qc.invalidateQueries({ queryKey: ["rateLock", tid, appId] }),
    qc.invalidateQueries({ queryKey: ["fees", tid, appId] }),
    qc.invalidateQueries({ queryKey: ["documents", tid, appId] }),
    qc.invalidateQueries({ queryKey: ["uploads", tid, appId] }),
  ]);

export function usePrice(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost(`${APP(tid!, appId)}/price`, {}),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["pricing", tid, appId] }),
  });
}

export function useEvaluateFees(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost(`${APP(tid!, appId)}/fees`, {}),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["fees", tid, appId] }),
  });
}

export function useOverrideFee(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { itemId: string; amount: number | null; reason: string }) =>
      apiPatch<FieldError[]>(`${APP(tid!, appId)}/fees/${vars.itemId}`, {
        amount: vars.amount,
        reason: vars.reason,
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["fees", tid, appId] }),
  });
}

export function useLockRate(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: {
      evalId: string;
      productCode: string;
      rate: number;
      lockTermDays: number | null;
    }) => apiPost(`${APP(tid!, appId)}/lock`, vars),
    onSuccess: () => invalidateClosing(qc, tid, appId),
  });
}

export function useUnlockRate(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (reason: string) =>
      apiPost(`${APP(tid!, appId)}/unlock`, { reason }),
    onSuccess: () => invalidateClosing(qc, tid, appId),
  });
}

export const PACKAGE_KINDS = [
  { value: "le-package", label: "LE package" },
  { value: "adverse-action", label: "Adverse action" },
] as const;

export function useRequestPackage(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { kind: string; reasonCodes: string[] }) =>
      apiPost(`${APP(tid!, appId)}/packages`, vars),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["documents", tid, appId] }),
  });
}

/** Audited download — the endpoint 302s to a minted capability URL
 *  which the BFF now forwards; a new tab follows it to the content. */
export const downloadUrl = (tid: string, appId: string, docId: string, vault = false) =>
  `/bff${APP(tid, appId)}/${vault ? "uploads" : "documents"}/${docId}/download`;
