# LOLA Officer Console — design world

Locked house style (sibling convention, GitHub-dark). Not a creative
choice — a constraint. New surfaces extend this file, they don't fork it.

## Tokens

- `background.default` `#0d1117` · `background.paper` `#161b22`
- `divider` `#30363d` · `text.primary` `#e6edf3` · `text.secondary` `#9198a1`
- `primary` `#2f9cff` · `secondary` `#79c0ff`
- `success` `#3fb950` · `warning` `#d29922` · `error` `#f85149`
- Compact density: dense Toolbar, dense List, `size="small"` controls,
  outlined (never filled) status chips.
- Single MUI theme object in `app/theme.ts`; no per-component palettes.

## Layout grammar

- Fixed AppBar (bordered, elevation 0) + permanent Drawer (`220px`) +
  main column. Nav item = route; selection follows `pathname`.
- Pages: `h6` section heading → content. No hero blocks, no marketing
  surfaces — Operate mode only.
- Tables are dense and border-light; rows navigate to detail routes.

## Semantic mapping

| Concept | Rendering |
|---|---|
| Pipeline step / app status | `StatusChip` — warning = working, info = progress, success = terminal, default = unknown |
| TRID countdown | dedicated chip — muted no-clock, default >48h, warning <48h, error overdue |
| Masked PII | `•••xxxx` verbatim from API + reveal affordance (staff only) |
| Revealed state | persistent error-outlined banner for the session scope |
| Pending (202 outbox) | chip + poll until the eval/registry row lands |

## Copy voice

Errors quote the API's problem details; empty states give the first
action ("Create a loan to start the pipeline"); buttons are verbs
("Re-Price", "Generate Package", "Reveal") — no exclamation, no emoji.
