# LOLA Officer Console — product context

**What it is:** the loan-officer/admin surface for LOLA's loan
origination pipeline. Officers create and correct 1003 applications,
watch TRID disclosure clocks, re-price and lock loans, recalculate and
override fees, and generate disclosure packages — all against the same
tenant data plane the API guards. A thin BFF over the .NET API — the
browser never sees an access token.

**Mode:** Operate. Scanability, consistency, and role clarity outrank
expression. These are staff doing repeated, regulated work — the
interface recedes.

**Audience:** two UI personas plus one machine role:

| Persona | Access |
|---|---|
| TenantAdmin | everything — user/group admin, invites, all PII classes plaintext |
| LoanOfficer | loan create/edit/correct/search, price/lock, fees, packages, reveal (audited); Ssn/Dob/Account masked by default |
| Service | client-credentials tokens; no UI session |

The shell makes authority legible at a glance: the header shows the
signed-in identity + roles, staff-only nav items hide for lesser roles,
and masked PII renders `•••xxxx` with an explicit, reasoned, audited
reveal — never a silent peek.

**Visual world:** GitHub-dark per sibling convention (MPEx/LOLA) —
near-black surfaces (`#0d1117`/`#161b22`), laser-blue accent (`#58a6ff`/
`#2f9cff`), `#30363d` borders, MUI/Emotion components. Restrained by
design: the palette is a locked house style, not a choice to revisit.

**Structure:** `/` pipeline, `/search` loan search + create, `/loans/{id}`
tabbed 1003 workspace, `/loans/{id}/closing` three-column costs & docs,
`/admin` staff surface. Deep links stay valid.

**House conventions (sibling rule-editor anatomy, verbatim):**

- **List → detail routes.** Dense tables; row → detail route.
- **State is a chip, not a guess.** Pipeline step + validation state are
  StatusChips; TRID countdown is its own severity-graded chip.
- **Lifecycle verbs are uniform.** Draft edits PATCH; a submitted
  application corrects by appending a new version — nothing ever
  "edits submitted" (invariant 6).
- **Density with reveal.** Compact tables; detail behind row navigation
  and expanders, not page hops.
- **Errors and empties name the next action.** ErrorPanel shows the
  API's problem-details message verbatim; empty states say what to do
  first.
- **Masked by default.** Any value the API masked stays masked in the
  UI; reveal is a dialog with a required reason, and the revealed view
  carries a persistent banner.
- **PII never rides a URL.** Blind-index search is a GraphQL POST body;
  query strings, logs, and history never see an SSN.
