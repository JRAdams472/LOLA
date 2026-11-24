import { expect, test, type Browser } from "@playwright/test";

const OFFICER_BASE = process.env.E2E_OFFICER_BASE_URL ?? "http://localhost:3200";
const OFFICER_EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const OFFICER_PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";
const TENANT = process.env.E2E_TENANT ?? "demo";

// Vendor-order vocabulary that must never reach the borrower wire or UI.
const VENDOR_VOCAB =
  /vendor|credit pull|bureaus|decisionScore|OFAC|flood zone|tri-merge|aus[_ ]engine/i;

const FRESH = { cookies: [], origins: [] };

/** Officer login on the console in its own context — explicit empty
 *  storageState: newContext() inherits the project's borrower
 *  storageState (incl. the IdP cookie), which would silently SSO the
 *  BORROWER into the console. */
async function officerContext(browser: Browser) {
  const context = await browser.newContext({ storageState: FRESH });
  const page = await context.newPage();
  await page.goto(OFFICER_BASE);
  await expect(page).toHaveURL(/account\/login/);
  await page.fill('input[name="org"]', "demo");
  await page.fill('input[name="email"]', OFFICER_EMAIL);
  await page.fill('input[name="password"]', OFFICER_PASSWORD);
  await page.click('button[type="submit"]');
  await expect(
    page.getByRole("heading", { name: "Pipeline" }),
  ).toBeVisible();
  return context;
}

/**
 * MPE-230 denial leg — an officer places a vendor order on the
 * borrower's OWN application; the borrower still sees nothing:
 * their wire payloads carry no vendor keys, their UI shows no vendor
 * vocabulary, and the staff order endpoints stay 403.
 */
test("borrower sees nothing when a vendor order exists on their loan", async ({
  page,
  browser,
}) => {
  // Borrower creates their own draft through the portal BFF.
  const created = await page.request.post(
    `/bff/api/tenants/${TENANT}/applications`,
    { data: {} },
  );
  expect(created.ok()).toBeTruthy();
  const { id: appId } = await created.json();

  // Officer places + completes a credit order on the borrower's app
  // (seeded fields — the credit mapper requires them).
  const officer = await officerContext(browser);
  try {
    const seeded = await officer.request.patch(
      `${OFFICER_BASE}/bff/api/tenants/${TENANT}/applications/${appId}`,
      {
        data: {
          patch: {
            "party[0].first_name": "E2E",
            "party[0].last_name": "Borrower",
            "party[0].ssn": `9${Date.now().toString().slice(-8)}`,
            "party[0].dob": "1992-07-15",
            "party[0].address[0].street": "7 Test Ave",
            "party[0].address[0].city": "Springfield",
            "party[0].address[0].state": "IL",
            "party[0].address[0].zip": "62701",
          },
        },
      },
    );
    expect(seeded.ok()).toBeTruthy();
    const placed = await officer.request.post(
      `${OFFICER_BASE}/bff/api/tenants/${TENANT}/applications/${appId}/vendor-orders`,
      { data: { kind: "credit" } },
    );
    expect(placed.status()).toBe(202);
    const ordersUrl = `${OFFICER_BASE}/bff/api/tenants/${TENANT}/applications/${appId}/vendor-orders`;
    await expect
      .poll(
        async () => {
          const res = await officer.request.get(ordersUrl);
          if (!res.ok()) return null;
          return (await res.json())[0]?.status ?? null;
        },
        { timeout: 60_000, intervals: [500, 1000, 2000] },
      )
      .toBe("completed");
  } finally {
    await officer.close();
  }

  // Borrower wire payloads: loans list + application detail carry no
  // vendor fields anywhere in the serialized JSON.
  const loans = await page.request.post("/bff/graphql", {
    data: {
      query:
        "query($t:String!){ portalLoans(tenantId:$t){ id statusStage } }",
      variables: { t: TENANT },
    },
  });
  expect(loans.ok()).toBeTruthy();
  expect(JSON.stringify(await loans.json())).not.toMatch(VENDOR_VOCAB);

  const detail = await page.request.get(
    `/bff/api/tenants/${TENANT}/applications/${appId}`,
  );
  expect(detail.ok()).toBeTruthy();
  expect(JSON.stringify(await detail.json())).not.toMatch(VENDOR_VOCAB);

  // The staff order endpoints deny the borrower outright.
  const denied = await page.request.post(
    `/bff/api/tenants/${TENANT}/applications/${appId}/vendor-orders`,
    { data: { kind: "voe" } },
  );
  expect(denied.status()).toBe(403);
  const deniedList = await page.request.get(
    `/bff/api/tenants/${TENANT}/applications/${appId}/vendor-orders`,
  );
  expect(deniedList.status()).toBe(403);

  // The borrower UI shows no vendor vocabulary on any surface.
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Home" })).toBeVisible();
  await expect(page.getByText(VENDOR_VOCAB)).toHaveCount(0);
});
