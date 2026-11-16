# Borrower Portal — design system (M12)

Same GitHub-dark world as the officer console (`canvas #0d1117`,
`surface #161b22`, `border #30363d`, `accent #58a6ff` / `#1f6feb`),
reinterpreted for a guided single-user flow rather than a dense ops UI.
Tailwind v4 theme tokens in `app/globals.css` (`--color-*` → `bg-*`,
`text-*`, `border-*` utilities).

## Layout

- One column, `max-w-3xl` centered — no sidebars, no data tables, no
  dense grids. A borrower reads top-to-bottom.
- Header: wordmark left ("LOLA Borrower Portal"), identity chip +
  sign-out right. Nothing else — navigation is the task itself.
- Cards: `surface` bg, `border`, `rounded-lg`, `p-5/6` — section
  boundaries the borrower can see.

## Type & controls

- System font stack; `text-xl` page headings, `text-sm` body,
  `text-xs` meta/chips.
- Inputs: `bg-canvas` on `surface` cards, `border-border` →
  `focus:border-accent`, visible labels (no placeholder-only labels).
- Primary action: `bg-accent-strong` → `hover:bg-accent`, full-width on
  forms; disabled = `opacity-40` + `cursor-not-allowed`.
- Errors: `danger` border/10% bg strip with `role="alert"`, plain
  language, always paired with what to do next.
- Status chips (P2+): `surface-2` pill, semantic color text
  (`success` done, `accent` active, `warning` needs-you, `muted`
  waiting).
