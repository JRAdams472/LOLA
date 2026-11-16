import { expect, test } from "@playwright/test";

/**
 * M12 P2 dashboard on the real stack: the borrower creates a draft
 * through the portal BFF (own-draft attribution is automatic), patches
 * a name, and the tracker renders borrower vocabulary — never internal
 * pipeline steps — with PII masked.
 */
test("dashboard shows own loan with friendly status and masked PII",
  async ({ page }) => {
    await page.goto("/");

    // Own draft via the same BFF the UI uses — cookie session attaches
    // the token; the app attributes to the borrower's sub.
    const me = await page.request.get("/auth/me");
    expect(me.ok()).toBeTruthy();
    const { tid } = await me.json();
    const created = await page.request.post(
      `/bff/api/tenants/${tid}/applications`,
      { data: {} },
    );
    expect(created.ok()).toBeTruthy();
    const { id: appId } = await created.json();
    const patch = await page.request.fetch(
      `/bff/api/tenants/${tid}/applications/${appId}`,
      {
        method: "PATCH",
        data: {
          patch: {
            "party[0].role": "borrower",
            "party[0].first_name": "Marina",
            "party[0].last_name": "E2ESample",
            "party[0].ssn": "000123456",
          },
        },
      },
    );
    expect(patch.ok()).toBeTruthy();

    await page.reload();
    await expect(
      page.getByText("Application in progress"),
    ).toBeVisible();

    // Borrower vocabulary — never the internal step names.
    await expect(page.getByText("Under review")).toBeVisible();
    await expect(page.getByText("Decision")).toBeVisible();
    await expect(page.getByText(/POSIntake|PPE/)).toHaveCount(0);

    // Masked-confirm: the name renders masked; the SSN never appears.
    await expect(page.getByText(/•••/)).toBeVisible();
    await expect(page.getByText(/000123456|123456/)).toHaveCount(0);

    await expect(
      page.getByText("Finish and submit your application."),
    ).toBeVisible();
  });
