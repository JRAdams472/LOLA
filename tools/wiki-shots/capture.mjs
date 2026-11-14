// Capture officer-console screenshots for the wiki against the isolated
// lolashots stack (`docker compose -p lolashots -f docker-compose.yml
// -f docker-compose.e2e.yml --profile seed up -d --build`, then this).
// Output: tools/wiki-shots/out/*.png — inspect every image for spinners
// and PII before publishing to the wiki repo's images/.
//
// Run from the repo root:  node tools/wiki-shots/capture.mjs
import { mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(
  new URL("../../clients/web/package.json", import.meta.url),
);
const { chromium } = require("@playwright/test");

const WEB = process.env.WEB_URL ?? "http://localhost:3200";
const EMAIL = process.env.OFFICER_EMAIL ?? "officer@lola.dev";
const PASSWORD = process.env.OFFICER_PASSWORD ?? "lola-officer-dev";
const OUT = fileURLToPath(new URL("./out/", import.meta.url));
const TENANT = "demo";

mkdirSync(OUT, { recursive: true });

async function main() {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

  // ── login (real OIDC auth-code+PKCE through the console) ──────────
  await page.goto(WEB);
  await page.waitForURL(/account\/login/);
  await shot(page, "officer-login");
  await page.fill('input[name="email"]', EMAIL);
  await page.fill('input[name="password"]', PASSWORD);
  await page.click('button[type="submit"]');
  await page.getByRole("heading", { name: "Pipeline" }).waitFor();

  // Find the submitted hero application the seeder created.
  const gql = (query, variables) =>
    page.request
      .post(`${WEB}/bff/graphql`, { data: { query, variables } })
      .then((r) => r.json());
  const { data } = await gql(
    `query($t:String!){ applications(tenantId:$t){ id borrowerName status } }`,
    { t: TENANT },
  );
  const hero = data.applications.find((a) => a.status !== "draft");
  const heroId = hero?.id ?? data.applications[0].id;
  console.log("hero:", heroId, hero?.borrowerName, hero?.status);

  // ── pipeline ──────────────────────────────────────────────────────
  await page.goto(WEB);
  await page.getByRole("cell", { name: /Demo Borrower/ }).first().waitFor();
  await shot(page, "officer-pipeline");

  // ── workspace (masked 1003) ───────────────────────────────────────
  await page.goto(`${WEB}/loans/${heroId}`);
  await page.getByLabel("First name").waitFor();
  await page.getByText("•••").first().waitFor();
  await shot(page, "officer-workspace");

  // ── audited PII reveal (reason required → unmasked + banner) ──────
  await page.getByLabel("reveal").first().click();
  await page.getByLabel(/Reason \(required\)/).fill("borrower SSN verification for LE");
  await page.getByRole("button", { name: "Reveal" }).click();
  await page.getByRole("dialog").waitFor({ state: "detached" });
  await page.getByText(/Unmasked PII visible/).first().waitFor();
  await shot(page, "officer-revealed");
  await page.getByRole("button", { name: /re-mask/i }).click();

  // ── closing costs & docs ──────────────────────────────────────────
  await page.goto(`${WEB}/loans/${heroId}/closing`);
  await page.getByText(/A — Origination charges/).first().waitFor();
  await page.getByText(/expires/).first().waitFor();
  await shot(page, "officer-closing");

  // ── search (blind-index SSN hit) ──────────────────────────────────
  await page.goto(`${WEB}/search`);
  await page.getByLabel("Field").click();
  await page.getByRole("option", { name: "SSN" }).click();
  await page.getByLabel("Value").fill("000123456");
  await page.getByRole("button", { name: "Search" }).click();
  await page.getByText("Demo Borrower").first().waitFor();
  await shot(page, "officer-search");

  // ── admin (officer sees read-only surface; capture users tab) ─────
  await page.goto(`${WEB}/admin`);
  await page.getByRole("cell", { name: /lola\.dev/ }).first().waitFor();
  await shot(page, "officer-admin");

  await browser.close();
  console.log("wrote", OUT);
}

async function shot(page, name) {
  await page.screenshot({ path: `${OUT}${name}.png` });
  console.log("shot", name);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
