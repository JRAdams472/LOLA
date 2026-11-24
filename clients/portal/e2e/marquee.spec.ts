import { expect, test, type Browser, type Page } from "@playwright/test";

/**
 * M12 P5 marquee — the full borrower journey on the real stack, in one
 * narrative: officer invites → borrower accepts → OIDC login → wizard
 * fill → save → logout → login → masked resume → submit → tracker →
 * officer prices/locks/evals/generates → borrower uploads → both sides
 * download. Officer calls ride the console BFF; borrower calls ride
 * the portal BFF — the access token never reaches either browser.
 */

const OFFICER_BASE = process.env.E2E_OFFICER_BASE_URL ?? "http://localhost:3200";
const OFFICER_EMAIL = process.env.E2E_OFFICER_EMAIL ?? "officer@lola.dev";
const OFFICER_PASSWORD = process.env.E2E_OFFICER_PASSWORD ?? "lola-officer-dev";
const TENANT = process.env.E2E_TENANT ?? "demo";
const SHOTS = "e2e/artifacts/marquee";

const EMAIL = `marquee-${Date.now().toString(36)}@lola.dev`;
const PASSWORD = "marquee-dev-password-12";

const PDF_BYTES = Buffer.from(
  "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF",
);

/** Everything the validator requires — seeded through the same PATCH
 *  the wizard's save path issues (the borrower types the narrative
 *  fields by hand; the long tail rides the wire). */
const REQUIRED: Record<string, string> = {
  "loan_amount": "425000",
  "loan_purpose": "purchase",
  "amortization_type": "fixed",
  "loan_term_months": "360",
  "party[0].dob": "1988-04-12",
  "party[0].email": "marina@example.test",
  "party[0].phone": "5550100123",
  "party[0].role": "borrower",
  "party[0].address[0].street": "1 Test Way",
  "party[0].address[0].city": "Springfield",
  "party[0].address[0].state": "IL",
  "party[0].address[0].zip": "62701",
  "party[0].employment[0].employer_name": "Example Works",
  "party[0].employment[0].start_date": "2020-06-01",
  "party[0].employment[0].self_employed": "false",
  "party[0].employment[0].monthly_income": "8200",
  "asset[0].asset_type": "checking",
  "asset[0].institution": "First Test Bank",
  "asset[0].account_number": "000111222",
  "asset[0].balance": "45000",
  "liability[0].liability_type": "auto",
  "liability[0].creditor": "Test Auto Finance",
  "liability[0].monthly_payment": "310",
  "liability[0].balance": "9000",
  "declaration.intent_to_occupy": "yes",
  "declaration.first_time_buyer": "true",
  "declaration.property_will_be": "primary_residence",
  "subject_property.street": "9 Demo Lane",
  "subject_property.city": "Springfield",
  "subject_property.state": "IL",
  "subject_property.zip": "62702",
  "subject_property.est_value": "560000",
  "subject_property.property_type": "single_family",
  "subject_property.usage": "primary_residence",
  "demographics.ethnicity": "not_provided",
  "demographics.race": "not_provided",
  "demographics.sex": "not_provided",
  "loan.base_amount": "425000",
  "loan.ltv": "75.89",
  "loan.lien_position": "first",
  "loan.product_type": "conventional",
};

const FRESH = { cookies: [], origins: [] };

async function officerLogin(browser: Browser): Promise<Page> {
  // Explicit empty state — browser.newContext() inherits the project's
  // use.storageState (borrower.json incl. the IdP cookie), which would
  // silently SSO the BORROWER into the console. Both legs must be
  // cookie-fresh so each does its own OIDC dance.
  const ctx = await browser.newContext({ storageState: FRESH });
  const officer = await ctx.newPage();
  await officer.goto(OFFICER_BASE);
  await expect(officer).toHaveURL(/account\/login/);
  await officer.fill('input[name="org"]', "demo");
  await officer.fill('input[name="email"]', OFFICER_EMAIL);
  await officer.fill('input[name="password"]', OFFICER_PASSWORD);
  await officer.click('button[type="submit"]');
  await expect(
    officer.getByRole("heading", { name: "Pipeline" }),
  ).toBeVisible();
  return officer;
}

