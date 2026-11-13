// MPE-210 proof screenshots — seeds a synthetic draft and shoots the
// workspace: masked fields, reveal dialog, revealed state, audit tab,
// and 422 field-error mapping. Synthetic borrowers only (invariant 3).
import { chromium } from "@playwright/test";

const BASE = "http://localhost:3200";
const SHOTS = "screenshots";
const id = () => Date.now().toString().slice(-8);

const browser = await chromium.launch();
const ctx = await browser.newContext({
  storageState: "e2e/.auth/officer.json",
  viewport: { width: 1440, height: 900 },
});
const page = await ctx.newPage();

// Seed a TRID-complete draft (synthetic Ada Lovelace).
const created = await page.request.post(
  `${BASE}/bff/api/tenants/demo/applications`,
  { data: {} },
);
const { id: appId } = await created.json();
const ssn = `3${id()}`;
await page.request.patch(`${BASE}/bff/api/tenants/demo/applications/${appId}`, {
  data: {
    patch: {
      "party[0].role": "borrower",
      "party[0].first_name": "Ada",
      "party[0].last_name": "Lovelace",
      "party[0].ssn": ssn,
      "party[0].employment[0].monthly_income": "14200",
      "subject_property.street": "1 Prime Plaza",
      "subject_property.est_value": "560000",
      loan_amount: "425000",
    },
  },
});

await page.goto(`${BASE}/loans/${appId}`);
await page.waitForSelector("text=•••");
await page.screenshot({ path: `${SHOTS}/ws-1-masked.png`, fullPage: true });

// Reveal dialog.
await page.getByRole("button", { name: "Reveal PII" }).click();
await page.getByLabel(/Reason/).fill("borrower SSN verification for LE");
await page.waitForTimeout(400); // let the dialog fade in fully
await page.screenshot({ path: `${SHOTS}/ws-2-reveal-dialog.png` });
await page.getByRole("button", { name: "Reveal", exact: true }).click();
await page.waitForSelector("text=Unmasked PII visible");
await page.getByRole("dialog").waitFor({ state: "hidden" });
await page.screenshot({ path: `${SHOTS}/ws-3-revealed.png`, fullPage: true });

// Audit tab shows the reveal event.
await page.getByRole("tab", { name: "Audit" }).click();
await page.locator("td", { hasText: "reveal" }).first().waitFor();
await page.screenshot({ path: `${SHOTS}/ws-4-audit.png` });

// Back to fields, re-mask, submit → 422 errors on inputs.
await page.getByRole("button", { name: "Re-mask" }).click();
await page.getByRole("button", { name: "Submit" }).click();
await page.waitForSelector("text=attention");
await page.screenshot({ path: `${SHOTS}/ws-5-422.png`, fullPage: true });

await browser.close();
console.log(`done — ${appId}`);
