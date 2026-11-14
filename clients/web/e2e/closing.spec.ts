import { expect, test, type Page } from "@playwright/test";

// Serial — the tests share the mutable demo tenant.
test.describe.configure({ mode: "serial" });

async function seedDraft(page: Page) {
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
          "party[0].ssn": `7${Date.now().toString().slice(-8)}`,
          "party[0].employment[0].monthly_income": "14200",
          "subject_property.street": "1 Prime Plaza",
          "subject_property.city": "Springfield",
          "subject_property.state": "IL",
          "subject_property.county": "Sangamon",
          "subject_property.est_value": "560000",
          "subject_property.purchase_price": "560000",
          "subject_property.property_type": "single_family",
          "subject_property.usage": "primary_residence",
          "subject_property.units": "1",
          // TRID artifacts gate on loan.purpose and MPEx eligibility on
          // type/ltv/creditScore/occupancy/amortization/term — without
          // them the evals return unmatched fees and ineligible offers.
          loan_purpose: "purchase",
          "loan.product_type": "conventional",
          amortization_type: "fixed",
          loan_term_months: "360",
          "loan.ltv": "75.9",
          "party[0].credit_score": "760",
          loan_amount: "425000",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

test("closing workspace runs fees, override, pricing, lock and a package — all through the BFF", async ({
  page,
}) => {
  // No browser call may leave the BFF — MPEx/ISPPr/MinIO stay
  // server-side (ADR-0007). Watch every request the page issues.
  const leaks: string[] = [];
  page.on("request", (req) => {
    const u = req.url();
    if (/localhost:(8080|8090|8400|9200)/.test(u)) leaks.push(u);
  });

  const id = await seedDraft(page);
  await page.goto(`/loans/${id}/closing`);

  await expect(
    page.getByText("Closing Costs & Docs").first(),
  ).toBeVisible();
  await expect(page.getByText("Loan Estimate fees")).toBeVisible();
  await expect(page.getByText("Pricing & rate lock")).toBeVisible();
  await expect(page.getByText("Packages & documents")).toBeVisible();
  await expect(page.getByText("Borrower vault uploads")).toBeVisible();

  // Recalculate → 202 → the eval row resolves via the outbox worker.
  await page.getByRole("button", { name: "Recalculate" }).click();
  await expect(
    page.getByText(/Section total|Evaluation running|fee evaluation failed/).first(),
  ).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText("Section total").first()).toBeVisible({
    timeout: 30_000,
  });
  await expect(page.getByText(/A — Origination charges/).first()).toBeVisible();

  // Fee override — pencil opens the dialog; amount + required reason.
  await page
    .getByRole("button", { name: /^override / })
    .first()
    .click();
  const overrideDialog = page.getByRole("dialog", { name: /Fee override/ });
  await expect(overrideDialog).toBeVisible();
  // Reason is required — Apply stays disabled until typed.
  await expect(
    overrideDialog.getByRole("button", { name: "Apply" }),
  ).toBeDisabled();
  await overrideDialog
    .getByLabel("Override amount (empty = clear override)")
    .fill("100");
  await overrideDialog.getByLabel("Reason (required)").fill("e2e override");
  await overrideDialog.getByRole("button", { name: "Apply" }).click();
  await expect(overrideDialog).not.toBeVisible();
  await expect(page.getByText("$100.00").first()).toBeVisible();

  // Re-Price → 202 → offers render once the worker resolves the eval.
  await page.getByRole("button", { name: "Re-Price" }).click();
  const lockButton = page.getByRole("button", { name: /^lock / }).first();
  await expect(lockButton).toBeVisible({ timeout: 30_000 });

  // Lock the first offer → the active-lock alert renders.
  await lockButton.click();
  await expect(page.getByText(/expires/).first()).toBeVisible({
    timeout: 10_000,
  });

  // Unlock requires a reason.
  await page.getByRole("button", { name: "Unlock" }).click();
  const unlockDialog = page.getByRole("dialog", { name: "Unlock rate" });
  await expect(
    unlockDialog.getByRole("button", { name: "Unlock" }),
  ).toBeDisabled();
  await unlockDialog.getByLabel("Reason (required)").fill("e2e unlock");
  await unlockDialog.getByRole("button", { name: "Unlock" }).click();
  // The active-lock banner clears and offers' Lock buttons return.
  await expect(
    page.getByRole("button", { name: /^lock / }).first(),
  ).toBeVisible({ timeout: 10_000 });

  // Re-lock — an LE package requires an active rate lock.
  await page.getByRole("button", { name: /^lock / }).first().click();
  await expect(page.getByText(/expires/).first()).toBeVisible({
    timeout: 10_000,
  });

  // Generate a document package → the registry row appears.
  await page.getByRole("button", { name: "Generate" }).click();
  const pkgDialog = page.getByRole("dialog", {
    name: "Generate document package",
  });
  await pkgDialog.getByRole("button", { name: "Generate" }).click();
  await expect(
    page.getByRole("cell", { name: /le-package/ }).first(),
  ).toBeVisible({ timeout: 30_000 });

  expect(leaks).toEqual([]);
});

