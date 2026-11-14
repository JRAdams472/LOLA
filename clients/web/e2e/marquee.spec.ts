import { expect, test, type Page } from "@playwright/test";

// Serial — one sequential journey through the whole officer flow.
test.describe.configure({ mode: "serial" });

const SHOTS = "shots/marquee";

// The chromium project injects officer.json globally; the marquee does
// its own login as step one, so it needs a clean context.
test.use({ storageState: { cookies: [], origins: [] } });

/**
 * The M11 marquee: one continuous officer journey on the real stack —
 *   login → create → fill → submit → price → lock → fee override →
 *   package → download → SSN blind-search → masked → audited reveal.
 *
 * Unlike the per-surface specs this runs WITHOUT a seeded storageState:
 * the OIDC login is part of the journey. Every screenshot lands in
 * `shots/` for the subticket proof (gitignored locally, attached to
 * MPE-212).
 */
async function login(page: Page) {
  await page.goto("/");
  await expect(page).toHaveURL(/account\/login/);
  await page.screenshot({ path: `${SHOTS}/01-login.png` });
  await page.fill('input[name="email"]', "officer@lola.dev");
  await page.fill('input[name="password"]', "lola-officer-dev");
  await page.click('button[type="submit"]');
  await expect(page.getByRole("heading", { name: "Pipeline" })).toBeVisible();
}

