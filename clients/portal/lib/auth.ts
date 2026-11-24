import "server-only";

import { createHash, randomBytes } from "crypto";
import { cookies } from "next/headers";
import { decodeJwt } from "jose";

import { ABSOLUTE_MS, IDLE_MS } from "@/lib/session-policy";
import { assertSessionEnv, isStrictSessionEnv } from "@/lib/session-env";
import {
  HANDLE_COOKIE,
  clearLegacyCookies,
} from "@/lib/session-cookies";
import {
  createSession,
  resolveSession,
  revokeSession,
} from "@/lib/session-store";

/**
 * Server-side auth for the borrower portal's BFF (MPE-290): auth-code+PKCE
 * against the LOLA OpenIddict issuer, but the browser carries ONLY an
 * opaque `__Host-` handle — token custody lives in the API's encrypted
 * session store. Refresh rotation happens API-side under the store's
 * single-flight; this module holds no refresh machinery at all.
 *
 * Session hardening vs the officer console (M12): the portal's 30m
 * sliding idle window and 4h absolute bound are passed to the store at
 * create (`IDLE_MS`/`ABSOLUTE_MS` from session-policy.ts) and enforced
 * server-side — resolve slides idle on live traffic and can never
 * extend the absolute bound.
 */
const ISSUER = process.env.OIDC_ISSUER ?? "http://localhost:8200";
const TOKEN_URL = process.env.OIDC_TOKEN_URL ?? `${ISSUER}/connect/token`;
const AUTHORIZE_URL =
  process.env.OIDC_AUTHORIZE_URL ?? `${ISSUER}/connect/authorize`;
export const CLIENT_ID = process.env.OIDC_CLIENT_ID ?? "lola-borrower-portal";
export const REDIRECT_URI =
  process.env.OIDC_REDIRECT_URI ?? "http://localhost:3300/callback";
const SECURE = process.env.SESSION_SECURE === "1" || isStrictSessionEnv();

const PKCE_COOKIE = "lola_portal_pkce";
const STATE_COOKIE = "lola_portal_state";

export interface SessionData {
  /** access token — the bearer for API calls, from store resolve */
  at: string;
  subject: string;
  name?: string;
}

const cookieOpts = {
  httpOnly: true,
  sameSite: "lax" as const,
  secure: SECURE,
  path: "/",
};

/** `__Host-` rules: Secure is mandatory — always on, even in dev (the
 *  localhost exception covers the dev/e2e http stack). No Domain and
 *  Path=/ are the other two requirements. */
const handleOpts = {
  httpOnly: true,
  sameSite: "lax" as const,
  secure: true,
  path: "/",
};

function newPkce() {
  const verifier = randomBytes(32).toString("base64url");
  const challenge = createHash("sha256").update(verifier).digest("base64url");
  const state = randomBytes(16).toString("hex");
  return { verifier, challenge, state };
}

/** Build the authorize URL and remember PKCE + state (+ post-login target). */
export async function beginLogin(next: string) {
  assertSessionEnv();
  const { verifier, challenge, state } = newPkce();
  const store = await cookies();
  store.set(PKCE_COOKIE, verifier, { ...cookieOpts, maxAge: 300 });
  store.set(STATE_COOKIE, `${state}|${next}`, { ...cookieOpts, maxAge: 300 });

  const url = new URL(AUTHORIZE_URL);
  url.searchParams.set("client_id", CLIENT_ID);
  url.searchParams.set("redirect_uri", REDIRECT_URI);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("scope", "openid profile offline_access lola-api");
  url.searchParams.set("state", state);
  url.searchParams.set("code_challenge", challenge);
  url.searchParams.set("code_challenge_method", "S256");
  return url.toString();
}

/** Refresh-grant expiry from the token response / rt itself, epoch ms —
 *  forwarded to the store so it can bound the session's grant life. */
