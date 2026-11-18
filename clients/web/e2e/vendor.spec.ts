import { expect, test } from "@playwright/test";

// Serial — the tests share the mutable demo tenant.
test.describe.configure({ mode: "serial" });

const SHOTS = "shots/vendor";

/**
 * MPE-229: the officer Steps tab surfaces vendor orders — kind,
 * status chip, and the normalized summary — next to the pipeline
 * events. Runs on the real compose stack: the order is placed over
 * REST, the VendorOutboxWorker drains it (stub adapter completes on
 * the first poll), and the tab renders the completed row.
 */
test("officer sees a completed vendor order with summary on the Steps tab", async ({
  page,
}) => {
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
          "party[0].first_name": "Ada",
          "party[0].last_name": "Lovelace",
          "party[0].ssn": `7${Date.now().toString().slice(-8)}`,
          "party[0].dob": "1985-04-12",
          "party[0].address[0].street": "42 Main St",
          "party[0].address[0].city": "Springfield",
          "party[0].address[0].state": "IL",
          "party[0].address[0].zip": "62701",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();

  const placed = await page.request.post(
    `/bff/api/tenants/demo/applications/${id}/vendor-orders`,
    { data: { kind: "credit" } },
  );
  expect(placed.status()).toBe(202);

  // The outbox worker drains asynchronously — poll the staff list
  // read until the stub adapter completes the order.
  const ordersUrl =
    `/bff/api/tenants/demo/applications/${id}/vendor-orders`;
  await expect
    .poll(
      async () => {
        const res = await page.request.get(ordersUrl);
        if (!res.ok()) return null;
        const rows = await res.json();
        return rows[0]?.status ?? null;
      },
      { timeout: 30_000, intervals: [500, 1000, 2000] },
    )
    .toBe("completed");

  await page.goto(`/loans/${id}`);
  await page.getByRole("tab", { name: "Steps" }).click();

  // Vendor order row: kind, status chip, normalized summary.
  const row = page.getByRole("row", { name: /credit/ });
  await expect(row).toBeVisible();
  await expect(row.getByText("completed")).toBeVisible();
  await expect(row).toContainText(/score \d+ · \d+ bureaus/);

  // The pipeline events list still carries the vendor step events.
  await expect(
    page.getByText(/Credit · completed/).first(),
  ).toBeVisible();
  await page.screenshot({ path: `${SHOTS}/01-steps-tab.png` });
});
