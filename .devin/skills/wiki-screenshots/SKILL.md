---
name: wiki-screenshots
description: Capture real web-UI screenshots for the GitHub wiki — run the isolated lolashots e2e stack, seed demo data, and shoot pages with Playwright. Use when a feature adds/changes user-facing screens or the wiki needs visual updates.
---

# Wiki screenshots

Migrated from lena2 — the scripts/tools land with the first UI milestone
(M10/M11). Reusable pattern:

- `tools/wiki-shots/seed_demo.py` — idempotent demo-data seeder
- `tools/wiki-shots/capture.mjs` — Playwright capture against the stack
  (edit the shot list for new pages)

Quickstart (once the shots stack exists):

```bash
docker compose -p lolashots \
  -f docker-compose.yml -f docker-compose.e2e.yml --profile seed up -d --build
python tools/wiki-shots/seed_demo.py
node tools/wiki-shots/capture.mjs
```

Key rules (details in the runbook): always use the separate `-p lolashots`
project (never the dev data-plane files — seeded demo tenants contain only
synthetic data, **never real or realistic PII**), `--profile seed` is
required, `networkidle` never settles (wait on rendered text instead), and
inspect every PNG for loading spinners before publishing to the
`LOLA.wiki` repo (`images/` + `![alt](images/x.png)`, push to `master`).

**PII rule specific to LOLA:** screenshots must never show real personal
data. Seeded borrowers are fictional (Test McTesterson-style names,
000-xx SSN-format fakes); if a screenshot could expose anything realistic,
regenerate with synthetic data — the wiki repo is separate and its history
is not a place for PII accidents.
