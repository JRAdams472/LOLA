#!/usr/bin/env node
// mergecheck — automerge-eligibility classifier for LOLA (MPE-288).
//
//   node tools/mergecheck.mjs [base-ref [head-ref]]
//
// Classifies the diff base...head (default origin/main...HEAD) against
// the merge-on-green allowlist. Exit codes:
//   0 = ELIGIBLE   — every changed path is allowlisted
//   1 = INELIGIBLE — one or more paths are outside the class
//   2 = error      — could not compute the diff
//
// The class, deliberately narrow (human-tagged via the
// `automerge-eligible` label; this script only bounds the blast radius):
//
//   ELIGIBLE
//   * *.md documentation and docs/** assets — EXCLUDING the agent
//     control plane: AGENTS.md, docs/adr/**, .devin/**, .github/**,
//     tools/**.
//   * Test-path files — tests/**, **/__tests__/**, **/e2e/**,
//     *.test.*, *.spec.* — ADDITIVE ONLY: a removed line declaring a
//     test or assertion makes the whole diff ineligible.
//
//   INELIGIBLE (deny by default — anything not listed above)
//   * src/**, migrations/**, control-plane files above, lockfiles
//     (package*.json, packages.lock.json), build config
//     (*.csproj, *.slnx, Directory.Build.*), compose/Dockerfile,
//     env/key/secret-shaped names, and everything else.
//
// A diff touching mergecheck itself is ineligible by construction —
// the gate can never amend itself without human review.

import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const base = process.argv[2] ?? "origin/main";
const head = process.argv[3] ?? "HEAD";

const NEVER = [
  [/^AGENTS\.md$/, "agent control plane"],
  [/^docs\/adr\//, "locked architecture decisions"],
  [/^\.devin\//, "agent control plane"],
  [/^\.github\//, "agent control plane (CI)"],
  [/^tools\//, "agent control plane (gates)"],
  [/^src\//, "source code"],
  [/^migrations\//, "migrations"],
  [/(^|\/)Directory\.Build\./, "build policy"],
  [/\.(csproj|props|targets|sln|slnx)$/, "build config"],
  [/(^|\/)package(-lock)?\.json$/, "dependency manifest / lockfile"],
  [/packages\.lock\.json$/, "lockfile"],
  [/(^|\/)(docker-compose|Dockerfile)/, "infra"],
  [/\.env($|\.)/, "env file"],
  [/\.(pem|key|pfx|p12)$/, "key material"],
];

const TEST_PATH = (p) =>
  /^tests\//.test(p) ||
  /(^|\/)(__tests__|e2e|mocks)\//.test(p) ||
  /\.(test|spec)\.[jt]sx?$/.test(p);

const DOCS_PATH = (p) => /^docs\//.test(p) || /\.(md|mdx)$/.test(p);

// Removed lines that mean "a test or assertion was weakened/deleted".
const REMOVED_TEST_LINE = /^-\s*(?!.*\/\/).*(\[(Fact|Theory|InlineData)\]|\b(it|test|describe)\s*\(|expect\s*\(|Assert\.|Should\(\))/;

const git = (args) =>
  execFileSync("git", args, { cwd: root, encoding: "utf8" });

let files;
try {
  files = git(["diff", "--name-only", `${base}...${head}`])
    .split("\n")
    .filter(Boolean);
} catch (e) {
  console.error(`mergecheck: error computing diff ${base}...${head}: ${e.message.split("\n")[0]}`);
  process.exit(2);
}

if (files.length === 0) {
  console.error(`mergecheck: error — empty diff ${base}...${head}`);
  process.exit(2);
}

const reasons = [];

for (const f of files) {
  const never = NEVER.find(([re]) => re.test(f));
  if (never) {
    reasons.push(`  ✗ ${f} — ${never[1]}`);
    continue;
  }
  if (TEST_PATH(f)) {
    const diff = git(["diff", `${base}...${head}`, "--", f]);
    const removed = diff
      .split("\n")
      .filter((l) => l.startsWith("-") && !l.startsWith("---"))
      .filter((l) => REMOVED_TEST_LINE.test(l));
    if (removed.length > 0) {
      reasons.push(
        `  ✗ ${f} — test diff removes test/assertion lines (${removed.length})`
      );
    }
    continue;
  }
  if (DOCS_PATH(f)) continue;
  reasons.push(`  ✗ ${f} — outside the merge-on-green class`);
}

if (reasons.length === 0) {
  console.log(
    `mergecheck: ELIGIBLE — ${files.length} file(s), all inside the class`
  );
  process.exit(0);
}
console.log(
  `mergecheck: INELIGIBLE — ${files.length} file(s) checked:\n${reasons.join("\n")}`
);
process.exit(1);
