import "server-only";

import { createHash, randomBytes } from "crypto";
import { cookies } from "next/headers";
import { decodeJwt, EncryptJWT, jwtDecrypt } from "jose";

/**
 * Server-side auth for the BFF: auth-code+PKCE against the LOLA
 * OpenIddict issuer, tokens sealed into an httpOnly JWE cookie.
 * `OIDC_ISSUER` is the browser-facing issuer (login redirects);
 * `OIDC_TOKEN_URL` is where the server exchanges codes — they differ
 * inside compose (api service name vs localhost).
 */
const ISSUER = process.env.OIDC_ISSUER ?? "http://localhost:8200";
const TOKEN_URL = process.env.OIDC_TOKEN_URL ?? `${ISSUER}/connect/token`;
const AUTHORIZE_URL =
  process.env.OIDC_AUTHORIZE_URL ?? `${ISSUER}/connect/authorize`;
export const CLIENT_ID = process.env.OIDC_CLIENT_ID ?? "lola-officer-console";
export const REDIRECT_URI =
  process.env.OIDC_REDIRECT_URI ?? "http://localhost:3200/callback";
const SECURE = process.env.SESSION_SECURE === "1";
// No fallback: a compiled-in default would let anyone forge session JWEs.
// Evaluated lazily so `next build` (which imports this module) never throws.
const SECRET = process.env.SESSION_SECRET;

const SESSION_COOKIE = "lola_session";
/**
 * Browsers cap one cookie at ~4096 bytes and silently drop anything
 * larger; a sealed session holding an AT+RT pair exceeds that, which
 * loops the login (callback redirects to "/", the proxy sees no session).
 * The JWE is therefore chunked across `lola_session.N` cookies.
 */
const CHUNK_SIZE = 3800;
const CHUNK_RE = /^lola_session\.(\d+)$/;
const PKCE_COOKIE = "lola_pkce";
const STATE_COOKIE = "lola_state";
/** Refresh this far before the access-token exp, ms. */
const REFRESH_MARGIN = 30_000;
/** Hard ceiling on the session cookie's maxAge, seconds (default 8h — a
 *  shift; matches the API login-cookie lifetime). The cookie is further
 *  bounded by the refresh grant's own expiry when the IdP exposes it. */
const SESSION_MAX_AGE_S = Number(process.env.SESSION_MAX_AGE_S ?? 60 * 60 * 8);
/** How long a completed refresh outcome is replayed to requests still holding
 *  the pre-refresh cookie — covers the refresh-rotation straggler window. */
const RECENT_TTL = 60_000;
const RECENT_CAP = 512;

function sessionKey(): Buffer {
  if (!SECRET) {
    throw new Error(
      "SESSION_SECRET is required — set it in .env / clients/web/.env.local",
    );
  }
  return createHash("sha256").update(SECRET).digest();
}

export interface SessionData {
  /** access token — the bearer for API calls */
  at: string;
  /** refresh token, when the IdP issued one (offline_access) */
  rt?: string;
  /** access-token expiry, epoch ms */
  atExp: number;
  /** refresh-grant expiry, epoch ms — only when the IdP exposes it
   *  (`refresh_expires_in` or a JWT rt). Opaque rts leave this unset. */
  rtExp?: number;
  name?: string;
}

const cookieOpts = {
  httpOnly: true,
  sameSite: "lax" as const,
  secure: SECURE,
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

/** Exchange the callback code for tokens; returns session + post-login path. */
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
  const session = sessionFromTokens(tokens);
  await setSession(session);
  store.delete(PKCE_COOKIE);
  store.delete(STATE_COOKIE);
  return next || "/";
}

/** Refresh-grant expiry from the token response / rt itself, epoch ms —
 *  undefined when the IdP doesn't expose it. */
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

function sessionFromTokens(t: {
  access_token: string;
  refresh_token?: string;
  refresh_expires_in?: number;
  expires_in?: number;
  id_token?: string;
}): SessionData {
  let name: string | undefined;
  try {
    name = t.id_token ? (decodeJwt(t.id_token).name as string) : undefined;
  } catch {
    /* display name is a nicety, not a requirement */
  }
  return {
    at: t.access_token,
    rt: t.refresh_token,
    atExp: Date.now() + (t.expires_in ?? 3600) * 1000,
    rtExp: rtExpiryFrom(t, t.refresh_token),
    name,
  };
}

async function setSession(s: SessionData) {
  const sealed = await new EncryptJWT(s as unknown as Record<string, unknown>)
    .setProtectedHeader({ alg: "dir", enc: "A256GCM" })
    .setIssuedAt()
    .encrypt(sessionKey());
  // Bound the cookie to the refresh grant's own life when known — the
  // cookie must never promise a session that outlives its rt.
  const maxAge =
    s.rtExp !== undefined
      ? Math.max(
          1,
          Math.min(SESSION_MAX_AGE_S, Math.floor((s.rtExp - Date.now()) / 1000)),
        )
      : SESSION_MAX_AGE_S;
  const store = await cookies();
  const chunks = Math.ceil(sealed.length / CHUNK_SIZE);
  for (let i = 0; i < chunks; i++) {
    store.set(`${SESSION_COOKIE}.${i}`, sealed.slice(i * CHUNK_SIZE, (i + 1) * CHUNK_SIZE), {
      ...cookieOpts,
      maxAge,
    });
  }
  // Clear the legacy single-cookie name and any stale chunks a previous,
  // larger session left beyond the new count.
  store.delete(SESSION_COOKIE);
  for (const c of store.getAll()) {
    const m = CHUNK_RE.exec(c.name);
    if (m && Number(m[1]) >= chunks) store.delete(c.name);
  }
}

