import { expect, test, type Page } from "@playwright/test";

// Serial — the tests share the mutable demo tenant.
test.describe.configure({ mode: "serial" });

async function seedDraft(page: Page, ssn: string) {
  const created = await page.request.post(
    "/bff/api/tenants/demo/applications",
    { data: {} },
  );
  expect(created.ok()).toBeTruthy();
  const { id } = await created.json();
  const patched = await page.request.patch(
    `/bff/api/tenants/demo/applications/${id}`,
    {
      data: {
        patch: {
          "party[0].role": "borrower",
          "party[0].first_name": "Ada",
          "party[0].last_name": "Lovelace",
          "party[0].ssn": ssn,
          "party[0].employment[0].monthly_income": "14200",
          "subject_property.street": "1 Prime Plaza",
          "subject_property.est_value": "560000",
          loan_amount: "425000",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

test("workspace renders the tabbed 1003 with masked PII and the pipeline row links in", async ({
  page,
}) => {
  const ssn = `6${Date.now().toString().slice(-8)}`;
  const id = await seedDraft(page, ssn);

  await page.goto("/");
  // Click THIS draft's row — the id prefix is unique; older Ada
  // Lovelace drafts from prior runs share the name.
  await page
    .getByRole("row", { name: new RegExp(id.slice(0, 8)) })
    .click();
  await expect(page).toHaveURL(new RegExp(`/loans/${id}$`));

  // Header: id, borrower, status, countdown, step.
  await expect(page.getByRole("heading", { name: id })).toBeVisible();
  await expect(page.getByText("Ada Lovelace").first()).toBeVisible();
  await expect(page.getByText("draft").first()).toBeVisible();
  await expect(page.getByText(/d left|due today|overdue/)).toBeVisible();

  // Tabs: five sections + Steps + staff Audit.
  for (const label of [
    "Borrower(s)",
    "Employment & Income",
    "Assets",
    "Liabilities",
    "Property & Loan",
    "Steps",
    "Audit",
  ]) {
    await expect(page.getByRole("tab", { name: label })).toBeVisible();
  }

  // SSN renders masked — the plaintext never reaches the DOM.
  await expect(page.getByText("•••").first()).toBeVisible();
  await expect(page.locator("body")).not.toContainText(ssn);
});

test("draft edit saves a merge-patch and submit 422s map onto fields", async ({
  page,
}) => {
  const id = await seedDraft(page, `5${Date.now().toString().slice(-8)}`);
  await page.goto(`/loans/${id}`);

  // Edit a field → dirty state → Save draft PATCHes the merge.
  await page.getByLabel("First name").fill("Grace");
  await page.getByRole("button", { name: /Save draft \(1\)/ }).click();
  await expect(
    page.getByRole("button", { name: "Save draft" }),
  ).toBeVisible();
  await expect(page.getByLabel("First name")).toHaveValue("Grace");

  // The draft is incomplete — submit returns 422 FieldError[] which
  // lands under the exact inputs (the view also jumps to the first
  // error's section).
  await page.getByRole("button", { name: "Submit" }).click();
  await expect(page.getByText(/fields? needs? attention/)).toBeVisible();
  await page.getByRole("tab", { name: "Borrower(s)" }).click();
  await expect(
    page.getByText("required field 'party[0].dob' is missing"),
  ).toBeVisible();
});

test("reveal unmasks PII behind a required reason, writes audit, and re-masks", async ({
  page,
}) => {
  const ssn = `4${Date.now().toString().slice(-8)}`;
  const id = await seedDraft(page, ssn);
  await page.goto(`/loans/${id}`);

  // Masked by default.
  await expect(page.getByText("•••").first()).toBeVisible();

  // The reason is required — Confirm stays disabled until filled.
  await page.getByRole("button", { name: "Reveal PII" }).click();
  const confirm = page.getByRole("button", { name: "Reveal", exact: true });
  await expect(confirm).toBeDisabled();
  await page.getByLabel(/Reason/).fill("borrower called about their application");
  await confirm.click();

  // Audited banner + plaintext now visible in the input.
  await expect(page.getByText(/Unmasked PII visible/)).toBeVisible();
  await expect(
    page.locator(`input[value="${ssn}"]`).first(),
  ).toBeVisible();

  // The reveal landed in the append-only audit chain.
  await page.getByRole("tab", { name: "Audit" }).click();
  await expect(
    page.getByRole("row", { name: /reveal/ }).first(),
  ).toBeVisible();

  // Re-mask restores the masked doc.
  await page.getByRole("button", { name: "Re-mask" }).click();
  await page.getByRole("tab", { name: "Borrower(s)" }).click();
  await expect(page.getByText("•••").first()).toBeVisible();
  await expect(page.locator("body")).not.toContainText(ssn);
});
