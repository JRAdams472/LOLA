"use client";

import { useEffect, useState } from "react";

export interface Me {
  sub: string;
  name: string | null;
  tid: string | null;
  roles: string[];
}

type MeState =
  | { status: "loading" }
  | { status: "error"; error: string }
  | { status: "ready"; me: Me };

/**
 * The authenticated principal — `/auth/me` proxies the API's
 * `/connect/userinfo`. A 401 means the sealed session is gone (idle or
 * absolute expiry) — bounce through the login flow. No react-query here:
 * the portal keeps its client surface small and fetches are one-shot.
 */
export function useMe(): MeState {
  const [state, setState] = useState<MeState>({ status: "loading" });
  useEffect(() => {
    const ctrl = new AbortController();
    fetch("/auth/me", { signal: ctrl.signal })
      .then(async (res) => {
        if (res.status === 401) {
          // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- inside a fetch callback (no useRouter); /auth/login is a route handler, needs a document navigation
          window.location.assign("/auth/login");
          return;
        }
        if (!res.ok) {
          setState({ status: "error", error: `/auth/me: ${res.status}` });
          return;
        }
        setState({ status: "ready", me: await res.json() });
      })
      .catch((e) => {
        if (!ctrl.signal.aborted)
          setState({ status: "error", error: `${e}` });
      });
    return () => ctrl.abort();
  }, []);
  return state;
}
