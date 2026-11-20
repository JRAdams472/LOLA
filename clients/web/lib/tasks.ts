"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiPost, gql } from "@/lib/api";

/** Officer work-queue row — PII-free by construction (M16): refs +
 *  generated titles + operational metadata only, never document fields
 *  or borrower attributes. Reads go through GraphQL (ADR-0011);
 *  claim/complete are REST transitions audited server-side. */
export interface QueueTask {
  seq: number;
  taskId: string;
  tenantId: string;
  applicationId: string;
  kind: string;
  title: string;
  priority: number;
  skillTags: string[];
  dueUtc: string | null;
  state: string;
  claimedBy: string | null;
  claimedUtc: string | null;
  completedUtc: string | null;
  createdUtc: string;
}

/** Priority ranks mirror TaskPriority — order matters for display. */
export const TASK_PRIORITY = { low: 10, normal: 30, high: 50, urgent: 80 } as const;

export const TASK_KIND_LABEL: Record<string, string> = {
  condition_blocking: "Blocking condition",
  step_review: "Step review",
  trid_deadline: "TRID deadline",
  stale_draft: "Stale draft",
};

const FIELDS = `taskId applicationId kind title priority skillTags
  dueUtc state claimedBy claimedUtc completedUtc createdUtc seq`;

export function useTasks(tid: string | null | undefined, skills?: string[]) {
  return useQuery({
    queryKey: ["tasks", tid, skills ?? null],
    enabled: !!tid,
    queryFn: () =>
      gql<{ tasks: QueueTask[] | null }>(
        `query Tasks($t: String!, $s: [String!]) {
          tasks(tenantId: $t, skills: $s) { ${FIELDS} }
        }`,
        { t: tid, s: skills ?? null },
      ).then((d) => d.tasks ?? []),
  });
}

export function useNextTask(tid: string | null | undefined) {
  return useQuery({
    queryKey: ["nextTask", tid],
    enabled: !!tid,
    queryFn: () =>
      gql<{ nextTask: QueueTask | null }>(
        `query NextTask($t: String!) {
          nextTask(tenantId: $t) { ${FIELDS} }
        }`,
        { t: tid },
      ).then((d) => d.nextTask),
  });
}

function useTransition(tid: string | null | undefined, verb: "claim" | "complete") {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (taskId: string) =>
      apiPost<QueueTask>(`/api/tenants/${tid}/tasks/${taskId}/${verb}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["tasks", tid] });
      qc.invalidateQueries({ queryKey: ["nextTask", tid] });
    },
  });
}

export const useClaimTask = (tid: string | null | undefined) =>
  useTransition(tid, "claim");
export const useCompleteTask = (tid: string | null | undefined) =>
  useTransition(tid, "complete");
