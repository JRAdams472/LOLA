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
          "subject_property.state": "NC",
          loan_purpose: "purchase",
          // Escrow posture: $3,600 tax + $1,200 hazard = $4,800/yr,
          // $400/mo payment, $800 balance → exactly within cushion.
          "escrow.current_balance": "800",
          "escrow.monthly_payment": "400",
          "escrow.annual_property_tax": "3600",
          "escrow.annual_hazard_insurance": "1200",
          // Section 8 input: an affiliated settlement provider.
          "settlement_provider[0].service_type": "title",
          "settlement_provider[0].provider_name": "Affiliated Title Co",
          "settlement_provider[0].affiliated_with_lender": "true",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();
  return id as string;
}

type RespaReport = {
  latestAnalysis: {
    verdict: string;
    annualDisbursements: number | null;
    refundRequired: boolean;
  } | null;
  analysisHistory: { analysisId: string }[];
  afbaDisclosureRequired: boolean;
  notices: {
    noticeId: string;
    noticeKind: string;
    status: string;
    overdue: boolean;
    sendBy: string;
  }[];
};

async function respa(page: Page, id: string): Promise<RespaReport> {
  const res = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/respa`,
  );
  expect(res.ok()).toBeTruthy();
  return res.json();
}

test("analyze → verdict renders → transfer lifecycle → append-only lineage", async ({
  page,
}) => {
  const id = await seedDraft(page);
  await page.goto(`/loans/${id}`);

  // —— Empty state: no analysis yet, staff sees the affordance.
  await page.getByRole("tab", { name: "RESPA" }).click();
  await expect(page.getByTestId("respa-panel")).toBeVisible();
  await expect(page.getByTestId("escrow-empty")).toBeVisible();
  await expect(page.getByTestId("notices-empty")).toBeVisible();

  // —— AfBA banner: the seeded provider is affiliated with the lender.
  await expect(page.getByTestId("afba-banner")).toBeVisible();

  // —— Analyze: the seeded fixture lands exactly within cushion.
  await page.getByTestId("analyze-escrow-button").click();
  await expect(page.getByTestId("escrow-verdict")).toBeVisible({
    timeout: 15_000,
  });
  await expect(page.getByTestId("escrow-verdict")).toHaveText(
    /within cushion/,
  );
  await expect(page.getByTestId("escrow-figures")).toContainText("$4,800.00");
  await expect(page.getByTestId("escrow-figures")).toContainText("$800.00");

  const first = await respa(page, id);
  expect(first.latestAnalysis?.verdict).toBe("within");
  expect(first.afbaDisclosureRequired).toBe(true);
  expect(first.analysisHistory).toHaveLength(1);

  // —— Record a transfer effective long past: both statutory notices
  //    land pending AND already overdue (deadlines in the past).
  await page.getByTestId("record-transfer-button").click();
  await page.getByTestId("transfer-effective").fill("2020-01-15");
  await page.getByTestId("transfer-transferor").fill("Old Servicer LLC");
  await page.getByTestId("transfer-transferee").fill("New Servicer Inc");
  await page.getByTestId("transfer-submit").click();

  await expect(page.getByTestId("overdue-banner")).toBeVisible({
    timeout: 15_000,
  });
  const withNotices = await respa(page, id);
  expect(withNotices.notices).toHaveLength(2);
  const transferor = withNotices.notices.find(
    (n) => n.noticeKind === "transferor",
  )!;
  const transferee = withNotices.notices.find(
    (n) => n.noticeKind === "transferee",
  )!;
  // Statutory deadlines: transferor effective−15d, transferee +15d.
  expect(transferor.sendBy).toBe("2019-12-31");
  expect(transferee.sendBy).toBe("2020-01-30");
  expect(transferor.overdue).toBe(true);
  expect(transferee.overdue).toBe(true);

  // —— The overdue sweep opened blocking conditions (deduped per notice).
  const conditions = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/conditions`,
  );
  expect(conditions.ok()).toBeTruthy();
  const condBody = await conditions.json();
  const servicing = (condBody as { kind: string; state: string }[]).filter(
    (c) => c.kind === "servicing_notice" && c.state === "open",
  );
  expect(servicing).toHaveLength(2);

  // —— Mark the transferor notice sent: overdue clears for that one,
  //    sent-late is detected (deadline was years ago).
  await page.getByTestId(`mark-sent-${transferor.noticeId}`).click();
  await expect(
    page.getByTestId(`notice-status-${transferor.noticeId}`),
  ).toHaveText("sent", { timeout: 15_000 });

  const afterSent = await respa(page, id);
  const sentNotice = afterSent.notices.find(
    (n) => n.noticeId === transferor.noticeId,
  )!;
  expect(sentNotice.status).toBe("sent");
  expect(sentNotice.overdue).toBe(false);

  // —— Mark received: full lifecycle completes.
  await page.getByTestId(`mark-received-${transferor.noticeId}`).click();
  await expect(
    page.getByTestId(`notice-status-${transferor.noticeId}`),
  ).toHaveText("received", { timeout: 15_000 });

  // —— Append-only lineage: a second analysis lands a second row.
  await page.getByTestId("analyze-escrow-button").click();
  await expect(page.getByText(/2 analyses recorded/)).toBeVisible({
    timeout: 15_000,
  });
  const final = await respa(page, id);
  expect(final.analysisHistory).toHaveLength(2);
});
