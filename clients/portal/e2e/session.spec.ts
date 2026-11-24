import { expect, test } from "@playwright/test";

/**
 * MPE-290 session custody — the idle/absolute bounds live in the API's
 * session store (bound per-session at create), the browser carries only
 * an opaque `__Host-` handle. These specs seed real store sessions
 * through the store API itself (a client-credentials token for
 * `lola-bff-portal` pins the portal namespace), so the idle bound can
 * be exercised live without a second BFF instance.
 *
 * Chromium refuses to inject `__Host-` cookies over http (the prefix
 * rules require Secure), so each request carries the handle as an
 * explicit Cookie header in a fresh context — the BFF reads it
 * identically. A dead session is the one the BFF clears: `getSession`
 * deletes the handle, which surfaces as a `__Host-lola_portal` tombstone
 * in Set-Cookie. The seeded AT is a placeholder — resolve succeeds but
 * upstream calls 401 — which is precisely what lets us tell "session
 * alive" (no tombstone) from "session dead" (tombstone emitted).
 */

const PORTAL_BASE = process.env.E2E_PORTAL_BASE_URL ?? "http://localhost:3300";
const API_BASE = process.env.E2E_API_URL ?? "http://localhost:8200";
const BFF_SECRET = process.env.E2E_BFF_PORTAL_SECRET ?? "lola-bff-portal-dev";
const HANDLE = "__Host-lola_portal";

interface Identity {
  sub: string;
  tid: string;
  lola_auth_ver: string;
}

/** The borrower identity from the REAL session in the page's auth state. */
async function meIdentity(request: import("@playwright/test").APIRequestContext) {
  const me = await request.get(`${PORTAL_BASE}/auth/me`);
  expect(me.ok()).toBeTruthy();
  return (await me.json()) as Identity;
}

async function storeToken(request: import("@playwright/test").APIRequestContext) {
  const res = await request.post(`${API_BASE}/connect/token`, {
    form: {
      grant_type: "client_credentials",
      client_id: "lola-bff-portal",
      client_secret: BFF_SECRET,
      scope: "lola-bff",
    },
  });
  expect(res.ok()).toBeTruthy();
  return ((await res.json()) as { access_token: string }).access_token;
}

async function seedSession(
  request: import("@playwright/test").APIRequestContext,
  id: Identity,
  idleMs: number,
) {
  const token = await storeToken(request);
  const handle = `e2e-${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
  const now = Date.now();
  const res = await request.post(`${API_BASE}/bff-sessions`, {
    headers: { authorization: `Bearer ${token}` },
    data: {
      handle,
      subject: id.sub,
      tenantId: id.tid,
      authVersion: Number(id.lola_auth_ver),
      accessToken: "e2e-dead-at",
      atExpMs: now + 3_600_000,
      absExpMs: now + 3_600_000,
      idleMs,
    },
  });
  expect(res.status()).toBe(201);
  return handle;
}

const withHandle = (handle: string) => ({
  headers: { cookie: `${HANDLE}=${handle}` },
});

/** True when the response clears the handle — a dead-resolve tombstone. */
const cleared = (res: import("@playwright/test").APIResponse) =>
  (res.headers()["set-cookie"] ?? "").includes(`${HANDLE}=`);

test("idle session is rejected server-side after the window", async ({
  page,
  browser,
}) => {
  const id = await meIdentity(page.request);
  // A 3s-idle session bound to the same borrower identity.
  const handle = await seedSession(page.request, id, 3_000);

  // Fresh context — the request carries ONLY the seeded handle.
  const ctx = await browser.newContext();
  const me = () =>
    ctx.request.get(`${PORTAL_BASE}/auth/me`, withHandle(handle));

  // Under the window the session RESOLVES — /auth/me 401s only because
  // the placeholder AT can't call userinfo; no tombstone is emitted,
  // which is the proof the store vouched for the handle.
  const alive = await me();
  expect(alive.status()).toBe(401);
  expect(cleared(alive), "live resolve must not clear the handle").toBe(
    false,
  );

  // Past the window the resolve itself denies — the BFF tombstones the
  // handle rather than let a dead cookie linger.
  await new Promise((r) => setTimeout(r, 4_000));
  const dead = await me();
  expect(dead.status()).toBe(401);
  expect(cleared(dead), "dead resolve clears the handle").toBe(true);
  await ctx.close();
});

test("logout revokes the server-side session — a replayed handle is dead", async ({
  page,
  browser,
}) => {
  const id = await meIdentity(page.request);
  const handle = await seedSession(page.request, id, 30 * 60_000);

  const ctx = await browser.newContext();

  // Sign out through the real route — it DELETEs the store session
  // (killing the upstream grant) then clears the browser cookie.
  const out = await ctx.request.get(
    `${PORTAL_BASE}/auth/logout`,
    withHandle(handle),
  );
  expect([200, 302]).toContain(out.status());

  // Replay the exact handle — under cookie custody this request would
  // still authenticate; under server-side custody the row is revoked
  // and the handle is tombstoned.
  const replay = await ctx.request.get(
    `${PORTAL_BASE}/auth/me`,
    withHandle(handle),
  );
  expect(replay.status()).toBe(401);
  expect(cleared(replay), "revoked handle must be tombstoned").toBe(true);
  await ctx.close();
});
