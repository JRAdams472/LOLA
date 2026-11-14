"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiDelete, apiPost, apiPut, gql } from "@/lib/api";
import type { AuditEvent } from "@/lib/loans";

/// ——— Admin surfaces (MPE-211) ————————————————————————————————————
/// Reads ride GraphQL; mutations hit /api/admin/tenants/{id} REST.
/// Read policies admit staff; writes are TenantAdmin-only server-side.

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  status: string;
  roles: string[];
  mfaSecretRef: string | null;
}

export interface Group {
  id: string;
  name: string;
  description: string | null;
  roles: string[];
  memberCount: number;
}

const ADMIN = (t: string) => `/api/admin/tenants/${t}`;

export function useTenantUsers(tid: string | null | undefined) {
  return useQuery({
    queryKey: ["tenantUsers", tid],
    enabled: !!tid,
    queryFn: () =>
      gql<{ tenantUsers: AdminUser[] }>(
        `query Users($t: String!) {
          tenantUsers(tenantId: $t) {
            id email displayName status roles mfaSecretRef
          }
        }`,
        { t: tid },
      ).then((d) => d.tenantUsers),
  });
}

export function useTenantGroups(tid: string | null | undefined) {
  return useQuery({
    queryKey: ["tenantGroups", tid],
    enabled: !!tid,
    queryFn: () =>
      gql<{ tenantGroups: Group[] }>(
        `query Groups($t: String!) {
          tenantGroups(tenantId: $t) {
            id name description roles memberCount
          }
        }`,
        { t: tid },
      ).then((d) => d.tenantGroups),
  });
}

export function useGroupMembers(
  tid: string | null | undefined,
  groupId: string | null,
) {
  return useQuery({
    queryKey: ["groupMembers", tid, groupId],
    enabled: !!tid && !!groupId,
    queryFn: () =>
      gql<{ groupMembers: string[] }>(
        `query Members($t: String!, $g: String!) {
          groupMembers(tenantId: $t, groupId: $g)
        }`,
        { t: tid, g: groupId },
      ).then((d) => d.groupMembers),
  });
}

const invalidateAdmin = (qc: ReturnType<typeof useQueryClient>, tid: string | null | undefined) =>
  Promise.all([
    qc.invalidateQueries({ queryKey: ["tenantUsers", tid] }),
    qc.invalidateQueries({ queryKey: ["tenantGroups", tid] }),
    qc.invalidateQueries({ queryKey: ["groupMembers", tid] }),
  ]);

export function useCreateUser(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { email: string; displayName: string; password?: string }) =>
      apiPost(`${ADMIN(tid!)}/users`, vars),
    onSuccess: () => invalidateAdmin(qc, tid),
  });
}

export function useCreateGroup(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { name: string; description?: string; roles: string[] }) =>
      apiPost(`${ADMIN(tid!)}/groups`, vars),
    onSuccess: () => invalidateAdmin(qc, tid),
  });
}

export function useUpdateGroup(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { gid: string; name: string; description?: string; roles?: string[] }) =>
      apiPut(`${ADMIN(tid!)}/groups/${vars.gid}`, {
        name: vars.name,
        description: vars.description,
        roles: vars.roles,
      }),
    onSuccess: () => invalidateAdmin(qc, tid),
  });
}

export function useDeleteGroup(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (gid: string) => apiDelete(`${ADMIN(tid!)}/groups/${gid}`),
    onSuccess: () => invalidateAdmin(qc, tid),
  });
}

export function useAddMember(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { gid: string; userId: string }) =>
      apiPost(`${ADMIN(tid!)}/groups/${vars.gid}/members`, { userId: vars.userId }),
    onSuccess: () => invalidateAdmin(qc, tid),
  });
}

export function useRemoveMember(tid: string | null | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { gid: string; userId: string }) =>
      apiDelete(`${ADMIN(tid!)}/groups/${vars.gid}/members/${vars.userId}`),
    onSuccess: () => invalidateAdmin(qc, tid),
  });
}

export interface InviteResult {
  inviteId: string;
  inviteUrl: string;
  expiresUtc: string;
}

export function useCreateInvite(tid: string | null | undefined) {
  return useMutation({
    mutationFn: (email: string) =>
      apiPost<InviteResult>(`${ADMIN(tid!)}/invites`, { email }),
  });
}

export function useTenantAudit(
  tid: string | null | undefined,
  filters: { actor?: string; action?: string; since?: string },
  enabled = true,
) {
  return useQuery({
    queryKey: ["tenantAudit", tid, filters],
    enabled: !!tid && enabled,
    queryFn: () =>
      gql<{ tenantAudit: AuditEvent[] }>(
        `query TAudit($t: String!, $actor: String, $action: String, $since: String) {
          tenantAudit(tenantId: $t, actor: $actor, action: $action, since: $since) {
            seq eventId actor action entity entityId
            fieldClasses reason prevHash hash createdUtc
          }
        }`,
        {
          t: tid,
          actor: filters.actor || null,
          action: filters.action || null,
          since: filters.since || null,
        },
      ).then((d) => d.tenantAudit),
  });
}

export interface RetentionRow {
  appId: string;
  createdUtc: string;
  retentionUntilUtc: string;
}

export function useRetention(tid: string | null | undefined, enabled = true) {
  return useQuery({
    queryKey: ["retention", tid],
    enabled: !!tid && enabled,
    queryFn: () =>
      gql<{ retention: { applications: RetentionRow[]; earliestClearUtc: string | null } }>(
        `query Retention($t: String!) {
          retention(tenantId: $t) {
            applications { appId createdUtc retentionUntilUtc }
            earliestClearUtc
          }
        }`,
        { t: tid },
      ).then((d) => d.retention),
  });
}
