import { createHmac } from "node:crypto";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, test } from "@playwright/test";

// Serial — the tests share the mutable demo tenant and one application.
test.describe.configure({ mode: "serial" });

const SHOTS = "shots/vendor-marquee";
const API_BASE = process.env.E2E_API_BASE_URL ?? "http://localhost:8200";
const OPENBAO_ADDR = process.env.E2E_OPENBAO_ADDR ?? "http://localhost:8300";

const KINDS = ["credit", "voe", "voi", "flood", "fraud", "aus"] as const;

interface VendorOrderRow {
  orderId: string;
  kind: string;
  status: string;
  summary: string | null;
  externalId: string | null;
}

/**
 * MPE-230 marquee — the vendor seam end-to-end on the real compose
 * stack: one application, all six stub services ordered → the outbox
 * worker drains → completions land (poll channel by default; the stub
 * self-posts signed webhooks when VENDOR_STUB_WEBHOOK_URL is set) →
 * encrypted results normalize into PII-free summaries → the officer
 * Steps tab renders all six rows.
 *
 * The webhook ingress is exercised directly with a spec-signed payload
 * (secret read from the dev OpenBao KV) to prove the live HMAC + replay
 * path regardless of which channel completed the orders.
 */
test("marquee — all six vendor services complete and render on the Steps tab", async ({
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
          "party[0].email": "ada@example.test",
          "party[0].phone": "5550100999",
          "party[0].address[0].street": "42 Main St",
          "party[0].address[0].city": "Springfield",
          "party[0].address[0].state": "IL",
          "party[0].address[0].zip": "62701",
          "party[0].employment[0].employer_name": "Example Works",
          "party[0].employment[0].monthly_income": "8200",
          "loan_amount": "425000",
          "loan_purpose": "purchase",
          // Run-unique: the stub's external id is a request-hash — an
          // identical payload across runs collides with any orphaned
          // submitted order and the webhook resolves Ambiguous.
          "subject_property.street": `9 Demo Lane ${Date.now().toString(36)}`,
          "subject_property.city": "Springfield",
          "subject_property.state": "IL",
          "subject_property.zip": "62702",
        },
      },
    },
  );
  expect(patched.ok()).toBeTruthy();

  // Officer orders every stub service on the one application.
  for (const kind of KINDS) {
    const placed = await page.request.post(
      `/bff/api/tenants/demo/applications/${id}/vendor-orders`,
      { data: { kind } },
    );
    expect(placed.status()).toBe(202);
  }
  await page.goto(`/loans/${id}`);
  await page.getByRole("tab", { name: "Steps" }).click();
  await page.screenshot({ path: `${SHOTS}/01-ordered.png` });

  // The worker drains asynchronously — wait until all six complete.
  const ordersUrl = `/bff/api/tenants/demo/applications/${id}/vendor-orders`;
  let orders: VendorOrderRow[] = [];
  await expect
    .poll(
      async () => {
        const res = await page.request.get(ordersUrl);
        if (!res.ok()) return null;
        orders = (await res.json()) as VendorOrderRow[];
        return orders.filter((o) => o.status === "completed").length;
      },
      { timeout: 60_000, intervals: [500, 1000, 2000] },
    )
    .toBe(KINDS.length);
  await page.screenshot({ path: `${SHOTS}/02-completed.png` });

  // Normalized, PII-free summaries per kind.
  const byKind = new Map(orders.map((o) => [o.kind, o]));
  expect(byKind.get("credit")?.summary).toMatch(
    /score \d+ · \d+ bureaus/,
  );
  expect(byKind.get("voe")?.summary).toMatch(/employment \w+ · \d+mo/);
  expect(byKind.get("voi")?.summary).toMatch(/\$[\d,]+\/yr/);
  expect(byKind.get("flood")?.summary).toMatch(/zone \w+/);
  expect(byKind.get("fraud")?.summary).toMatch(/OFAC (match|clear)/);
  expect(byKind.get("aus")?.summary).toMatch(/· \w+ risk/);

  // Steps tab renders every order with a status chip + summary.
  await page.reload();
  await page.getByRole("tab", { name: "Steps" }).click();
  for (const kind of KINDS) {
    // Row accessible name is "kind status summary …" — anchor to the
    // leading cell so /aus/ can't collide with "bureaus" in the
    // credit summary.
    const row = page.getByRole("row", {
      name: new RegExp(`^${kind}\\b`),
    });
    await expect(row).toBeVisible();
    await expect(row.getByText("completed")).toBeVisible();
  }
  await page.screenshot({ path: `${SHOTS}/03-steps-tab.png`, fullPage: true });

  // The pipeline events list carries the vendor step events too.
  for (const kind of ["Credit", "Voe", "Flood", "Fraud", "Aus"]) {
    await expect(
      page.getByText(new RegExp(`${kind} · completed`)).first(),
    ).toBeVisible();
  }
});