function rtExpiryFrom(
  t: { refresh_expires_in?: number },
  rt: string | undefined,
): number | undefined {
  if (typeof t.refresh_expires_in === "number") {
    return Date.now() + t.refresh_expires_in * 1000;
  }
  if (rt) {
    try {
      const exp = decodeJwt(rt).exp;
      if (exp) return exp * 1000;
    } catch {
      /* opaque rt — expiry not determinable client-side */
    }
  }
  return undefined;
}

/** Exchange the callback code for tokens, hand the grant to the store,
 *  and set the opaque handle cookie; returns the post-login path. */
export async function finishLogin(code: string, state: string | null) {
  const store = await cookies();
  const verifier = store.get(PKCE_COOKIE)?.value;
  const [expected, next] = (store.get(STATE_COOKIE)?.value ?? "|").split("|");
  if (!verifier || !state || state !== expected) {
    throw new Error("bad oauth state");
  }

  const res = await fetch(TOKEN_URL, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      client_id: CLIENT_ID,
      redirect_uri: REDIRECT_URI,
      code,
      code_verifier: verifier,
    }),
  });
  const tokens = await res.json();
  if (!res.ok) {
    throw new Error(`token exchange failed: ${tokens.error ?? res.status}`);
  }

  // The id_token carries the identity the store records — subject,
  // tenant, and the phase-6 authority version resolve revalidates.
  const claims = decodeJwt(tokens.id_token) as {
    sub?: string;
    tid?: string;
    lola_auth_ver?: string;
    name?: string;
  };
  const authVersion = Number(claims.lola_auth_ver);
  if (!claims.sub || !claims.tid || !authVersion) {
    throw new Error("login grant missing identity claims");
  }

  const handle = randomBytes(32).toString("base64url");
  const now = Date.now();
  const created = await createSession({
    handle,
    subject: claims.sub,
    tenantId: claims.tid,
    authVersion,
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token,
    name: claims.name,
    atExpMs: now + (tokens.expires_in ?? 3600) * 1000,
    rtExpMs: rtExpiryFrom(tokens, tokens.refresh_token),
    // Portal bounds: 30m sliding idle + 4h absolute — the store
    // enforces both; the cookie only mirrors the absolute cap.
    absExpMs: now + ABSOLUTE_MS,
    idleMs: IDLE_MS,
  });
  if (!created) {
    throw new Error("session store unavailable");
  }
  store.set(HANDLE_COOKIE, handle, {
    ...handleOpts,
    maxAge: Math.ceil(ABSOLUTE_MS / 1000),
  });
  store.delete(PKCE_COOKIE);
  store.delete(STATE_COOKIE);
  clearLegacyCookies(store);
  return next || "/";
}

/**
 * Current session — resolves the opaque handle against the API's
 * session store. The store enforces the portal bounds (absolute, then
 * idle) and slides `last_active` on the resolve itself — a live call
 * IS activity — so no client-side touch bookkeeping remains. Null when
 * there's no usable session. Callable only from route handlers /
 * server actions (it writes cookies).
 */
export async function getSession(): Promise<SessionData | null> {
  assertSessionEnv();
  const store = await cookies();
  const handle = store.get(HANDLE_COOKIE)?.value;
  if (!handle) {
    clearLegacyCookies(store);
    return null;
  }
  const s = await resolveSession(handle);
  if (!s) {
    // Dead handle — revoked, expired, foreign namespace, or a store
    // that can't vouch for it. The handle is worthless either way.
    store.delete(HANDLE_COOKIE);
    clearLegacyCookies(store);
    return null;
  }
  clearLegacyCookies(store);
  return { at: s.accessToken, subject: s.subject, name: s.name };
}

/**
 * Full sign-out: revoke the server-side session (which also kills the
 * upstream refresh grant — impossible under cookie custody) THEN drop
 * the browser cookies. The handle alone is worthless after this.
 */
export async function endSession() {
  const store = await cookies();
  const handle = store.get(HANDLE_COOKIE)?.value;
  if (handle) await revokeSession(handle);
  store.delete(HANDLE_COOKIE);
  clearLegacyCookies(store);
}