test("marquee — full officer journey end to end", async ({ page }) => {
  // ADR-0007: the browser may never call a vendor directly. Track every
  // page-initiated request; presigned downloads go through request
  // context, not the page.
  const leaks: string[] = [];
  page.on("request", (req) => {
    const u = req.url();
    if (/localhost:(8080|8090|8400|9200)/.test(u)) leaks.push(u);
  });

  // ── login ───────────────────────────────────────────────────────────
  await login(page);
  await page.screenshot({ path: `${SHOTS}/02-pipeline.png` });

  // ── create ──────────────────────────────────────────────────────────
  // In `next dev` the pipeline can paint before hydration wires the
  // click handler — retry the click until the dialog actually opens.
  await expect(async () => {
    await page.getByRole("button", { name: "New loan" }).click();
    await expect(
      page.getByRole("button", { name: "Create draft" }),
    ).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 15_000 });
  await page.getByRole("button", { name: "Create draft" }).click();
  await expect(page).toHaveURL(/\/loans\/app-/);
  const id = page.url().split("/").pop()!;

  // ── fill ────────────────────────────────────────────────────────────
  // Merge-patch the required fields via the BFF FIRST — while no UI
  // save can be in flight (a UI PATCH racing this one once produced
  // a lost update on the server) — then do a real UI edit so Save
  // stays part of the journey.
  const ssn = `9${Date.now().toString().slice(-8)}`;
  const patch = await page.request.patch(
    `/bff/api/tenants/demo/applications/${id}`,
    {
      data: {
        patch: {
          loan_amount: "425000",
          loan_purpose: "purchase",
          amortization_type: "fixed",
          loan_term_months: "360",
          "party[0].role": "borrower",
          "party[0].last_name": "Journey",
          "party[0].ssn": ssn,
          "party[0].dob": "1985-04-12",
          "party[0].email": "marquee@example.test",
          "party[0].phone": "5550100",
          "party[0].address[0].street": "1 Prime Plaza",
          "party[0].address[0].city": "Springfield",
          "party[0].address[0].state": "IL",
          "party[0].address[0].zip": "62701",
          "party[0].employment[0].employer_name": "Acme Corp",
          "party[0].employment[0].start_date": "2019-06-01",
          "party[0].employment[0].self_employed": "false",
          "party[0].employment[0].monthly_income": "14200",
          "asset[0].asset_type": "checking",
          "asset[0].institution": "First Bank",
          "asset[0].account_number": "123456789",
          "asset[0].balance": "85000",
          "liability[0].liability_type": "auto",
          "liability[0].creditor": "Auto Finance Co",
          "liability[0].monthly_payment": "410",
          "liability[0].balance": "12000",
          "declaration.intent_to_occupy": "yes",
          "declaration.first_time_buyer": "false",
          "declaration.property_will_be": "primary_residence",
          "subject_property.street": "1 Prime Plaza",
          "subject_property.city": "Springfield",
          "subject_property.state": "IL",
          "subject_property.zip": "62701",
          "subject_property.est_value": "560000",
          "subject_property.property_type": "single_family",
          "subject_property.usage": "primary_residence",
          "demographics.ethnicity": "not_provided",
          "demographics.race": "not_provided",
          "demographics.sex": "not_provided",
          "loan.base_amount": "425000",
          "loan.ltv": "75.9",
          "loan.lien_position": "first",
          "loan.product_type": "conventional",
        },
      },
    },
  );
  expect(patch.ok()).toBeTruthy();
  await page.reload();
  await expect(page.getByLabel("Last name")).toHaveValue("Journey");

  // A real UI edit + save on top of the patched draft — wait for the
  // PATCH response so the save provably commits before submit.
  await page.getByLabel("First name").fill("Marquee");
  const saved = page.waitForResponse(
    (r) =>
      r.request().method() === "PATCH" &&
      r.url().includes(`/applications/${id}`) &&
      r.ok(),
  );
  await page.getByRole("button", { name: /Save draft \(1\)/ }).click();
  await saved;
  await expect(
    page.getByRole("button", { name: "Save draft", exact: true }),
  ).toBeVisible();
  await page.screenshot({ path: `${SHOTS}/03-workspace.png` });

  // ── submit ──────────────────────────────────────────────────────────
  await page.getByRole("button", { name: "Submit" }).click();
  await expect(page.getByText("submitted").first()).toBeVisible({
    timeout: 15_000,
  });

  // ── price + lock + override + package (closing surface) ────────────
  await page.getByRole("link", { name: "Closing & Docs" }).click();
  await expect(page).toHaveURL(new RegExp(`/loans/${id}/closing`));

  await page.getByRole("button", { name: "Re-Price" }).click();
  const lockButton = page.getByRole("button", { name: /^lock / }).first();
  await expect(lockButton).toBeVisible({ timeout: 30_000 });
  await lockButton.click();
  await expect(page.getByText(/expires/).first()).toBeVisible({
    timeout: 10_000,
  });
  await page.screenshot({ path: `${SHOTS}/04-locked.png` });

  await page.getByRole("button", { name: "Recalculate" }).click();
  await expect(page.getByText("Section total").first()).toBeVisible({
    timeout: 30_000,
  });
  await page
    .getByRole("button", { name: /^override / })
    .first()
    .click();
  const overrideDialog = page.getByRole("dialog", { name: /Fee override/ });
  await overrideDialog
    .getByLabel("Override amount (empty = clear override)")
    .fill("100");
  await overrideDialog.getByLabel("Reason (required)").fill("marquee override");
  await overrideDialog.getByRole("button", { name: "Apply" }).click();
  await expect(page.getByText("$100.00").first()).toBeVisible();

  await page.getByRole("button", { name: "Generate" }).click();
  const pkgDialog = page.getByRole("dialog", {
    name: "Generate document package",
  });
  await pkgDialog.getByRole("button", { name: "Generate" }).click();
  await expect(
    page.getByRole("cell", { name: /le-package/ }).first(),
  ).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: `${SHOTS}/05-package.png` });

  // ── download ────────────────────────────────────────────────────────
  // The grid 302s through the BFF to a fresh presigned URL — verify the
  // redirect chain and that the target actually serves bytes. The docId
  // comes from the same GraphQL read the page issues; poll until the
  // package resolves to `completed`.
  let doneDoc: { docId: string; status: string } | undefined;
  await expect(async () => {
    const docsRes = await page.request.post("/bff/graphql", {
      data: {
        query: `query { documents(tenantId: "demo", appId: "${id}") { docId status } }`,
      },
    });
    const docsJson = await docsRes.json();
    doneDoc = docsJson.data.documents.find(
      (d: { status: string }) => d.status === "completed",
    );
    expect(doneDoc).toBeTruthy();
  }).toPass({ timeout: 30_000, intervals: [1_000, 2_000, 3_000] });
  const dl = await page.request.get(
    `/bff/api/tenants/demo/applications/${id}/documents/${doneDoc!.docId}/download`,
    { maxRedirects: 0 },
  );
  expect(dl.status()).toBe(302);
  const location = dl.headers()["location"];
  expect(location).toBeTruthy();
  const bytes = await page.request.get(location!);
  expect(bytes.ok()).toBeTruthy();
  expect((await bytes.body()).length).toBeGreaterThan(0);

  // ── SSN blind-search ────────────────────────────────────────────────
  await page.goto("/search");
  await page.getByLabel("Field").click();
  await page.getByRole("option", { name: "SSN" }).click();
  await page.getByLabel("Value").fill(ssn);
  await page.getByRole("button", { name: "Search" }).click();
  await expect(page.getByText("Marquee Journey").first()).toBeVisible();
  // SSN travels in the POST body — it must never appear in the URL.
  expect(page.url()).not.toContain(ssn);
  await page.screenshot({ path: `${SHOTS}/06-search.png` });

  // ── masked → audited reveal ─────────────────────────────────────────
  await page.goto(`/loans/${id}`);
  await expect(page.getByText("•••").first()).toBeVisible();
  await expect(page.locator("body")).not.toContainText(ssn);
  await page.getByRole("button", { name: "Reveal PII" }).click();
  await page.getByLabel(/Reason/).fill("marquee borrower verification");
  await page.getByRole("button", { name: "Reveal", exact: true }).click();
  await expect(
    page.locator(`input[value="${ssn}"]`).first(),
  ).toBeVisible();
  await page.getByRole("tab", { name: "Audit" }).click();
  await expect(page.getByRole("row", { name: /reveal/ }).first()).toBeVisible();
  await page.screenshot({ path: `${SHOTS}/07-revealed.png` });
  await page.getByRole("button", { name: "Re-mask" }).click();
  await page.getByRole("tab", { name: "Borrower(s)" }).click();
  await expect(page.locator("body")).not.toContainText(ssn);

  expect(leaks).toEqual([]);
});
