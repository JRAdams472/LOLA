import { expect, test } from "@playwright/test";

/** Auth-flow edges that must run without a stored session. */

test("unauthenticated page load bounces to the OIDC login", async ({
  page,
}) => {
  await page.goto("/");
  await expect(page).toHaveURL(/account\/login/);
});

test("invite accept with a bogus token surfaces an error", async ({
  page,
}) => {
  await page.goto("/invites/accept?token=not-a-real-token");
  await page.fill("#displayName", "Bogus User");
  await page.fill("#password", "a-long-enough-password");
  await page.fill("#confirm", "a-long-enough-password");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByRole("alert")).toBeVisible();
});
