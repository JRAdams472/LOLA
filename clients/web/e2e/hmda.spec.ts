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
          "subject_property.state": "NC",
          "subject_property.usage": "principal_residence",
          "subject_property.construction_method": "site_built",
          "subject_property.census_tract": "1234.56",
          "subject_property.est_value": "375000",
          "subject_property.units": "1",
          loan_purpose: "purchase",
          "loan.product_type": "conventional",
          loan_amount: "300000",
          interest_rate: "6.5",
          loan_term_months: "360",
          "loan.cltv": "80",
          "demographics.ethnicity_1": "not_hispanic_or_latino",
          "demographics.race_1": "white",
          "demographics.sex": "female",
          "hmda.action_taken": "originated",
          "hmda.action_taken_date": "2026-04-15",
          "hmda.income": "96000",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

test("assemble → edits render → export downloads → append-only lineage", async ({
  page,
}) => {
  const id = await seedDraft(page);
  await page.goto(`/loans/${id}`);

  // —— Empty state.
  await page.getByRole("tab", { name: "HMDA" }).click();
  await expect(page.getByTestId("hmda-panel")).toBeVisible();
  await expect(page.getByTestId("hmda-empty")).toBeVisible();

  // —— Assemble.
  await page.getByTestId("assemble-btn").click();
  await expect(page.getByTestId("hmda-uli")).toBeVisible({ timeout: 15000 });
  const uliText = await page.getByTestId("hmda-uli").textContent();
  expect(uliText).toContain("10BX939C5543TQA1144M");

  // —— Figures: action taken formatted, loan amount, NA where absent.
  await expect(page.getByTestId("hmda-action")).toContainText("originated");
  await expect(page.getByTestId("hmda-amount")).toContainText("$300,000");
  // no ATR evaluation ran — spread + HOEPA must render NA, never invented
  await expect(page.getByTestId("hmda-spread")).toContainText("NA");
  await expect(page.getByTestId("hmda-hoepa")).toContainText("NA");

  // —— Edit tiers render with CFPB edit ids.
  await expect(page.getByTestId("tier-syntactical")).toBeVisible();
  await expect(page.getByTestId("tier-validity")).toBeVisible();
  await expect(page.getByTestId("tier-quality")).toBeVisible();
  await expect(page.getByTestId("edit-V608")).toBeVisible();

  // —— Lineage starts at 1.
  await expect(page.getByTestId("hmda-lineage")).toContainText("lineage: 1 row");

  // —— Export downloads pipe-delimited bytes with a valid LAR row.
  const year = new Date().getFullYear() >= 2026 ? 2026 : 2026;
  const exportRes = await page.request.get(
    `/bff/api/tenants/demo/hmda/export?year=${year}`,
  );
  expect(exportRes.ok()).toBeTruthy();
  const body = await exportRes.text();
  const lines = body.split("\n").filter((l) => l.length > 0);
  expect(lines.length).toBeGreaterThanOrEqual(2); // TS + at least one LAR
  expect(lines[0].startsWith("1|")).toBeTruthy(); // transmittal record
  const larLine = lines.find((l) => l.startsWith("2|") && l.includes(uliText!.trim()));
  expect(larLine).toBeTruthy();

  // —— Freeze snapshot.
  await page.getByTestId("freeze-btn").click();
  await page.waitForTimeout(1000);

  // —— Re-assemble → append-only lineage of 2, never overwrite.
  await page.getByTestId("assemble-btn").click();
  await expect(page.getByTestId("hmda-lineage")).toContainText("lineage: 2 rows", {
    timeout: 15000,
  });
});