test("marquee: invite → apply → submit → package → documents → download",
  async ({ browser }) => {
    test.setTimeout(300_000);

    // ── 1 · officer issues the invite ──────────────────────────────
    const officer = await officerLogin(browser);
    const invite = await officer.request.post(
      `${OFFICER_BASE}/bff/api/admin/tenants/${TENANT}/invites`,
      { data: { email: EMAIL } },
    );
    expect(invite.status()).toBe(201);
    const { inviteUrl } = await invite.json();

    // ── 2 · borrower accepts + logs in ─────────────────────────────
    const bctx = await browser.newContext({ storageState: FRESH });
    const page = await bctx.newPage();
    await page.goto(inviteUrl);
    await page.screenshot({ path: `${SHOTS}/01-invite.png` });
    await page.fill("#displayName", "Marquee Borrower");
    await page.fill("#password", PASSWORD);
    await page.fill("#confirm", PASSWORD);
    await page.getByRole("button", { name: "Create account" }).click();
    await page.getByRole("link", { name: "Sign in" }).click();
    await page.fill('input[name="org"]', "demo");
  await page.fill('input[name="email"]', EMAIL);
    await page.fill('input[name="password"]', PASSWORD);
    await page.click('button[type="submit"]');
    await expect(
      page.getByRole("heading", { name: "Home" }),
    ).toBeVisible();
    await page.screenshot({ path: `${SHOTS}/02-home.png` });

    // ── 3 · wizard — type the narrative fields, save, resume ───────
    await page.goto("/apply");
    await page.getByRole("button", { name: "Begin" }).click();
    await page.getByLabel("First Name *").fill("Marina");
    await page.getByLabel("Last Name *").fill("MarqueeSample");
    await page.getByLabel("SSN *").fill("000123456");
    // The fields are controlled inputs — the edit must reach wizard
    // state before the save or the PATCH would carry nothing.
    await expect(page.getByLabel("SSN *")).toHaveValue("000123456");
    const savePatch = page.waitForResponse(
      (r) => r.request().method() === "PATCH"
        && r.url().includes("/applications/"),
    );
    await page.getByRole("button", { name: "Save draft" }).click();
    // Disabled alone can't prove a save — an empty patch disables the
    // button too. The PATCH response is the real signal.
    await expect(savePatch).resolves.toBeTruthy();
    await expect(
      page.getByRole("button", { name: "Save draft" }),
    ).toBeDisabled({ timeout: 10_000 });
    await page.screenshot({ path: `${SHOTS}/03-wizard.png` });

    // Logout → login — the draft and its masked PII persist.
    await page.goto("/auth/logout");
    await page.goto("/");
    await expect(page).toHaveURL(/account\/login/);
    await page.fill('input[name="org"]', "demo");
  await page.fill('input[name="email"]', EMAIL);
    await page.fill('input[name="password"]', PASSWORD);
    await page.click('button[type="submit"]');
    await expect(
      page.getByRole("heading", { name: "Home" }),
    ).toBeVisible();
    await page.getByRole("link", { name: "Continue application" }).click();
    // Masked-confirm resume — the borrower's own PII renders masked.
    // Assert only after the draft doc lands: /apply fires portalLoans
    // then portalLoan(tenantId, appId) — the detail call is the one that
    // carries docJson, so wait for it before looking for masked text.
    await page.waitForResponse(
      (r) => r.url().includes("/bff/graphql")
        && (r.request().postData() ?? "").includes("portalLoan("),
    );
    await expect(page.getByText("•••3456")).toBeVisible();
    await expect(page.getByText("•••rina")).toBeVisible();
    await page.screenshot({ path: `${SHOTS}/04-resume.png` });

    // Seed the rest through the wire (the wizard's own PATCH route)
    // then submit through the UI.
    const me = await page.request.get("/auth/me");
    const { tid } = await me.json();
    const loansRes = await page.request.post("/bff/graphql", {
      data: {
        query:
          `query($t:String!){portalLoans(tenantId:$t){id statusStage}}`,
        variables: { t: tid },
      },
    });
    const appId = (await loansRes.json()).data.portalLoans[0].id;
    const fill = await page.request.fetch(
      `/bff/api/tenants/${tid}/applications/${appId}`,
      { method: "PATCH", data: { patch: REQUIRED } },
    );
    expect(fill.ok()).toBeTruthy();

    await page.reload();
    await page.getByRole("button", { name: "Review" }).click();
    await page.screenshot({ path: `${SHOTS}/05-review.png` });
    await page.getByRole("button", { name: "Submit application" }).click();
    await expect(
      page.getByText("Application submitted"),
    ).toBeVisible();
    await page.screenshot({ path: `${SHOTS}/06-submitted.png` });
    await page.getByRole("link", { name: "Track your application" }).click();
    await expect(page.getByText("Under review").first()).toBeVisible();

    // ── 4 · officer prices → locks → evals fees → generates LE ─────
    const app = `${OFFICER_BASE}/bff/api/tenants/${TENANT}/applications/${appId}`;
    const gql = (query: string) =>
      officer.request.post(`${OFFICER_BASE}/bff/graphql`, {
        data: { query },
      });
    const price = await officer.request.post(`${app}/price`, { data: {} });
    expect(price.status()).toBeLessThan(400);
    let evalData: {
      evalId: string;
      result: { products: { productCode: string; eligible: boolean;
        offers: { rate: number; lockTermDays: number }[] }[] } | null;
    } | undefined;
    await expect(async () => {
      const r = await gql(
        `{ pricing(tenantId: "${TENANT}", appId: "${appId}") {
            evalId status
            result { products { productCode eligible
              offers { rate lockTermDays } } } } }`,
      );
      const d = await r.json();
      expect(d.data.pricing?.status).toBe("done");
      evalData = d.data.pricing;
    }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 3_000] });

    const product = evalData!.result!.products.find((p) => p.eligible);
    expect(product).toBeTruthy();
    const offer = product!.offers[0];
    const lock = await officer.request.post(`${app}/lock`, {
      data: {
        evalId: evalData!.evalId,
        productCode: product!.productCode,
        rate: offer.rate,
        lockTermDays: offer.lockTermDays,
      },
    });
    expect(lock.status()).toBeLessThan(400);

    const fees = await officer.request.post(`${app}/fees`, { data: {} });
    expect(fees.status()).toBeLessThan(400);
    await expect(async () => {
      const r = await gql(
        `{ fees(tenantId: "${TENANT}", appId: "${appId}") {
            batchId evals { status } } }`,
      );
      const d = await r.json();
      expect(
        d.data.fees?.evals?.some((e: { status: string }) => e.status === "done"),
      ).toBeTruthy();
    }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 3_000] });

    const pkg = await officer.request.post(`${app}/packages`, {
      data: { kind: "le-package", reasonCodes: [] },
    });
    expect(pkg.status()).toBeLessThan(400);
    await expect(async () => {
      const r = await gql(
        `{ documents(tenantId: "${TENANT}", appId: "${appId}") {
            docId kind status } }`,
      );
      const d = await r.json();
      expect(
        d.data.documents?.some(
          (doc: { status: string }) => doc.status === "completed",
        ),
      ).toBeTruthy();
    }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 3_000] });
    await officer.context().close();

    // ── 5 · borrower uploads + downloads ───────────────────────────
    await page.goto("/documents");
    await page
      .getByLabel("File")
      .setInputFiles({ name: "paystub.pdf", mimeType: "application/pdf",
                       buffer: PDF_BYTES });
    await page.getByRole("button", { name: "Upload" }).click();
    const row = page.getByRole("listitem").filter({ hasText: "paystub.pdf" });
    await expect(row.getByText("Received")).toBeVisible({
      timeout: 60_000,
    });
    // The officer-generated package shows as a ready disclosure.
    await expect(
      page.getByText("Loan Estimate package"),
    ).toBeVisible({ timeout: 30_000 });
    await page.screenshot({ path: `${SHOTS}/07-documents.png` });

    // Both download links mint audited capability URLs.
    for (const link of await page
      .getByRole("link", { name: "Download" })
      .all()) {
      const href = await link.getAttribute("href");
      const resp = await page.request.get(href!, { maxRedirects: 0 });
      expect(resp.status()).toBe(302);
      // Vault objects use LOLA's stub signer (sig=/exp=); ISPPr packages
      // proxy a real MinIO presign (X-Amz-Signature). Either way the
      // browser lands on an expiring capability URL, never raw storage.
      expect(resp.headers()["location"]).toMatch(
        /sig=|x-amz-signature/i,
      );
    }
    await bctx.close();
  });