/** Reassemble the sealed session from the (possibly chunked) cookies. */
function readSessionCookie(store: {
  get(name: string): { value: string } | undefined;
  getAll(): { name: string; value: string }[];
}): string {
  const single = store.get(SESSION_COOKIE)?.value;
  if (single) return single;
  return store
    .getAll()
    .map((c) => ({ m: CHUNK_RE.exec(c.name), v: c.value }))
    .filter((p): p is { m: RegExpExecArray; v: string } => p.m !== null)
    .sort((a, b) => Number(a.m[1]) - Number(b.m[1]))
    .map((p) => p.v)
    .join("");
}

export async function clearSession() {
  const store = await cookies();
  store.delete(SESSION_COOKIE);
  for (const c of store.getAll()) {
    if (CHUNK_RE.test(c.name)) store.delete(c.name);
  }
}

/**
 * Refresh outcome, shared between the request that performed the refresh and
 * any stragglers holding the same pre-refresh cookie. The promise does only
 * the IdP call — cookie writes are applied per-request (each request gets
 * its own `cookies()` store), never inside the shared promise.
 */
type RefreshResult = { s: SessionData } | { s: null; revoked: boolean };

// In-process dedup: concurrent requests carrying the same sealed cookie share
// one refresh; `recent` replays the outcome to late arrivals for RECENT_TTL,
// so a request that missed the rotation doesn't resubmit the now-invalid rt
// and log the user out. Single-replica deployment — a shared store would be
// needed if web ever scales out.
const inflight = new Map<string, Promise<RefreshResult>>();
const recent = new Map<string, { r: RefreshResult; ts: number }>();

function pruneRecent() {
  const now = Date.now();
  for (const [k, v] of recent) {
    if (now - v.ts > RECENT_TTL) recent.delete(k);
  }
  while (recent.size > RECENT_CAP) {
    const oldest = recent.keys().next().value;
    if (oldest === undefined) break;
    recent.delete(oldest);
  }
}

/** The IdP round-trip — never throws; revocation vs. transient failure is
 *  encoded in the result so callers can clear (or keep) their own cookie. */
async function refreshOnce(s: SessionData): Promise<RefreshResult> {
  try {
    const res = await fetch(TOKEN_URL, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "refresh_token",
        client_id: CLIENT_ID,
        refresh_token: s.rt as string,
      }),
    });
    const tokens = await res.json().catch(() => ({}));
    if (!res.ok) {
      // invalid_grant = the refresh grant is definitively dead (revoked or
      // rotated past us); anything else is transient — cookie stays.
      return { s: null, revoked: tokens?.error === "invalid_grant" };
    }
    const rt = tokens.refresh_token ?? s.rt;
    return {
      s: {
        ...sessionFromTokens(tokens),
        rt,
        rtExp: rtExpiryFrom(tokens, rt) ?? s.rtExp,
        name: s.name,
      },
    };
  } catch {
    return { s: null, revoked: false };
  }
}

/** Apply a refresh outcome to THIS request's cookie store. */
async function applyRefreshResult(r: RefreshResult) {
  if (r.s) {
    await setSession(r.s);
    return r.s;
  }
  if (r.revoked) await clearSession();
  return null;
}

/**
 * Current session — refreshes the access token when close to expiry and a
 * refresh token exists. Returns null when there's no usable session.
 * Callable only from route handlers / server actions (it writes cookies).
 */
export async function getSession(): Promise<SessionData | null> {
  const raw = readSessionCookie(await cookies());
  if (!raw) return null;
  let s: SessionData;
  try {
    s = (await jwtDecrypt(raw, sessionKey())).payload as unknown as SessionData;
  } catch {
    return null;
  }
  if (Date.now() < s.atExp - REFRESH_MARGIN) return s;
  // The refresh grant itself is dead — the cookie can't promise more.
  if (s.rtExp !== undefined && Date.now() >= s.rtExp) {
    await clearSession();
    return null;
  }
  if (!s.rt) {
    await clearSession();
    return null;
  }

  pruneRecent();
  const hit = recent.get(raw);
  if (hit) return applyRefreshResult(hit.r);

  let pending = inflight.get(raw);
  if (!pending) {
    const p = refreshOnce(s);
    inflight.set(raw, p);
    // refreshOnce never throws, so this bookkeeping always runs.
    void p.then((r) => {
      inflight.delete(raw);
      recent.set(raw, { r, ts: Date.now() });
      pruneRecent();
    });
    pending = p;
  }
  return applyRefreshResult(await pending);
}
