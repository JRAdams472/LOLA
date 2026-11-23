#!/usr/bin/env node
// verify — the pre-push gate for LOLA (MPE-283).
//
//   node tools/verify.mjs [--quick]
//
// Runs every check CI would run, fail-fast in order:
//   adrcheck → dotnet format → dotnet build → dotnet test →
//   per client (clients/* with package.json): lint → typecheck → test → build
//
// --quick skips `dotnet test` and web `build` for tight local loops;
// a full run is still required before opening a PR.
//
// Live tests (Mpex/Zara/Isppr/Vault) hard-require the sibling stacks —
// export the env vars from .env.example first, or those projects fail
// by design (never-skip).

import { spawnSync } from "node:child_process";
import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const QUICK = process.argv.includes("--quick");

let failed = false;
const step = (name, cmd, args, opts = {}) => {
  if (failed) return;
  const t0 = Date.now();
  const r = spawnSync(cmd, args, {
    cwd: opts.cwd ?? root,
    stdio: "inherit",
    shell: process.platform === "win32",
  });
  const secs = ((Date.now() - t0) / 1000).toFixed(0);
  if (r.status !== 0) {
    failed = true;
    console.error(`\n✗ ${name} FAILED (${secs}s)`);
  } else {
    console.log(`✓ ${name} (${secs}s)`);
  }
};

step("ADRs", "node", ["tools/adrcheck.mjs"]);
step("dotnet format", "dotnet", ["format", "--verify-no-changes"]);
step("dotnet build", "dotnet", ["build", "-c", "Release"]);
if (!QUICK)
  step("dotnet test", "dotnet", ["test", "-c", "Release", "--no-build"]);

const clients = readdirSync(join(root, "clients"), { withFileTypes: true })
  .filter((d) => d.isDirectory())
  .map((d) => d.name)
  .filter((n) => existsSync(join(root, "clients", n, "package.json")));

for (const c of clients) {
  const cwd = join(root, "clients", c);
  if (!existsSync(join(cwd, "node_modules"))) {
    console.error(`\n✗ ${c}: node_modules missing — run 'npm ci' in ${cwd} first`);
    failed = true;
    break;
  }
  for (const script of QUICK
    ? ["lint", "typecheck", "test"]
    : ["lint", "typecheck", "test", "build"]) {
    step(`${c} ${script}`, "npm", ["run", script, "--silent"], { cwd });
  }
}

console.log(
  failed
    ? "\nverify: FAILED — fix the step above and re-run before pushing."
    : "\nverify: all green — safe to push."
);
process.exit(failed ? 1 : 0);
