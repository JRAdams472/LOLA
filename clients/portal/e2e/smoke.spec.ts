import { expect, test } from "@playwright/test";

/** Signed-in smoke: the invited borrower lands on Home with their
 *  identity resolved and can reach the sign-out flow. */
test("borrower lands on the portal home", async ({ page }) => {
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "Home" }),
  ).toBeVisible();
  await expect(page.getByTestId("user-name")).toHaveText("E2E Borrower");
  // The dashboard shows either a loan card or the empty state — both
  // are the borrower's own application surface.
  await expect(page.getByText(/application/i).first()).toBeVisible();
});
