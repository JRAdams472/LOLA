import "server-only";

/**
 * Server-side session store client (MPE-290): the API owns the OAuth
 * grant — this BFF sends an opaque handle and gets back a usable access
 * token; the refresh token never reaches Next.js.
 *
 * Calls authenticate as this BFF's own confidential client
 * (`BFF_CLIENT_ID`/`BFF_CLIENT_SECRET`, client_credentials + `lola-bff`
 * scope) — the minted `lola_bff_ns` claim pins this process to its
 * session namespace, so a foreign client's handle can never resolve
 * through it. The cc token is cached module-side until expiry.
 *
 * Failure model: transport errors and non-2xx both fail closed — a dead
 * store means "unauthenticated", never a token fallback.
 */

// Env is read lazily — module-load capture would freeze the values
// under jest's resetModules and in any host that injects env late.
const apiUrl = () => process.env.API_URL ?? "http://localhost:8200";
const tokenUrl = () =>
  process.env.OIDC_TOKEN_URL ?? `${apiUrl()}/connect/token`;

let cached: { at: string; expMs: number } | null = null;
let inflight: Promise<string> | null = null;

async function storeToken(): Promise<string> {
  const clientId = process.env.BFF_CLIENT_ID;
  const clientSecret = process.env.BFF_CLIENT_SECRET;
  if (!clientId || !clientSecret) {
    throw new Error(
      "BFF_CLIENT_ID/BFF_CLIENT_SECRET are required — the session " +
        "store only answers this BFF's confidential client",
    );
  }
  if (cached && Date.now() < cached.expMs - 30_000) return cached.at;
  // Concurrent first-callers share one token mint.
  inflight ??= (async () => {
    const res = await fetch(tokenUrl(), {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "client_credentials",
        client_id: clientId,
        client_secret: clientSecret,
        scope: "lola-bff",
      }),
      cache: "no-store",
    });
    if (!res.ok) throw new Error(`session-store token: ${res.status}`);
    const t = (await res.json()) as {
      access_token: string;
      expires_in?: number;
    };
    cached = {
      at: t.access_token,
      expMs: Date.now() + (t.expires_in ?? 3600) * 1000,
    };
    return cached.at;
  })().finally(() => {
    inflight = null;
  });
  return inflight;
}

/** Null on transport failure — callers treat it identically to a 401. */
async function call(
  path: string,
  init: RequestInit,
): Promise<Response | null> {
  try {
    return await fetch(`${apiUrl()}${path}`, {
      ...init,
      headers: {
        authorization: `Bearer ${await storeToken()}`,
        "content-type": "application/json",
      },
      cache: "no-store",
    });
  } catch {
    return null;
  }
}

export interface NewSession {
  handle: string;
  subject: string;
  tenantId: string;
  authVersion: number;
  accessToken: string;
  refreshToken?: string;
  name?: string;
  atExpMs: number;
  rtExpMs?: number;
  absExpMs: number;
  /** Sliding idle window for this namespace; console passes none. */
  idleMs?: number;
}

export interface ResolvedSession {
  /** The usable access token — the ONLY grant the BFF ever sees. */
  accessToken: string;
  atExpMs: number;
  subject: string;
  name?: string;
}

export async function createSession(s: NewSession): Promise<boolean> {
  const res = await call("/bff-sessions", {
    method: "POST",
    body: JSON.stringify(s),
  });
  return res?.ok === true;
}

/** Null on unknown/revoked/expired/wrong-namespace — or a dead store. */
export async function resolveSession(
  handle: string,
): Promise<ResolvedSession | null> {
  const res = await call("/bff-sessions/resolve", {
    method: "POST",
    body: JSON.stringify({ handle }),
  });
  if (!res?.ok) return null;
  const body = (await res.json()) as ResolvedSession;
  return body.accessToken ? body : null;
}

/** Best-effort revoke — the API also kills the upstream refresh grant. */
export async function revokeSession(handle: string): Promise<void> {
  await call(`/bff-sessions/${encodeURIComponent(handle)}`, {
    method: "DELETE",
  });
}
