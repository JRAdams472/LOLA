"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPost, gql } from "@/lib/api";

export interface ApplicationMeta {
  id: string;
  status: string;
  pipelineStep: string;
  borrowerUserId: string | null;
  currentVersion: number;
  tridClockStart: string | null;
  borrowerName: string | null;
  tridDeadlineUtc: string | null;
}

export function useApplications(tid: string | null | undefined) {
  return useQuery({
    queryKey: ["applications", tid],
    enabled: !!tid,
    queryFn: () =>
      gql<{ applications: ApplicationMeta[] }>(
        `query Applications($t: String!) {
          applications(tenantId: $t) {
            id status pipelineStep currentVersion
            borrowerName tridClockStart tridDeadlineUtc
          }
        }`,
        { t: tid },
      ).then((d) => d.applications),
  });
}

/** Blind-index equality search — the term rides the GraphQL POST body
 *  (never a URL or history entry — SSN-safe). Staff-only server-side. */
export const SEARCH_FIELDS = [
  { field: "first_name", label: "First name" },
  { field: "last_name", label: "Last name" },
  { field: "ssn", label: "SSN" },
  { field: "email", label: "Email" },
  { field: "phone", label: "Phone" },
  { field: "street", label: "Property address" },
] as const;

export function useLoanSearch(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { field: string; value: string }) =>
      gql<{ search: { applicationIds: string[] } }>(
        `query Search($t: String!, $field: String!, $value: String!) {
          search(tenantId: $t, field: $field, value: $value) { applicationIds }
        }`,
        { t: tid, ...vars },
      ).then((d) => d.search.applicationIds),
    // Hits join the pipeline read-model — refresh it so a brand-new
    // draft resolves to a name/status row, not a bare id.
    onSuccess: () =>
      qc.invalidateQueries({ queryKey: ["applications", tid] }),
  });
}

/** Blank 1003 draft — the workspace (Phase 3) is where it gets filled. */
export function useCreateDraft(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      apiPost<{ id: string }>(`/api/tenants/${tid}/applications`, {}),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["applications", tid] }),
  });
}
