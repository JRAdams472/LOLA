import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";

/**
 * M12 P3 wizard on the real stack. The borrower drives /apply through
 * the same BFF the UI uses — draft create + PATCH ride the sealed
 * session, so coverage includes the atomic-merge save path end to end.
 */

async function createDraft(page: import("@playwright/test").Page) {
  const me = await page.request.get("/auth/me");
  const { tid } = await me.json();
  const created = await page.request.post(
    `/bff/api/tenants/${tid}/applications`,
    { data: {} },
  );
  expect(created.ok()).toBeTruthy();
  const { id } = await created.json();
  return { tid, id };
}

function patch(
  page: import("@playwright/test").Page,
  tid: string,
  appId: string,
  fields: Record<string, string>,
) {
  return page.request.fetch(
    `/bff/api/tenants/${tid}/applications/${appId}`,
    { method: "PATCH", data: { patch: fields } },
  );
}

const REQUIRED: Record<string, string> = {
  "loan_amount": "425000",
  "loan_purpose": "purchase",
  "amortization_type": "fixed",
  "loan_term_months": "360",
  "party[0].first_name": "Marina",
  "party[0].last_name": "E2ESample",
  "party[0].ssn": "000123456",
  "party[0].dob": "1988-04-12",
  "party[0].email": "marina@example.test",
  "party[0].phone": "5550100123",
  "party[0].role": "borrower",
  "party[0].address[0].street": "1 Test Way",
  "party[0].address[0].city": "Springfield",
  "party[0].address[0].state": "IL",
  "party[0].address[0].zip": "62701",
  "party[0].employment[0].employer_name": "Example Works",
  "party[0].employment[0].start_date": "2020-06-01",
  "party[0].employment[0].self_employed": "false",
  "party[0].employment[0].monthly_income": "8200",
  "asset[0].asset_type": "checking",
  "asset[0].institution": "First Test Bank",
  "asset[0].account_number": "000111222",
  "asset[0].balance": "45000",
  "liability[0].liability_type": "auto",
  "liability[0].creditor": "Test Auto Finance",
  "liability[0].monthly_payment": "310",
  "liability[0].balance": "9000",
  "declaration.intent_to_occupy": "yes",
  "declaration.first_time_buyer": "true",
  "declaration.property_will_be": "primary_residence",
  "subject_property.street": "9 Demo Lane",
  "subject_property.city": "Springfield",
  "subject_property.state": "IL",
  "subject_property.zip": "62702",
  "subject_property.est_value": "560000",
  "subject_property.property_type": "single_family",
  "subject_property.usage": "primary_residence",
  "demographics.ethnicity": "not_provided",
  "demographics.race": "not_provided",
  "demographics.sex": "not_provided",
  "loan.base_amount": "425000",
  "loan.ltv": "75.89",
  "loan.lien_position": "first",
  "loan.product_type": "conventional",
};

test("wizard saves, shows masked resume, maps 422s, and submits",
  async ({ page }) => {
    const { tid, id } = await createDraft(page);
    // Seed PII through the wire so resume has a masked confirm to show.
    const seed = await patch(page, tid, id, {
      "party[0].ssn": "000123456",
      "party[0].first_name": "Marina",
    });
    expect(seed.ok()).toBeTruthy();

    await page.goto("/apply");
    // Masked-confirm: saved PII renders •••+last4 — even the borrower's
    // own name is masked-confirm under the zero-grant visibility role.
    await expect(page.getByText("•••3456")).toBeVisible();
    await expect(page.getByText("•••rina")).toBeVisible();
    await expect(page.getByText("000123456")).toHaveCount(0);

    // Fill a field + navigate — the draft saves on nav; after reload the
    // PII field resumes in masked-confirm form.
    await page.getByLabel("Last Name *").fill("E2ESample");
    // The nav save is awaited in-app but the click resolves first — wait
    // for the PATCH to land before reloading or the fetch is aborted.
    const navSaved = page.waitForResponse(
      (r) => r.request().method() === "PATCH"
        && r.url().includes(`/applications/${id}`),
    );
    await page.getByRole("button", { name: "Employment & income" }).click();
    await navSaved;
    await page.reload();
    await expect(page.getByText("•••mple")).toBeVisible();

    // Review → submit an incomplete draft → 422 maps onto the wizard.
    await page.getByRole("button", { name: "Review" }).click();
    await page.getByRole("button", { name: "Submit application" }).click();
    // Deep-link lands on the first error section (Property & loan —
    // loan_amount is the first catalog field still missing).
    await expect(
      page.getByRole("button", { name: "Property & loan" }),
    ).toHaveAttribute("aria-current", "step");
    await expect(page.getByRole("alert").first()).toBeVisible();

    // Complete the catalog and submit for real.
    const fill = await patch(page, tid, id, REQUIRED);
    expect(fill.ok()).toBeTruthy();
    await page.reload();
    await page.getByRole("button", { name: "Review" }).click();
    await page.getByRole("button", { name: "Submit application" }).click();
    await expect(
      page.getByText("Application submitted"),
    ).toBeVisible();

    // The tracker advanced — no draft vocabulary left.
    await page.getByRole("link", { name: "Track your application" }).click();
    await expect(page.getByText("Under review").first()).toBeVisible();
  });

test("partial draft survives logout and login", async ({ page }) => {
  const { tid, id } = await createDraft(page);
  const save = await patch(page, tid, id, {
    "party[0].first_name": "Persisted",
    "party[0].ssn": "000123456",
  });
  expect(save.ok()).toBeTruthy();

  // Log out, sign back in through the real OIDC flow, resume.
  const creds = JSON.parse(
    readFileSync("e2e/.auth/creds.json", "utf8"),
  ) as { email: string; password: string };
  await page.goto("/auth/logout");
  await page.goto("/auth/login");
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="org"]', "demo");
  await page.fill('input[name="email"]', creds.email);
  await page.fill('input[name="password"]', creds.password);
  await page.click('button[type="submit"]');
  await expect(page.getByRole("heading", { name: "Home" })).toBeVisible();

  await page.goto("/apply");
  await expect(page.getByText("•••sted")).toBeVisible();
  await expect(page.getByText("•••3456")).toBeVisible();
});
