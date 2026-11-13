import { expect, test as setup } from "@playwright/test";

const AUTH_FILE = "e2e/.auth/officer.json";
const EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";

/**
 * One real login through the OIDC flow (console → /connect/authorize →
 * API /account/login → /callback); every other spec reuses the sealed
 * session cookie via storageState instead of re-logging. Requires the
 * API to be running with DEV_SEED_* set (see .env.example).
 */
setup("authenticate as officer", async ({ page }) => {
  await page.goto("/");
  // proxy.ts → /auth/login → authorize → API login form.
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="email"]', EMAIL);
  await page.fill('input[name="password"]', PASSWORD);
  await page.click('button[type="submit"]');
  await expect(page.getByRole("heading", { name: "Pipeline" })).toBeVisible();
  await page.context().storageState({ path: AUTH_FILE });
});
