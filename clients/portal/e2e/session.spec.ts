import { expect, test } from "@playwright/test";

/**
 * M12 P5 session hardening — the idle bound is enforced server-side in
 * the sealed session, not by the client. This spec runs against a
 * second portal instance (:3301) launched with
 * PORTAL_SESSION_IDLE_MIN=0.05 (3s) — same secret, cookies are
 * port-agnostic on localhost.
 *
 *   PORTAL_SESSION_IDLE_MIN=0.05 npx next dev -p 3301
 *
 * The absolute bound rides the same `sessionMaxAgeS` cap (covered by
 * session-policy unit tests) — only the idle window is short enough
 * to exercise live.
 */

const IDLE_BASE = process.env.E2E_IDLE_BASE_URL ?? "http://localhost:3301";

test("idle session is rejected server-side after the window", async ({
  page,
}) => {
  // Fresh session on the idle-short instance via the stored cookies —
  // the sealed JWE carries lastActive; this instance evaluates it
  // against a 3s window. Slide it fresh through the main instance
  // first: suite ordering can leave the stored cookie older than the
  // window, which would 401 before the test even starts.
  const warm = await page.request.get(
    `${process.env.E2E_PORTAL_BASE_URL ?? "http://localhost:3300"}/auth/me`,
  );
  expect(warm.ok()).toBeTruthy();
  await page.goto(IDLE_BASE);
  await expect(
    page.getByRole("heading", { name: "Home" }),
  ).toBeVisible();

  // Under the window: the BFF still answers.
  const me1 = await page.request.get(`${IDLE_BASE}/auth/me`);
  expect(me1.ok()).toBeTruthy();

  // Past the window: the same session is rejected — a real 401 from
  // the JWE/session-policy check, not a client-side timeout (the
  // proxy middleware only checks cookie presence).
  await page.waitForTimeout(4_000);
  const res = await page.request.post(`${IDLE_BASE}/bff/graphql`, {
    data: { query: "{ __typename }" },
  });
  expect(res.status()).toBe(401);
});
