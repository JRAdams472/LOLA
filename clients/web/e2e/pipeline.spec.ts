import { expect, test, type Page } from "@playwright/test";

// Serial — the tests share the mutable demo tenant, so row counts
// would race under parallel workers.
test.describe.configure({ mode: "serial" });

/** Seeds a TRID-complete draft through the BFF — the page's request
 *  context shares the session cookie, and the /bff route attaches the
 *  bearer server-side. A unique SSN per run keeps searches unambiguous
 *  in the shared demo tenant. */
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

test("pipeline lists the seeded draft with borrower, status, and TRID countdown", async ({
  page,
}) => {
  const ssn = `9${Date.now().toString().slice(-8)}`;
  await seedDraft(page, ssn);

  await page.goto("/");
  // Prior runs leave "Ada Lovelace" drafts in the demo tenant — the
  // seeded row is the first (soonest deadline sorts to the top).
  const row = page.getByRole("row", { name: /Ada Lovelace/ }).first();
  await expect(row).toBeVisible();
  await expect(row.getByText("draft")).toBeVisible();
  // The six-field patch stamped the clock — a live countdown renders.
  await expect(row.getByText(/d left|due today|overdue/)).toBeVisible();
});

test("new loan dialog creates a draft that appears in the pipeline", async ({
  page,
}) => {
  // Guarantee at least one real row so the empty-state row can't
  // distort the count.
  await seedDraft(page, `7${Date.now().toString().slice(-8)}`);
  await page.goto("/");
  // Wait for real data — skeleton rows are <tr> too, so anchor on the
  // seeded borrower's name cell.
  await expect(
    page.getByRole("row", { name: /Ada Lovelace/ }).first(),
  ).toBeVisible();
  await page.getByRole("button", { name: "New loan" }).click();
  await page.getByRole("button", { name: "Create draft" }).click();
  // Creating a draft lands in its loan workspace.
  await expect(page).toHaveURL(/\/loans\/[a-z0-9-]+$/);
  await expect(page.getByText("draft").first()).toBeVisible();
  // Back on the pipeline a row for THIS new draft exists.
  const newId = page.url().split("/").pop()!;
  await page.goto("/");
  // The Application column truncates to the id prefix.
  await expect(
    page.getByRole("row", { name: new RegExp(newId.slice(0, 8)) }),
  ).toBeVisible();
});

test("search finds the draft by SSN — the term never reaches the URL", async ({
  page,
}) => {
  const ssn = `8${Date.now().toString().slice(-8)}`;
  await seedDraft(page, ssn);

  await page.goto("/search");
  await page.getByLabel("Field").click();
  await page.getByRole("option", { name: "SSN" }).click();
  await page.getByLabel("Value").fill(ssn);
  await page.getByRole("button", { name: "Search" }).click();

  const row = page.getByRole("row", { name: /Ada Lovelace/ }).first();
  await expect(row).toBeVisible();
  // Invariant: the search term is a POST body — never URL/history.
  expect(page.url()).not.toContain(ssn);
});
