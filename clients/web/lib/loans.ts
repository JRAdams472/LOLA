"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPatch, apiPost, gql } from "@/lib/api";

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

/// ——— Loan workspace (MPE-210) ——————————————————————————————————

export interface ApplicationView {
  id: string;
  status: string;
  pipelineStep: string;
  borrowerUserId: string | null;
  currentVersion: number;
  tridClockStart: string | null;
  tridDeadlineUtc: string | null;
  submittedUtc: string | null;
  docJson: string;
}

export function useApplication(tid: string | null | undefined, appId: string) {
  return useQuery({
    queryKey: ["application", tid, appId],
    enabled: !!tid && !!appId,
    queryFn: () =>
      gql<{ application: ApplicationView | null }>(
        `query Application($t: String!, $id: String!) {
          application(tenantId: $t, appId: $id) {
            id status pipelineStep borrowerUserId currentVersion
            tridClockStart tridDeadlineUtc submittedUtc docJson
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.application),
  });
}

export interface StepEvent {
  id: string;
  step: string;
  event: string;
  actor: string;
  reason: string | null;
  createdUtc: string;
}

export function useSteps(
  tid: string | null | undefined,
  appId: string,
  enabled = true,
) {
  return useQuery({
    queryKey: ["steps", tid, appId],
    enabled: !!tid && !!appId && enabled,
    queryFn: () =>
      gql<{ steps: StepEvent[] }>(
        `query Steps($t: String!, $id: String!) {
          steps(tenantId: $t, appId: $id) { id step event actor reason createdUtc }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.steps),
  });
}

export interface AuditEvent {
  seq: number;
  eventId: string;
  actor: string;
  action: string;
  entity: string;
  entityId: string | null;
  fieldClasses: string[];
  reason: string | null;
  prevHash: string;
  hash: string;
  createdUtc: string;
}

export function useApplicationAudit(
  tid: string | null | undefined,
  appId: string,
  enabled = true,
) {
  return useQuery({
    queryKey: ["appAudit", tid, appId],
    enabled: !!tid && !!appId && enabled,
    queryFn: () =>
      gql<{ applicationAudit: AuditEvent[] }>(
        `query AppAudit($t: String!, $id: String!) {
          applicationAudit(tenantId: $t, appId: $id) {
            seq eventId actor action entity entityId
            fieldClasses reason prevHash hash createdUtc
          }
        }`,
        { t: tid, id: appId },
      ).then((d) => d.applicationAudit),
  });
}

export interface FieldError {
  path: string;
  code: string;
  message: string;
}

/** 422 problem-details carry `extensions.errors` — the FieldError list
 *  maps straight onto catalog paths for per-input messages. */
export function fieldErrors(problem: unknown): FieldError[] {
  const ext = (problem as { errors?: FieldError[] })?.errors;
  return Array.isArray(ext) ? ext : [];
}

const invalidateApp = (
  qc: ReturnType<typeof useQueryClient>,
  tid: string | null | undefined,
  appId: string,
) =>
  Promise.all([
    qc.invalidateQueries({ queryKey: ["application", tid, appId] }),
    qc.invalidateQueries({ queryKey: ["applications", tid] }),
    qc.invalidateQueries({ queryKey: ["steps", tid, appId] }),
    qc.invalidateQueries({ queryKey: ["appAudit", tid, appId] }),
  ]);

/// Success shape for submit/correct — 422 instead returns RFC 9457
/// problem details whose `errors` extension carries FieldError[].
export interface SubmitResult {
  ok: boolean;
  version: number | null;
  errors: FieldError[];
}

/** Draft merge-patch — {patch: {path: value}} against the live draft. */
export function usePatchDraft(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (patch: Record<string, string>) =>
      apiPatch<SubmitResult>(`/api/tenants/${tid}/applications/${appId}`, { patch }),
    onSuccess: () => invalidateApp(qc, tid, appId),
  });
}

/** Submit a draft for validation — 422 carries FieldError[]. */
export function useSubmit(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      apiPost<SubmitResult>(`/api/tenants/${tid}/applications/${appId}/submit`, {}),
    onSuccess: () => invalidateApp(qc, tid, appId),
  });
}

/** Append-only correction on a submitted/in-review app — staff only. */
export function useCorrect(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (patch: Record<string, string>) =>
      apiPost<SubmitResult>(`/api/tenants/${tid}/applications/${appId}/correct`, { patch }),
    onSuccess: () => invalidateApp(qc, tid, appId),
  });
}

export interface RevealedApplication extends Omit<ApplicationView, "docJson"> {
  doc: Record<string, unknown>;
}

/** Audited PII reveal — staff-only endpoint; the reason is required
 *  and lands in the append-only audit chain. Returns the UNMASKED doc. */
export function useReveal(tid: string | null | undefined, appId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (reason: string) =>
      apiPost<RevealedApplication>(
        `/api/tenants/${tid}/applications/${appId}/reveal`,
        { reason },
      ),
    // The reveal itself writes an audit event — refresh the chain.
    onSuccess: () => invalidateApp(qc, tid, appId),
  });
}
