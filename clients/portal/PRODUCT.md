# LOLA Borrower Portal — product notes (M12)

The borrower-facing counterpart to the officer console. One person, one
job per visit: **finish the application, send the documents, see where
things stand.** Everything on screen is written for someone who has
never seen a loan origination system — plain language, no industry
terms, no internal states.

## Voice

- Guided, not demanding — the portal tells the borrower the next step,
  not the whole process.
- Reassuring about data — "we show your SSN partially for your safety"
  beats silently showing `•••`.
- Honest about waiting — "Under review" with a human timeframe beats a
  fake progress bar.

## Rules

- Masked-confirm PII — a borrower sees `•••6789`, never the full value;
  re-entering replaces, it never edits the mask.
- No officer vocabulary — never `POSIntake`, `PPE`, `TRID`, `correction`,
  `version`. Server-side `BorrowerStatusMap` is the single vocabulary
  source.
- Sessions are short — 30-min idle, 4h absolute; expiring is a feature
  (borrower devices are shared), not an error.
