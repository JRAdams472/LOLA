import { expect, test } from "@playwright/test";

const EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";

/** The login flow itself — runs without a stored session. */
test("unauthenticated load bounces to login and back", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="email"]', EMAIL);
  await page.fill('input[name="password"]', PASSWORD);
  await page.click('button[type="submit"]');
  await expect(page.getByRole("heading", { name: "Pipeline" })).toBeVisible();
  // The access token is never visible to the browser — only the sealed
  // httpOnly session cookie exists (chunked as lola_session.N when the JWE
  // exceeds the browser's per-cookie size cap).
  const cookies = await page.context().cookies();
  expect(
    cookies.some((c) => c.name.startsWith("lola_session") && c.httpOnly),
  ).toBe(true);
});
