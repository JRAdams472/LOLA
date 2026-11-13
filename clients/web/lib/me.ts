"use client";

import { useQuery } from "@tanstack/react-query";

export interface Me {
  sub: string;
  name: string | null;
  tid: string | null;
  roles: string[];
}

/**
 * The authenticated principal — `/auth/me` proxies the API's
 * `/connect/userinfo` so roles always reflect server-side truth.
 * `isStaff` (TenantAdmin/LoanOfficer) gates staff surfaces; `isAdmin`
 * (TenantAdmin) gates admin mutations. While loading both are false
 * (affordances hide until identity resolves).
 */
export function useMe() {
  const q = useQuery<Me>({
    queryKey: ["me"],
    queryFn: async () => {
      const res = await fetch("/auth/me");
      if (res.status === 401) {
        // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- inside a React Query queryFn (no useRouter); /auth/login is a route handler, needs a document navigation
        window.location.assign("/auth/login");
        throw new Error("unauthenticated");
      }
      if (!res.ok) throw new Error(`/auth/me failed: ${res.status}`);
      return res.json() as Promise<Me>;
    },
    staleTime: 60_000,
  });
  const roles = q.data?.roles ?? [];
  const isStaff = roles.includes("TenantAdmin") || roles.includes("LoanOfficer");
  return {
    ...q,
    me: q.data,
    isStaff,
    isAdmin: roles.includes("TenantAdmin"),
    canReveal: isStaff,
  };
}
