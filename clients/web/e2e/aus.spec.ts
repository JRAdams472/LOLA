import { expect, test, type Page } from "@playwright/test";

// Serial — shares the mutable demo tenant with the other specs.
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
          "party[0].first_name": "Grace",
          "party[0].last_name": "Hopper",
          "party[0].ssn": `5${Date.now().toString().slice(-8)}`,
          "party[0].dob": "1980-06-15",
          "party[0].address[0].street": "42 Main St",
          "party[0].address[0].city": "Springfield",
          "party[0].address[0].state": "IL",
          "party[0].address[0].zip": "62701",
          "subject_property.street": "1 Elm Ave",
          "subject_property.city": "Springfield",
          "subject_property.state": "IL",
          "subject_property.zip": "62702",
          loan_purpose: "purchase",
          loan_amount: "300000",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

test("submit → findings render → conditions auto-open → lineage → HMDA populated", async ({
  page,
}) => {
  const id = await seedDraft(page);
  await page.goto(`/loans/${id}`);

  // —— Empty state: clean, never invented values.
  await page.getByRole("tab", { name: "AUS" }).click();
  await expect(page.getByTestId("aus-panel")).toBeVisible();
  await expect(page.getByTestId("aus-empty")).toBeVisible();

  // —— Submit to DU — the stub adapter completes synchronously but the
  // outbox drain is async, so the chip is a wait-for, not immediate.
  await page.getByTestId("submit-du").click();
  await expect(page.getByTestId("rec-du")).toHaveText("approve-eligible", {
    timeout: 20000,
  });

  // —— Findings render with vendor codes + severities.
  await expect(page.getByTestId("finding-DU-1001")).toBeVisible();
  await expect(page.getByTestId("finding-DU-0420")).toBeVisible();

  // —— The required finding opened a blocking condition — banner shows.
  await expect(page.getByTestId("aus-blocking-banner")).toBeVisible();

  // —— Condition actually exists on the application (cross-surface check).
  const conditions = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/conditions`,
  );
  expect(conditions.ok()).toBeTruthy();
  const condBody = await conditions.json();
  const ausConditions = (condBody.conditions ?? condBody ?? []).filter(
    (c: { kind?: string }) => c.kind === "aus_finding",
  );
  expect(ausConditions.length).toBeGreaterThanOrEqual(1);

  // —— Re-submit: append-only lineage grows, never overwrites.
  await page.getByTestId("submit-du").click();
  await expect(page.getByTestId("aus-lineage")).toContainText(
    "2 submissions on record",
    { timeout: 20000 },
  );

  // —— HMDA cross-check: assemble the LAR and the AUS columns populate.
  const assemble = await page.request.post(
    `/bff/api/tenants/demo/applications/${id}/hmda/assemble`,
    { data: {} },
  );
  expect(assemble.ok()).toBeTruthy();
  const hmda = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/hmda`,
  );
  expect(hmda.ok()).toBeTruthy();
  const hmdaBody = await hmda.json();
  const fields = JSON.parse(hmdaBody.latest.fieldsJson);
  expect(fields.aus_1).not.toBe("NA");
  expect(fields.aus_result_1).not.toBe("NA");
});
