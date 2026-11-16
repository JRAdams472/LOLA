import { expect, test } from "@playwright/test";

/**
 * M12 P4 documents on the real stack: the borrower uploads a real PDF
 * through the BFF → quarantine → ClamAV → clean; disallowed bytes get
 * a friendly rejection; downloads mint a presigned capability URL
 * (302 the browser follows — no token, no vault internals).
 */

const PDF_BYTES = Buffer.from(
  "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF",
);

test("upload → scan → listed; rejects disallowed type; download 302s", async ({
  page,
}) => {
  const me = await page.request.get("/auth/me");
  const { tid } = await me.json();

  // Ensure the borrower has an application to attach docs to.
  const created = await page.request.post(
    `/bff/api/tenants/${tid}/applications`,
    { data: {} },
  );
  expect(created.ok()).toBeTruthy();

  await page.goto("/documents");

  // Reject path first — a .txt with no allowed magic bytes → 415 →
  // friendly copy, nothing lands in the uploads list.
  await page
    .getByLabel("File")
    .setInputFiles({ name: "notes.txt", mimeType: "text/plain",
                     buffer: Buffer.from("not a document") });
  await page.getByRole("button", { name: "Upload" }).click();
  await expect(
    page.getByText(/can't read that file type/),
  ).toBeVisible();

  // Happy path — a real PDF rides the streamed multipart route.
  await page.getByLabel("Document type").selectOption("bank-statement");
  await page
    .getByLabel("File")
    .setInputFiles({ name: "bank-statement.pdf", mimeType: "application/pdf",
                     buffer: PDF_BYTES });
  await page.getByRole("button", { name: "Upload" }).click();
  await expect(
    page.getByText(/we're checking it now/),
  ).toBeVisible();

  // Quarantine → scan is async; the page polls while checking.
  const row = page.getByRole("listitem").filter({ hasText: "bank-statement.pdf" });
  await expect(row).toContainText("Bank statement");
  await expect(row.getByText("Received")).toBeVisible({ timeout: 60_000 });

  // The download link mints an audited 302 → presigned capability URL.
  const href = await row
    .getByRole("link", { name: "Download" })
    .getAttribute("href");
  expect(href).toContain(`/api/tenants/${tid}/`);
  const resp = await page.request.get(href!, { maxRedirects: 0 });
  expect(resp.status()).toBe(302);
  // Capability URL — LOLA's own auth-free content route, sig + exp:
  // the browser never touches MinIO (ADR-0007).
  const loc = resp.headers()["location"] ?? "";
  expect(loc).toContain("/api/vault-content/");
  expect(loc).toContain("sig=");
  expect(loc).toContain("exp=");
});
