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
          "party[0].ssn": `6${Date.now().toString().slice(-8)}`,
          "party[0].employment[0].monthly_income": "15100",
          "party[0].credit_score": "772",
          "subject_property.street": "9 Compiler Way",
          "subject_property.city": "Arlington",
          "subject_property.state": "VA",
          "subject_property.county": "Arlington",
          "subject_property.est_value": "540000",
          "subject_property.purchase_price": "540000",
          "subject_property.property_type": "single_family",
          "subject_property.usage": "primary_residence",
          "subject_property.units": "1",
          loan_purpose: "purchase",
          "loan.product_type": "conventional",
          amortization_type: "fixed",
          loan_term_months: "360",
          "loan.ltv": "78.7",
          loan_amount: "425000",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

type ToleranceReport = {
  versions: { versionId: string; seq: number }[];
  violations: { bucket: string; feeCode: string | null }[];
  openCureConditionId: string | null;
};

async function tolerance(page: Page, id: string): Promise<ToleranceReport> {
  const res = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/tolerance`,
  );
  expect(res.ok()).toBeTruthy();
  return res.json();
}

async function waitForTolerance(
  page: Page,
  id: string,
  pred: (r: ToleranceReport) => boolean,
  timeoutMs = 30_000,
) {
  const deadline = Date.now() + timeoutMs;
  let last: ToleranceReport | null = null;
  while (Date.now() < deadline) {
    last = await tolerance(page, id);
    if (pred(last)) return last;
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`tolerance predicate timed out; last=${JSON.stringify(last)}`);
}

async function latestFeeEval(page: Page, id: string) {
  const res = await page.request.post("/bff/graphql", {
    data: {
      query:
        "query($t:String!,$id:String!){fees(tenantId:$t,appId:$id)" +
        "{evals{evalId status}}}",
      variables: { t: "demo", id },
    },
  });
  const { data } = await res.json();
  return (data?.fees?.evals ?? []).at(-1) as
    | { evalId: string; status: string }
    | undefined;
}

// Each fee POST lands as its own batch — the evals surface only shows
// the latest, so the wait is for a *new* evalId resolving to done.
async function waitForNewEval(
  page: Page,
  id: string,
  previousEvalId: string | undefined,
) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    const latest = await latestFeeEval(page, id);
    if (
      latest &&
      latest.evalId !== previousEvalId &&
      latest.status === "done"
    ) {
      return latest;
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`timed out waiting for a new completed fee eval`);
}

test("LE baseline → revised disclosure → 0% violation → cure → cleared", async ({
  page,
}) => {
  const id = await seedDraft(page);
  await page.goto(`/loans/${id}/closing`);

  // —— Baseline: fees → price → lock → LE package → version 1 bound.
  await page.getByRole("button", { name: "Recalculate" }).click();
  await expect(page.getByText("Section total").first()).toBeVisible({
    timeout: 30_000,
  });

  await page.getByRole("button", { name: "Re-Price" }).click();
  const lockButton = page.getByRole("button", { name: /^lock / }).first();
  await expect(lockButton).toBeVisible({ timeout: 30_000 });
  await lockButton.click();
  await expect(page.getByText(/expires/).first()).toBeVisible({
    timeout: 10_000,
  });

  await page.getByRole("button", { name: "Generate" }).click();
  await page
    .getByRole("dialog", { name: "Generate document package" })
    .getByRole("button", { name: "Generate" })
    .click();
  await expect(
    page.getByRole("cell", { name: /le-package/ }).first(),
  ).toBeVisible({ timeout: 30_000 });

  // Version 1 binds at enqueue; evaluation runs when the doc completes.
  await waitForTolerance(page, id, (r) => r.versions.length === 1);
  await waitForTolerance(
    page,
    id,
    (r) => r.versions.length === 1 && r.violations.length === 0,
  );
  // No violations yet — the panel shows only once bound.
  await expect(page.getByTestId("tolerance-panel")).toBeVisible();

  // —— Changed circumstance: borrower opts into discount points → a new
  //    0%-bucket fee (discount-points = 0.25% of the loan amount) appears
  //    on the revised LE — any increase on a 0% line is a violation.
  const patched = await page.request.patch(
    `/bff/api/tenants/demo/applications/${id}`,
    { data: { patch: { "declaration.discount_points": "true" } } },
  );
  expect(patched.ok()).toBeTruthy();

  const baselineEval = await latestFeeEval(page, id);
  await page.getByRole("button", { name: "Recalculate" }).click();
  await waitForNewEval(page, id, baselineEval?.evalId);

  // —— Re-issue the LE → version 2 → tolerance evaluation flags the
  //    zero-bucket increase and opens the cure condition.
  await page.getByRole("button", { name: "Generate" }).click();
  await page
    .getByRole("dialog", { name: "Generate document package" })
    .getByRole("button", { name: "Generate" })
    .click();
  await expect(
    page.getByRole("cell", { name: /le-package/ }).nth(1),
  ).toBeVisible({ timeout: 30_000 });

  await waitForTolerance(
    page,
    id,
    (r) =>
      r.versions.length === 2 &&
      r.violations.length > 0 &&
      r.openCureConditionId != null,
  );

  await page.reload();
  const banner = page.getByTestId("tolerance-violation-banner");
  await expect(banner).toBeVisible({ timeout: 15_000 });
  await expect(banner).toContainText("discount-points");
  await expect(page.getByTestId("tolerance-delta-Zero")).toContainText(
    "+$1,062.50",
  );

  // —— Cure: staff records the reason → condition closes → banner clears.
  await page.getByTestId("record-cure-button").click();
  const cureDialog = page.getByRole("dialog", {
    name: "Record tolerance cure",
  });
  await cureDialog.getByLabel("Reason").fill("cured at consummation");
  await cureDialog.getByRole("button", { name: "Record" }).click();
  await expect(cureDialog).not.toBeVisible();

  await waitForTolerance(page, id, (r) => r.openCureConditionId == null);
  await page.reload();
  await expect(
    page.getByTestId("tolerance-violation-banner"),
  ).not.toBeVisible();
});