test("admin read surfaces render for a loan officer; mutations stay hidden", async ({
  page,
}) => {
  await page.goto("/admin");

  // Users tab — seeded officer + admin appear; write affordances are
  // TenantAdmin-gated and the server would 403 them anyway.
  await expect(page.getByRole("tab", { name: "Users" })).toBeVisible();
  await expect(page.getByText("officer@lola.dev")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Create user" }),
  ).not.toBeVisible();

  // Groups are readable, member-list opens, but no edit/delete/add.
  await page.getByRole("tab", { name: "Groups" }).click();
  await expect(page.getByRole("row", { name: /officers/ })).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Create group" }),
  ).not.toBeVisible();

  // Invites are TenantAdmin-only for a loan officer.
  await page.getByRole("tab", { name: "Borrower Invites" }).click();
  await expect(page.getByText("Invites are TenantAdmin-only")).toBeVisible();

  // Audit + retention are staff-readable.
  await page.getByRole("tab", { name: "Tenant Audit" }).click();
  await expect(page.getByRole("cell", { name: "write" }).first()).toBeVisible();
  await page.getByRole("tab", { name: "Retention" }).click();
  await expect(page.getByText("Earliest eligible clear:")).toBeVisible();
});

test.describe("tenant admin", () => {
  test.use({ storageState: "e2e/.auth/admin.json" });

  test("admin console manages users, groups, invites, audit and retention", async ({
    page,
  }) => {
    await page.goto("/admin");

    // Users tab — seeded officer + admin appear.
    await expect(page.getByRole("tab", { name: "Users" })).toBeVisible();
    await expect(page.getByText("officer@lola.dev")).toBeVisible();
    await expect(page.getByRole("button", { name: "Create user" })).toBeVisible();

  // Groups tab — the seeded officers group renders with member count.
  await page.getByRole("tab", { name: "Groups" }).click();
  const groupRow = page.getByRole("row", { name: /officers/ });
  await expect(groupRow).toBeVisible();
  await expect(page.getByRole("button", { name: "Create group" })).toBeVisible();

  // Membership dialog lists members and offers the add-member picker.
  await page
    .getByRole("button", { name: "officers", exact: true })
    .click();
  const membersDialog = page.getByRole("dialog", { name: /Members — officers/ });
  await expect(membersDialog).toBeVisible();
  await expect(membersDialog.getByText("officer@lola.dev")).toBeVisible();
  await membersDialog.getByRole("button", { name: "Close" }).click();

  // Create + delete a group round-trips through the admin API.
  const groupName = `e2e-${Date.now().toString().slice(-6)}`;
  await page.getByRole("button", { name: "Create group" }).click();
  const groupDialog = page.getByRole("dialog", { name: "Create group" });
  await groupDialog.getByLabel("Name").fill(groupName);
  await groupDialog
    .getByLabel("Role granted to members")
    .click();
  await page.getByRole("option", { name: "LoanOfficer" }).click();
  await groupDialog.getByRole("button", { name: "Save" }).click();
  await expect(page.getByRole("row", { name: new RegExp(groupName) })).toBeVisible();
  await page.getByRole("button", { name: `delete ${groupName}` }).click();
  await expect(
    page.getByRole("row", { name: new RegExp(groupName) }),
  ).not.toBeVisible();

  // Invites — TenantAdmin issues a one-time link.
  await page.getByRole("tab", { name: "Borrower Invites" }).click();
  await page.getByLabel("Borrower email").fill("borrower-e2e@example.test");
  await page.getByRole("button", { name: "Invite" }).click();
  await expect(page.getByText(/Invite created/).first()).toBeVisible();
  await expect(
    page.getByText(/\/invites\/accept\?token=/).first(),
  ).toBeVisible();

  // Tenant audit — the seeded draft writes already fill the chain;
  // the action filter narrows it.
  await page.getByRole("tab", { name: "Tenant Audit" }).click();
  await expect(page.getByText("Actor").first()).toBeVisible();
  await expect(
    page.getByRole("cell", { name: "write" }).first(),
  ).toBeVisible({ timeout: 10_000 });
  await page.getByLabel("Action").fill("reveal");
  await expect(page.getByRole("cell", { name: "write" })).toHaveCount(0);

  // Retention — the report lists per-app windows + earliest clear.
  await page.getByRole("tab", { name: "Retention" }).click();
  await expect(page.getByText("Earliest eligible clear:")).toBeVisible();
  await expect(
    page.getByRole("cell", { name: /^app-/ }).first(),
  ).toBeVisible();
  });
});
