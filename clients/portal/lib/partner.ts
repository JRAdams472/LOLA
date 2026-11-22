"use client";

import { useCallback, useEffect, useState } from "react";

import { apiGet } from "@/lib/api";
import { uploadDoc } from "@/lib/documents";

/**
 * Partner (Broker/Realtor) surface — M19 P2. Every shape here mirrors
 * an allowlisted server projection: `PartnerApplicationDto`,
 * `ApplicationListPage`, `BorrowerConditionDto`, `VaultDocDto`. Staff
 * fields (borrowerUserId, rate-lock internals, document intelligence)
 * never cross the wire, so they can't render even by accident — the
 * UI is not the authorization boundary, the SQL scope is.
 */

export interface PartnerLoanMeta {
  id: string;
  status: string;
  pipelineStep: string;
}

export interface PartnerApplication {
  id: string;
  status: string;
  pipelineStep: string;
  currentVersion: number;
  submittedUtc: string | null;
  loanAmount: string | null;
}

export interface PartnerCondition {
  conditionId: string;
  label: string;
  statusLabel: string;
  statusKey: string;
  neededUtc: string | null;
}

export interface PartnerUpload {
  vaultDocId: string;
  classification: string;
  statusLabel: string;
  statusKey: string;
  filename: string;
  sizeBytes: number;
  uploadedUtc: string | null;
}

interface ApplicationPage {
  items: PartnerLoanMeta[];
  total: number;
  page: number;
  pageSize: number;
}

type State<T> =
  | { status: "loading" }
  | { status: "error"; error: string }
  | { status: "ready"; data: T; reload: () => void };

function useFetch<T>(fetcher: () => Promise<T>, deps: unknown[]): State<T> {
  const [state, setState] = useState<State<T>>({ status: "loading" });
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);
  useEffect(() => {
    const ctrl = new AbortController();
    fetcher()
      .then((data) => {
        if (!ctrl.signal.aborted)
          setState({ status: "ready", data, reload });
      })
      .catch((e) => {
        if (!ctrl.signal.aborted)
          setState({ status: "error", error: `${e}` });
      });
    return () => ctrl.abort();
    // eslint-disable-next-line @eslint-react/exhaustive-deps -- fetcher is rebuilt per render; deps+nonce are the real keys
  }, [...deps, nonce, reload]);
  return state;
}

async function getOrThrow<T>(path: string): Promise<T> {
  const { status, data } = await apiGet<T & { detail?: string }>(path);
  if (status === 403) throw new Error("Not linked to this application");
  if (status === 404) throw new Error("Application not found");
  if (status !== 200) throw new Error(`${path}: ${status}`);
  return data;
}

/** Linked applications only — the API's partner scope makes the list
 *  server-authoritative; there is no client-side filter. */
export function usePartnerLoans(tid: string | null | undefined) {
  return useFetch(
    async () =>
      (await getOrThrow<ApplicationPage>(
        `/api/tenants/${tid}/applications?page=1&pageSize=100`,
      )).items,
    [tid],
  );
}

export function usePartnerApplication(
  tid: string | null | undefined,
  appId: string,
) {
  return useFetch(
    async () =>
      getOrThrow<PartnerApplication>(
        `/api/tenants/${tid}/applications/${appId}`,
      ),
    [tid, appId],
  );
}

export function usePartnerConditions(
  tid: string | null | undefined,
  appId: string,
) {
  return useFetch(
    async () =>
      getOrThrow<PartnerCondition[]>(
        `/api/tenants/${tid}/applications/${appId}/conditions`,
      ),
    [tid, appId],
  );
}

/** Own uploads only — the API filters uploaded_by=partner in SQL. */
export function usePartnerUploads(
  tid: string | null | undefined,
  appId: string,
) {
  return useFetch(
    async () =>
      getOrThrow<PartnerUpload[]>(
        `/api/tenants/${tid}/applications/${appId}/uploads`,
      ),
    [tid, appId],
  );
}

export { uploadDoc };
