import { writeFileSync } from "node:fs";
import { expect, test as setup } from "@playwright/test";

const AUTH_FILE = "e2e/.auth/borrower.json";
const CREDS_FILE = "e2e/.auth/creds.json";
const OFFICER_BASE = process.env.E2E_OFFICER_BASE_URL ?? "http://localhost:3200";
const OFFICER_EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const OFFICER_PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";
const TENANT = process.env.E2E_TENANT ?? "demo";
const BORROWER_EMAIL =
  process.env.E2E_BORROWER_EMAIL ??
  `borrower-${Date.now().toString(36)}@lola.dev`;
const BORROWER_PASSWORD =
  process.env.E2E_BORROWER_PASSWORD ?? "borrower-dev-password-12";

/**
 * The full invite path, once: officer logs into the console and issues an
 * invite → borrower accepts (sets name + password) → borrower logs in
 * through the OIDC flow. Every other spec reuses the session handle via
 * storageState. Requires the dev stack (API + officer console) with
 * DEV_SEED_* set.
 */
setup("invite + authenticate as borrower", async ({ page, browser }) => {
  // 1. Officer login on the console — reuse its session to call
  //    the admin invite endpoint through the console BFF. This MUST be a
  //    separate browser context: the IdP cookie is shared per-origin, so
  //    an officer session in the same context would silently SSO into
  //    the portal and skip the borrower's password step.
  const officerContext = await browser.newContext();
  const officer = await officerContext.newPage();
  await officer.goto(OFFICER_BASE);
  await expect(officer).toHaveURL(/account\/login/);
  await officer.fill('input[name="org"]', "demo");
  await officer.fill('input[name="email"]', OFFICER_EMAIL);
  await officer.fill('input[name="password"]', OFFICER_PASSWORD);
  await officer.click('button[type="submit"]');
  await expect(
    officer.getByRole("heading", { name: "Pipeline" }),
  ).toBeVisible();

  const invite = await officer.request.post(
    `${OFFICER_BASE}/bff/api/admin/tenants/${TENANT}/invites`,
    { data: { email: BORROWER_EMAIL } },
  );
  expect(invite.status()).toBe(201);
  const { inviteUrl } = await invite.json();
  await officerContext.close();

  // 2. Accept the invite on the portal — sets display name + password.
  await page.goto(inviteUrl);
  await expect(
    page.getByRole("heading", { name: "Set up your account" }),
  ).toBeVisible();
  await page.fill("#displayName", "E2E Borrower");
  await page.fill("#password", BORROWER_PASSWORD);
  await page.fill("#confirm", BORROWER_PASSWORD);
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(
    page.getByRole("heading", { name: "Account created" }),
  ).toBeVisible();

  // 3. Sign in through the real OIDC flow.
  await page.getByRole("link", { name: "Sign in" }).click();
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="org"]', "demo");
  await page.fill('input[name="email"]', BORROWER_EMAIL);
  await page.fill('input[name="password"]', BORROWER_PASSWORD);
  await page.click('button[type="submit"]');
  await expect(
    page.getByRole("heading", { name: "Home" }),
  ).toBeVisible();
  await page.context().storageState({ path: AUTH_FILE });
  // Specs that exercise logout→login need the borrower's credentials —
  // the email is generated per run, so persist it alongside the state.
  writeFileSync(
    CREDS_FILE,
    JSON.stringify({ email: BORROWER_EMAIL, password: BORROWER_PASSWORD }),
  );
});
