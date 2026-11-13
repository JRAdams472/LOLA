import { defineConfig } from "@playwright/test";

// e2e runs against the dev stack: the API must be up (docker compose up
// api, or dotnet run) with DEV_SEED_* set so a demo tenant + officer
// exist (see .env.example). The webServer block starts `next dev` if
// needed.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  retries: 0,
  // github = CI annotations; html = artifact uploaded on failure.
  reporter: process.env.CI ? [["github"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3200",
  },
  // Auth setup logs in through the real OIDC flow once; other specs reuse
  // the sealed session cookie via storageState. auth.spec exercises the
  // login flow itself, so it runs state-free.
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
      use: { storageState: "e2e/.auth/officer.json" },
    },
  ],
  webServer: {
    command: "npm run dev",
    // Probe a static asset — / 307-redirects to login, which the
    // readiness check doesn't accept as "up".
    url: `${process.env.E2E_BASE_URL ?? "http://localhost:3200"}/icon.svg`,
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
