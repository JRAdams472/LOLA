import { defineConfig } from "@playwright/test";

// e2e runs against the dev stack: the API must be up (docker compose up
// api, or dotnet run) with DEV_SEED_* set so a demo tenant + officer
// exist (see .env.example). The webServer block starts `next dev` if
// needed. The borrower login flows through an officer-issued invite —
// see e2e/auth.setup.ts.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  retries: 0,
  // Every spec shares the one storageState borrower — parallel workers
  // would interleave drafts and race assertions across specs.
  workers: 1,
  // github = CI annotations; html = artifact uploaded on failure.
  reporter: process.env.CI ? [["github"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3300",
  },
  // Auth setup issues an invite via the officer console, accepts it, and
  // logs the borrower in through the real OIDC flow once; other specs
  // reuse the session handle cookie via storageState. auth.spec exercises
  // the login/invite flow itself, so it runs state-free.
  projects: [
    { name: "setup", testMatch: /auth\.setup\.ts/ },
    {
      name: "chromium-auth",
      testMatch: /auth\.spec\.ts/,
    },
    {
      name: "chromium",
      dependencies: ["setup"],
      testIgnore: /auth\.(spec|setup)\.ts/,
      use: { storageState: "e2e/.auth/borrower.json" },
    },
  ],
  webServer: {
    command: "npm run dev",
    // Probe a static asset — / 307-redirects to login, which the
    // readiness check doesn't accept as "up".
    url: `${process.env.E2E_BASE_URL ?? "http://localhost:3300"}/icon.svg`,
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