test("webhook ingress — spec-signed replay is idempotent, bad signature rejected", async ({
  page,
}) => {
  // Fresh application + one completed order to replay against.
  const created = await page.request.post(
    "/bff/api/tenants/demo/applications",
    { data: {} },
  );
  const { id } = await created.json();
  await page.request.patch(`/bff/api/tenants/demo/applications/${id}`, {
    data: {
      patch: {
        "party[0].first_name": "Ada",
        "party[0].last_name": "Lovelace",
        "party[0].ssn": `8${Date.now().toString().slice(-8)}`,
        "party[0].dob": "1985-04-12",
      },
    },
  });
  const placed = await page.request.post(
    `/bff/api/tenants/demo/applications/${id}/vendor-orders`,
    { data: { kind: "fraud" } },
  );
  expect(placed.status()).toBe(202);
  const ordersUrl = `/bff/api/tenants/demo/applications/${id}/vendor-orders`;
  let externalId: string | null = null;
  await expect
    .poll(
      async () => {
        const res = await page.request.get(ordersUrl);
        if (!res.ok()) return null;
        const rows = (await res.json()) as VendorOrderRow[];
        externalId = rows[0]?.externalId ?? null;
        return rows[0]?.status ?? null;
      },
      { timeout: 60_000, intervals: [500, 1000, 2000] },
    )
    .toBe("completed");
  expect(externalId).toBeTruthy();

  // The dev stack keeps tenant webhook secrets only in OpenBao KV —
  // the spec reads it with the dev root token (gitignored) and signs
  // exactly like a real vendor would.
  const rootToken = readFileSync(
    resolve(__dirname, "../../../data/openbao/root-token"),
    "utf8",
  ).trim();
  const kvRes = await page.request.get(
    `${OPENBAO_ADDR}/v1/lola-kv/data/vendors/demo/fraud`,
    { headers: { "x-vault-token": rootToken } },
  );
  expect(kvRes.ok()).toBeTruthy();
  const secret = (await kvRes.json()).data.data.webhook_secret as string;
  expect(secret).toBeTruthy();

  const sign = (body: string) =>
    `sha256=${createHmac("sha256", secret).update(body).digest("hex")}`;

  // Replay of a completed order: signature valid → 200, idempotent
  // no-op (the order already carries exactly one result + one step
  // event — asserted by the API suites).
  const payload = JSON.stringify({
    tenant: "demo",
    external_id: externalId,
    outcome: "completed",
    report: { screeningType: "ofac-identity", ofacMatch: false },
  });
  const replay = await page.request.post(
    `${API_BASE}/api/vendor-webhooks/fraud`,
    { data: payload, headers: { "x-vendor-signature": sign(payload) } },
  );
  expect(replay.status()).toBe(200);

  // Same body, forged signature → 401.
  const forged = await page.request.post(
    `${API_BASE}/api/vendor-webhooks/fraud`,
    { data: payload, headers: { "x-vendor-signature": "sha256=deadbeef" } },
  );
  expect(forged.status()).toBe(401);

  // No signature header → 401.
  const unsigned = await page.request.post(
    `${API_BASE}/api/vendor-webhooks/fraud`,
    { data: payload },
  );
  expect(unsigned.status()).toBe(401);

  // Valid signature, unknown external id → 404 (no phantom orders).
  const ghost = JSON.stringify({
    tenant: "demo",
    external_id: "stub-fraud-doesnotexist",
    outcome: "completed",
    report: {},
  });
  const notFound = await page.request.post(
    `${API_BASE}/api/vendor-webhooks/fraud`,
    { data: ghost, headers: { "x-vendor-signature": sign(ghost) } },
  );
  expect(notFound.status()).toBe(404);
});
