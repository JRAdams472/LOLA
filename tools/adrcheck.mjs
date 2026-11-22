#!/usr/bin/env node
// adrcheck — ADR structure gate for LOLA (MPE-282).
//
//   node tools/adrcheck.mjs [adr-dir]
//
// Defaults to docs/adr relative to this script; pass a directory to check
// a different set (e.g. a synthetic corrupted copy for self-test).
//
// Every docs/adr/NNNN-*.md must have:
//   * exactly one H1 heading beginning "# ADR" (the title);
//   * exactly one "**Status:**" line;
//   * exactly one "## Decision" and one "## Consequences" section,
//     in that order, each with non-empty body text.
// README.md and non-ADR files are ignored.

import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const dir =
  process.argv[2] ??
  fileURLToPath(new URL("../docs/adr/", import.meta.url));

const files = readdirSync(dir)
  .filter((f) => /^\d{4}-.*\.md$/.test(f))
  .sort();

let fail = false;
const bad = (file, msg) => {
  fail = true;
  console.error(`FAIL ${file}: ${msg}`);
};

for (const f of files) {
  const text = readFileSync(join(dir, f), "utf8");
  const lines = text.split(/\r?\n/);

  const h1s = lines.filter((l) => /^#\s/.test(l));
  const adrH1s = h1s.filter((l) => /^#\s+ADR\b/.test(l));
  if (adrH1s.length !== 1)
    bad(f, `expected exactly one "# ADR" H1, found ${adrH1s.length}`);
  if (h1s.length > adrH1s.length)
    bad(f, `stray H1 heading(s): ${h1s.filter((l) => !/^#\s+ADR\b/.test(l)).join(" | ")}`);

  const statuses = lines.filter((l) => l.startsWith("**Status:**"));
  if (statuses.length !== 1)
    bad(f, `expected exactly one "**Status:**" line, found ${statuses.length}`);

  const d = lines.findIndex((l) => /^##\s+Decision\b/.test(l));
  const c = lines.findIndex((l) => /^##\s+Consequences\b/.test(l));
  if (d === -1) bad(f, "missing \"## Decision\" section");
  if (c === -1) bad(f, "missing \"## Consequences\" section");
  if (d !== -1 && c !== -1 && d > c)
    bad(f, "\"## Decision\" must precede \"## Consequences\"");
  const dupD = lines.filter((l) => /^##\s+Decision\b/.test(l)).length;
  const dupC = lines.filter((l) => /^##\s+Consequences\b/.test(l)).length;
  if (dupD > 1) bad(f, `duplicate "## Decision" sections (${dupD})`);
  if (dupC > 1) bad(f, `duplicate "## Consequences" sections (${dupC})`);

  // Section bodies must carry real content, not just headings/blank lines.
  const body = (start, end) =>
    lines
      .slice(start + 1, end === -1 ? lines.length : end)
      .filter((l) => l.trim() && !l.startsWith("#") && !l.startsWith("**Status:**"));
  if (d !== -1 && body(d, c).length === 0)
    bad(f, "\"## Decision\" section is empty");
  if (c !== -1 && body(c, -1).length === 0)
    bad(f, "\"## Consequences\" section is empty");
}

if (files.length === 0) {
  console.error(`no ADR files matched in ${dir}`);
  process.exit(2);
}
console.log(
  fail
    ? `adrcheck: FAILED — ${files.length} ADRs checked`
    : `adrcheck: OK — ${files.length} ADRs checked`
);
process.exit(fail ? 1 : 0);
