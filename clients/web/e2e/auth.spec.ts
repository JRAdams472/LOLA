import { expect, test } from "@playwright/test";

const EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";

/** The login flow itself — runs without a stored session. */
test("unauthenticated load bounces to login and back", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="org"]', "demo");
  await page.fill('input[name="email"]', EMAIL);
  await page.fill('input[name="password"]', PASSWORD);
  await page.click('button[type="submit"]');
  await expect(page.getByRole("heading", { name: "Pipeline" })).toBeVisible();
  // MPE-290: token custody is server-side — the browser carries ONE
  // opaque __Host- handle and no token material at all (a handle is
  // base64url: never a dotted JWT).
  const cookies = await page.context().cookies();
  const handle = cookies.find((c) => c.name === "__Host-lola_session");
  expect(handle, "opaque handle cookie").toBeTruthy();
  expect(handle!.httpOnly).toBe(true);
  expect(handle!.secure).toBe(true);
  expect(handle!.value).not.toMatch(/\./);
  expect(
    cookies.some((c) => c.name.startsWith("lola_session")),
    "no legacy token-bearing chunks remain",
  ).toBe(false);
});

/** Pre-cutover sealed-JWE cookies are never honored (MPE-290). */
test("legacy lola_session chunks do not authenticate", async ({ page }) => {
  await page.context().addCookies([
    {
      name: "lola_session.0",
      value: "eyJhbGciOiJkaXIiLCJlbmMiOiJBMjU2R0NNIn0.forged",
      url: "http://localhost:3200",
    },
    {
      name: "lola_session",
      value: "eyJhbGciOiJkaXIiLCJlbmMiOiJBMjU2R0NNIn0.forged",
      url: "http://localhost:3200",
    },
  ]);
  await page.goto("/");
  // No opaque handle → the proxy bounces to login despite the forged
  // legacy cookies.
  await expect(page).toHaveURL(/account\/login|auth\/login/);
});
