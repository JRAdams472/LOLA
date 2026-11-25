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
          "party[0].employment[0].monthly_income": "15100",
          "party[0].credit_score": "772",
          "liability[0].liability_type": "installment",
          "liability[0].creditor": "NC State Employees CU",
          "liability[0].monthly_payment": "450",
          "subject_property.street": "9 Compiler Way",
          "subject_property.city": "Raleigh",
          "subject_property.state": "NC",
          "subject_property.county": "Wake",
          "subject_property.est_value": "540000",
          "subject_property.purchase_price": "540000",
          "subject_property.property_type": "single_family",
          "subject_property.usage": "primary_residence",
          "subject_property.units": "1",
          loan_purpose: "purchase",
          "loan.product_type": "conventional",
          "loan.lien_position": "first",
          amortization_type: "fixed",
          loan_term_months: "360",
          interest_rate: "6.5",
          loan_amount: "425000",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

type AtrReport = {
  latest: {
    dti: number | null;
    verdicts: { test: string; verdict: string }[];
  } | null;
  history: { resultId: string }[];
};

async function atr(page: Page, id: string): Promise<AtrReport> {
  const res = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/atr`,
  );
  expect(res.ok()).toBeTruthy();
  return res.json();
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

async function waitForFeeEval(page: Page, id: string) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    const latest = await latestFeeEval(page, id);
    if (latest?.status === "done") return latest;
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error("timed out waiting for a completed fee eval");
}

test("evaluate → verdicts render → fee eval → re-evaluate turns conclusive", async ({
  page,
}) => {
  const id = await seedDraft(page);
  await page.goto(`/loans/${id}`);

  // —— Empty state: no evaluation yet, staff sees the affordance.
  await page.getByRole("tab", { name: "ATR/QM" }).click();
  await expect(page.getByTestId("atr-panel")).toBeVisible();
  await expect(page.getByText(/No ATR\/QM evaluation yet/)).toBeVisible();

  // —— First evaluation, no completed fee eval → battery runs, the
  //    P&F-dependent tests warn rather than passing silently.
  await page.getByTestId("evaluate-atr-button").click();
  await expect(page.getByTestId("atr-test-qm_general")).toBeVisible({
    timeout: 15_000,
  });
  const first = await atr(page, id);
  expect(first.latest).not.toBeNull();
  expect(first.history).toHaveLength(1);
  // DTI is real: (P&I ≈ $2,686 + $450 debts) / $15,100 income ≈ 20.8%.
  expect(first.latest!.dti).toBeGreaterThan(19);
  expect(first.latest!.dti).toBeLessThan(23);
  const qmFirst = first.latest!.verdicts.find((v) => v.test === "qm_general");
  expect(qmFirst?.verdict).toBe("warn");

  // —— Produce a completed fee eval so points-and-fees is real.
  await page.goto(`/loans/${id}/closing`);
  await page.getByRole("button", { name: "Recalculate" }).click();
  await waitForFeeEval(page, id);

  // —— Re-evaluate: append-only second row; P&F-capped tests now have
  //    real inputs and turn conclusive.
  await page.goto(`/loans/${id}`);
  await page.getByRole("tab", { name: "ATR/QM" }).click();
  await expect(page.getByTestId("atr-panel")).toBeVisible();
  await page.getByTestId("evaluate-atr-button").click();
  await expect(page.getByText(/2 evaluations recorded/)).toBeVisible({
    timeout: 15_000,
  });

  const second = await atr(page, id);
  expect(second.history).toHaveLength(2);
  const qmSecond = second.latest!.verdicts.find(
    (v) => v.test === "qm_general",
  );
  expect(["pass", "fail"]).toContain(qmSecond?.verdict);
  // Figures render in the panel.
  await expect(page.getByTestId("atr-figures")).toContainText("DTI");
  await expect(page.getByTestId("atr-figures")).toContainText("APOR");
});
