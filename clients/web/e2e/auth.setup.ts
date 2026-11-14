import { expect, test as setup } from "@playwright/test";

const AUTH_FILE = "e2e/.auth/officer.json";
const ADMIN_FILE = "e2e/.auth/admin.json";
const EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";
const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? "admin@lola.dev";
const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? "lola-admin-dev";

/**
 * One real login through the OIDC flow (console → /connect/authorize →
 * API /account/login → /callback); every other spec reuses the sealed
 * session cookie via storageState instead of re-logging. Requires the
 * API to be running with DEV_SEED_* set (see .env.example).
 */
async function login(
  page: import("@playwright/test").Page,
  email: string,
  password: string,
  file: string,
) {
  await page.goto("/");
  // proxy.ts → /auth/login → authorize → API login form.
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="email"]', email);
  await page.fill('input[name="password"]', password);
  await page.click('button[type="submit"]');
  await expect(page.getByRole("heading", { name: "Pipeline" })).toBeVisible();
  await page.context().storageState({ path: file });
}

setup("authenticate as officer", async ({ page }) => {
  await login(page, EMAIL, PASSWORD, AUTH_FILE);
});

setup("authenticate as tenant admin", async ({ page }) => {
  await login(page, ADMIN_EMAIL, ADMIN_PASSWORD, ADMIN_FILE);
});
