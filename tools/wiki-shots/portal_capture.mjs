// Capture borrower-portal screenshots for the wiki against the isolated
// lolashots stack. Run `portal_seed.py` first — it creates the borrower,
// a submitted application with a clean vault upload + LE package, a
// partial draft for the wizard, and writes out/pending-invite.txt.
//
// Run from the repo root:  node tools/wiki-shots/portal_capture.mjs
import { mkdirSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(
  new URL("../../clients/portal/package.json", import.meta.url),
);
const { chromium } = require("@playwright/test");

const PORTAL = process.env.PORTAL_URL ?? "http://localhost:3300";
const EMAIL = "portal-demo@example.test";
const PASSWORD = "wiki-portal-demo-pass";
const OUT = fileURLToPath(new URL("./out/", import.meta.url));

mkdirSync(OUT, { recursive: true });

async function shot(page, name) {
  await page.screenshot({ path: `${OUT}${name}.png`, fullPage: false });
  console.log("shot", name);
}

async function main() {
  const inviteUrl = readFileSync(`${OUT}pending-invite.txt`, "utf8").trim();
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

  // ── invite acceptance (public — the token is the credential) ─────
  await page.goto(inviteUrl);
  await page.getByRole("heading").first().waitFor();
  await shot(page, "portal-invite-accept");

  // ── OIDC login as the seeded borrower ────────────────────────────
  await page.goto(PORTAL);
  await page.waitForURL(/account\/login/);
  await page.fill('input[name="email"]', EMAIL);
  await page.fill('input[name="password"]', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForURL(PORTAL + "/", { timeout: 30_000 });

  // ── dashboard — submitted card + tracker + draft card ────────────
  await page.getByText("Under review").first().waitFor();
  await page.getByText("Application in progress").first().waitFor();
  await shot(page, "portal-dashboard");

  // ── intake wizard — draft resumes with masked PII ────────────────
  await page.goto(`${PORTAL}/apply`);
  await page.getByText("•••").first().waitFor({ timeout: 15_000 });
  await shot(page, "portal-apply-wizard");

  // ── documents — upload card, clean upload, LE package ────────────
  // The page defaults to the first loan (the draft); the populated
  // surfaces live on the submitted app the seeder hands over.
  const appId = readFileSync(`${OUT}submitted-app.txt`, "utf8").trim();
  await page.goto(`${PORTAL}/documents?app=${appId}`);
  await page.getByText("bank-statement.pdf").waitFor();
  await page.getByText("Received").first().waitFor();
  await shot(page, "portal-documents");

  await browser.close();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
