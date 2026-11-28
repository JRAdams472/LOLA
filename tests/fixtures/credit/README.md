# Credit bureau / tri-merge and AVM wire-format fixtures

Harvested 2026-11-28. This directory is a **contract-test fixture corpus**, not
a bureau certification kit and not legal or compliance advice. The nationwide
bureaus' production wire formats and several implementation materials are
licensee/subscriber materials. The synthetic files here prove LOLA parser,
normalization, merge-conflict, late-history extraction, score-model retention,
and malformed-input behavior; they do **not** prove interoperability with
Equifax, TransUnion, Experian, a credit reseller, or an AVM vendor.

All consumer data in the synthetic fixtures is fictional and intentionally
unrealistic: `TEST TRIMERGE FIXTURE`, SSN `999-00-0001`, birth date
`1900-01-01`, address `0000 SYNTHETIC WAY`, ZIP `00000`, and masked account
identifiers such as `****7788`.

## Quick validation

The dependency-free validation helper was run on 2026-11-28:

```text
PASS equifax-sample.xml: well-formed; 5 tradelines; score model FICO 10T; in/out window lates
PASS transunion-sample.xml: well-formed; 5 tradelines; score model VantageScore 4.0; in/out window lates
PASS experian-sample.xml: well-formed; 5 tradelines; score model FICO 10T; in/out window lates
PASS MISMO v2.3.1 DTD shape audit: all used element/attribute names exist
PASS malformed-truncated-credit.xml: expected XML parse failure
PASS avm-response-sample.json: valid JSON with HouseCanary-shaped value/FSD keys
PASS shared-account conflicts: [('equifax', 'CAPITAL ONE', '4125', '2026-10'), ('transunion', 'CAPITAL ONE NA', '3890', '2026-11'), ('experian', 'CAPITAL ONE BANK', '4302', '2026-09')]
PASS mortgage conflicts: [('equifax', 'FIRST SYNTHETIC MORTGAGE', '286400', '2026-10'), ('transunion', 'FIRST SYNTHETIC MORTGAGE', '285975', '2026-11'), ('experian', 'FIRST SYNTHETIC MTG', '287105', '2026-09')]
PASS bureau-unique tradelines: Equifax ****3300, TransUnion ****4402, Experian ****6505
PASS creditor-normalization spellings: CAPITAL ONE / CAPITAL ONE NA / CAPITAL ONE BANK
ALL FIXTURE CHECKS PASSED
```

Rerun from the repository root:

```bash
python tests/fixtures/credit/validate_fixtures.py
```

The helper checks XML/JSON well-formedness and fixture-specific invariants. It
is not a DTD/XSD validator. `malformed-truncated-credit.xml` is the deliberate
exception to the well-formedness rule and must fail XML parsing.

## Inventory and provenance

### `mismo/` — authentic public MISMO schema artifacts

| File | Status | Source / notes |
| --- | --- | --- |
| `CREDIT_RESPONSE_v2_3_1.dtd` | Authentic, unmodified extraction | Official MISMO Credit Reporting v2.3.1 package. SHA-256 `9daa6ce2d18491ec992edd8ef70205f7b8b3843edacbd209536451190bae8779` (63,525 bytes). |
| `SMART_DOCUMENT_xmldsig_core_schema_V_1_02.dtd` | Authentic, unmodified extraction | Same package; XMLDSig DTD used by the MISMO SMART Document envelope. SHA-256 `3718f0f243fac7172933ed081dde1d06df930ab79669f348560c6d515cf14a8e` (8,318 bytes). |

Official source:

- MISMO Version 2 product index: <https://www.mismo.org/standards-resources/mismo-product/mismo-version-2>
- Credit Reporting v2.3.1 zip: <https://resources.mismo.org/work-products/files/mismo-version-2/credit-reporting-v2-3-1-7cdf03f9e2.zip>
  - Downloaded zip SHA-256: `d1f4d96d141fac870ccbb73dfd85154eb247f3e4247cb1526250c49fcfb253fd` (103,916 bytes).

The zip also contains `Brick(CREDIT_RESPONSE_v2_3_1).xml`, an official MISMO
sample response. That sample was inspected for element order, attribute style,
tradeline shape, payment-pattern examples, and score nesting, but it was **not
copied** because it contains realistic-shaped personal identifiers and account
numbers, while this corpus has an explicit no-realistic-PII constraint. It is
also a copyrighted standards artifact, not public-domain material merely
because it is publicly downloadable.

### Bureau-shaped XML fixtures

| File | Status | Evidence that shaped it |
| --- | --- | --- |
| `equifax-sample.xml` | **SYNTHETIC / non-authoritative** | MISMO v2.3.1 response DTD and official Brick sample structure; Xactus MISMO 2 API field documentation; Equifax public XML/JSON comparison guide for evidence that native Equifax XML is a distinct legacy format. Carries repository source `Equifax` and synthetic score tag `FICO 10T`. |
| `transunion-sample.xml` | **SYNTHETIC / non-authoritative** | Same MISMO structure, with TransUnion encoded as `CreditRatingCodeType="Other"` + `CreditRatingCodeTypeOtherDescription="TransUnion"` because the v2.3.1 DTD enum has only `Equifax`, `Experian`, and `Other`. Publicly indexed TUXML documentation confirms TransUnion also has a distinct native XML schema. Carries synthetic score tag `VantageScore 4.0`. |
| `experian-sample.xml` | **SYNTHETIC / non-authoritative** | Same MISMO structure, with repository source `Experian`; public Experian Connect/API documentation was used only as evidence that native Experian schemas use different field names/date encodings. Carries synthetic score tag `FICO 10T`. |
| `malformed-truncated-credit.xml` | **SYNTHETIC negative fixture** | Prefix derived from the synthetic MISMO-shaped Equifax fixture, deliberately truncated inside the first `CREDIT_LIABILITY`. It must not parse. |

Important interpretation: these three bureau files are **single-repository
MISMO-shaped extracts**. They are not byte-level native Equifax FFF/XML,
TransUnion TUXML, or Experian NetConnect/ARF/INProfile files. That is the only
responsible structure to use without licensee-only bureau specifications, and
it is also a realistic shape for credit data surfaced through mortgage credit
vendors that normalize repository data to MISMO.

Public evidence used:

- Xactus Credit ReportX API reference, MISMO 2 response field tree: <https://developer.xactus.com/apis/creditapis/creditreport_mismo2/other/create-credit-report>
- Xactus response-transport notes showing `RESPONSE_GROUP / RESPONSE / RESPONSE_DATA / CREDIT_RESPONSE` and optional embedded PDF/HTML report files: <https://developer.xactus.com/apimd/creditapis/a4_mismo2_cr_receive_response>
- Xactus supported MISMO versions 2.1, 2.1.1, 2.3, and 2.3.1: <https://developer.xactus.com/apimd/creditapis/a1_mismo2_cr_digest>
- Public TazWorks XML example showing a real gateway embedding MISMO credit response data: <https://tazworks.com/api/api-basic/xml-examples/>
- Unofficial mirror of the MISMO XML Implementation Guide: Credit Reporting Version 2: <https://silo.tips/download/xml-implementation-guide-credit-reporting-version-2>
- TransUnion TUXML code-sample index (publicly indexed; direct fetch was 403 in the harvest environment): <https://techservices.transunion.com/doc-repository/tuxmlCodeSamples/credit-report-samples>
- TransUnion user-guide/schema index (publicly indexed; direct fetch was 403): <https://techservices.transunion.com/doc-repository/user-guides-manuals>
- Experian Connect credit-profile field documentation: <https://connectapi.experian.com/api/doc/credit-profile.html>

### Equifax public implementation evidence — cited, not copied

Equifax hosts a public comparison guide for legacy STS/TotalView XML and the
newer JSON API at
<https://assets.equifax.com/acro-migration-kb/assets/Equifax_JSON_STS_TotalView_6.0_5.0_XML_Version_Comparison%2003-2022.pdf>.
The guide was reachable during harvest (1,296,165 bytes; observed SHA-256
`3f40efab6268fb4648ebef78fbd32ae0c278ec947e3a988c4eb12cfb59ea2fee`) but is not
copied into this corpus because it is a large proprietary implementation guide
rather than a wire fixture. Useful facts visible in the public indexing/document
text: legacy Equifax consumer credit existed in Full File Fixed and XML
formats; the newer API uses JSON fields rather than XML elements; response data
ordering follows prior ACRO versions; and JSON trade output can include the
legacy 6.0 trade fields. It reinforces why the synthetic `equifax-sample.xml`
cannot honestly claim to be native Equifax XML.

### `avm-response-sample.json`

**SYNTHETIC / non-authoritative.** Shaped on public HouseCanary response
examples and public Freddie Mac HVE explanations:

- Public gist containing a HouseCanary-shaped response with top-level array,
  `address_info`, literal slash key `property/value`, `api_code`,
  `api_code_description`, and `result.value.{fsd,price_lwr,price_mean,price_upr}`:
  <https://gist.github.com/alettieri/1bac7d00347b0838882b25d290198d94/2a77a5e6ab390a7870a0d954e7c0f2e34c16e95a>
- HouseCanary API guide explaining `/v2/property/value`, JSON array responses,
  confidence intervals, and FSD: <https://www.housecanary.com/blog/real-estate-data-api-for-developers>
- HouseCanary valuation technical brief: confidence score is `1 - FSD`; both
  are often scaled by 100: <https://www.housecanary.com/images/65b19749ac0988693d5fde36_hc_valuation_technical-brief.pdf>
- Freddie Mac HVE public sample/report explanation showing model type, low,
  point, high, confidence score, and FSD: <https://solutionsforlenders.com/wp-content/uploads/2014/07/hve.pdf>
- Freddie Mac FSD discussion: FSD is a proportional forecast standard
  deviation; roughly 68% of errors are expected within plus/minus one FSD:
  <https://sf.freddiemac.com/docs/pdf/fact-sheets/dougwhitepaper_metricsmatter.pdf>

The fixture uses the HouseCanary-shaped JSON keys exactly where public evidence
exists (`property/value`, `price_mean`, `price_lwr`, `price_upr`, `fsd`) and
adds clearly marked synthetic normalization-test fields (`valuation_date`,
`model.name`, `model.version`, `confidence_score`, `comp_count`). The address
is synthetic. `confidence_score` is `0.94`, consistent with `1 - 0.06`.

### `validate_fixtures.py`

Repository-local, dependency-free validation helper created for this corpus.
It checks structure and invariants plus a lightweight DTD shape audit (all
element and attribute names used in the synthetic XML exist in the harvested
DTD). It does not perform full DTD content-model or datatype validation.

## Wire-format facts encoded by these fixtures

### MISMO v2 credit-response envelope

- Root: `RESPONSE_GROUP` with `MISMOVersionID="2.3.1"`.
- Typical path: `RESPONSE_GROUP / RESPONSE / RESPONSE_DATA / CREDIT_RESPONSE`.
- v2 is attribute-heavy and does not use the MISMO v3 `MESSAGE` nesting or the
  v3 namespace shape.
- `RESPONSE_GROUP` may end in a `Signature` element; the harvested package
  includes the XMLDSig DTD for that SMART Document envelope support.
- `CREDIT_RESPONSE` child order in the v2.3.1 DTD is significant for DTD
  validation. Relevant order: `_DATA_INFORMATION`, `CREDIT_BUREAU`,
  `CREDIT_REPORT_PRICE*`, `CREDIT_REPOSITORY_INCLUDED`, `REQUESTING_PARTY`,
  `CREDIT_REQUEST_DATA`, `CREDIT_ERROR_MESSAGE*`, `BORROWER*`,
  `CREDIT_LIABILITY*`, public records/inquiries/files/scores, then comments,
  referrals, summaries, regulatory products, and embedded files.
- A response may embed a rendered PDF or HTML report in `EMBEDDED_FILE`; Xactus
  publicly documents that transport behavior. These fixtures omit embedded
  files to keep parser tests focused on structured data.

### Tradelines

- Tradeline element: `CREDIT_LIABILITY`.
- Core account facts are attributes, not child elements:
  - `_AccountIdentifier`
  - `_AccountOpenedDate`
  - `_AccountReportedDate`
  - `_AccountBalanceDate`
  - `_AccountStatusDate`
  - `_AccountStatusType`
  - `_AccountType`
  - `_HighCreditAmount`
  - `_CreditLimitAmount`
  - `_MonthlyPaymentAmount`
  - `_MonthsReviewedCount`
  - `_PastDueAmount`
  - `_UnpaidBalanceAmount`
  - `CreditBusinessType`
  - `CreditLoanType`
- Creditor name is nested at `CREDIT_LIABILITY / _CREDITOR/@_Name`.
- Repository provenance is nested at `CREDIT_LIABILITY / CREDIT_REPOSITORY`
  with `_SourceType` and `_SubscriberCode`.
- Account typing is split:
  - `_AccountType` is broad (`Mortgage`, `Revolving`, `Installment`,
    `CreditLine`, `Open`, `Unknown`).
  - `CreditLoanType` carries detail such as
    `ConventionalRealEstateMortgage`, `CreditCard`, `Automobile`,
    `Educational`, `Unsecured`, `ChargeAccount`, or
    `CheckCreditOrLineOfCredit`.
- The fixtures therefore represent the requested types as:
  - mortgage: `_AccountType="Mortgage"`, `CreditLoanType="ConventionalRealEstateMortgage"`
  - revolving/credit card: `_AccountType="Revolving"`, `CreditLoanType="CreditCard"`
  - auto: `_AccountType="Installment"`, `CreditLoanType="Automobile"`
  - student: `_AccountType="Installment"`, `CreditLoanType="Educational"`
  - personal/installment: `_AccountType="Installment"`, `CreditLoanType="Unsecured"`

### Payment history and delinquency

The public MISMO v2 implementation guide documents two complementary forms:

1. `_PAYMENT_PATTERN` — a compact monthly grid/string.
   - `_StartDate` is the month represented by the first character.
   - Each subsequent character moves backward one month.
   - Characters include `C` current, `1` through `6` months delinquent,
     `7` wage-earner plan, `8` repossession/foreclosure, `9`
     collection/charge-off, `N` no activity, and `X` no data.
   - Thus the string is reverse-chronological; do not read it forward.
2. `_PRIOR_ADVERSE_RATING` — one element per dated delinquent month, with
   `_Date` and `_Type` such as `Late30Days`, `Late60Days`, `Late90Days`.
3. `_LATE_COUNT` carries aggregate `_30Days`, `_60Days`, `_90Days`, and
   optional `_120Days`; nested `PERIODIC_LATE_COUNT` uses `FirstYear`,
   `SecondYear`, and `ThirdYear` buckets.
4. `_HIGHEST_ADVERSE_RATING` and `_MOST_RECENT_ADVERSE_RATING` can summarize
   severity/recency separately.

Fixture dates use the month precision commonly present in this format
(`YYYY-MM`). The report datetime is `2026-11-15T12:00:00Z`; the fixture key
`FixtureLookbackBoundaryDate` is `2024-11-15`. Because adverse dates are only
month-precision, fixtures intentionally avoid `2024-11`: months `2024-12` and
later are unambiguously inside the 24-month window, while `2024-10` and earlier
are unambiguously outside it.

`PERIODIC_LATE_COUNT` boundaries are not defined by the DTD itself. In these
fixtures they are rolling 12-month buckets backward from report month
`2026-11`; parsers under test should use `_PRIOR_ADVERSE_RATING` for exact
lookback extraction rather than inferring exact dates from the aggregate
buckets.

### Scores and model tags

- Score element: `CREDIT_SCORE`.
- Important attributes: `CreditRepositorySourceType`, `_Date`, `_Value`,
  `_ModelNameType`, `_ModelNameTypeOtherDescription`.
- Factors are nested as `CREDIT_SCORE / _FACTOR` with `_Code` and `_Text`.
- MISMO v2.3.1 predates FICO 10T and VantageScore 4.0. The fixtures therefore
  use the standard extension escape hatch:
  - `_ModelNameType="Other"`
  - `_ModelNameTypeOtherDescription="FICO 10T"` or `"VantageScore 4.0"`
- Merge logic must preserve `_ModelNameTypeOtherDescription`; collapsing every
  score to `Other` loses the model distinction the fixtures are designed to
  test.

### Repository/rating-code quirks

- v2.3.1 `CreditRatingCodeType` enumerates only `Equifax`, `Experian`, and
  `Other`; there is no literal `TransUnion` enum value.
- The TransUnion fixture therefore uses
  `CreditRatingCodeType="Other"` and
  `CreditRatingCodeTypeOtherDescription="TransUnion"`.
- `CreditRepositorySourceType` and `CREDIT_REPOSITORY/@_SourceType` do include
  literal `TransUnion`, so do not copy the rating-code quirk into repository
  provenance handling.

### Deliberate merge conflicts

Report month is `2026-11` unless the tradeline's own `_AccountReportedDate`
says otherwise.

| Canonical account | Equifax | TransUnion | Experian | Intended test |
| --- | --- | --- | --- | --- |
| `****7788` revolving | `CAPITAL ONE`, balance 4125, reported 2026-10 | `CAPITAL ONE NA`, balance 3890, reported 2026-11 | `CAPITAL ONE BANK`, balance 4302, reported 2026-09 | exact account-mask match across three bureaus, creditor-name normalization, distinct balances and report dates |
| `****0456` mortgage | balance 286400, reported 2026-10 | balance 285975, reported 2026-11 | `FIRST SYNTHETIC MTG`, balance 287105, reported 2026-09 | three-bureau mortgage conflict plus abbreviation normalization |
| `****2100` auto | `FIFTH THIRD AUTO`, balance 11920, reported 2026-09 | `5/3 AUTO FINANCE`, balance 11780, reported 2026-10 | `FIFTH THIRD BANK AUTO`, balance 12025, reported 2026-08 | punctuation/abbreviation normalization and stale-data conflict |
| `****9901` installment | `SYNTHETIC FINANCE CO`, balance 6310 | `SYNTHETIC FINANCE COMPANY`, balance 6255 | `SYNTHETIC FINANCE CO`, balance 6405 | suffix expansion plus conflicting balances |
| `****3300` retail | present, closed | absent | absent | Equifax-unique tradeline |
| `****4402` credit line | absent | present | absent | TransUnion-unique tradeline |
| `****6505` student | absent | absent | present | Experian-unique tradeline |

The delinquency data also intentionally disagrees by bureau. For example, the
shared Capital One tradeline has a June 2025 30-day late in Equifax and
TransUnion but not in Experian, while the mortgage has severe early-2025
delinquency in Equifax, a February 2026 30-day late in TransUnion, and no
adverse entries in Experian.

### Adjacent Fannie Mae format evidence

Fannie Mae publishes a separate **Credit Report Data Format & Reference
Tables** integration guide at
<https://singlefamily.fanniemae.com/media/15086/display>. Public indexing shows
that it is a fixed-position MORNETPlus-style data stream with segment/position
/length tables (`HDR-010`, data stream `H001`, position, length, segment
occurrence), not the MISMO XML shape used here. Direct download was blocked by
Cloudflare in the harvest environment, so no copy is included.

Fannie's public selling-guide credit-report requirements still corroborate the
fixture field selection: creditor name, date opened, high credit, current
status, required payment, unpaid balance, payment history, and last-updated
dates are required; payment history must include number of times past due and
dates of delinquencies. See
<https://selling-guide.fanniemae.com/sel/b3-5.2-01/requirements-credit-reports>.

The MISMO credit-report reissue recommendation distinguishes repository raw
files from credit-bureau merged reports and uses the credit-report identifier
for reissue/status workflows. An unofficial mirrored copy is at
<https://silo.tips/download/credit-report-reissue-recommendation-table-of-contents>.

## Data privacy / fixture-data status

These are test documents, not production consumer files. Do not replace the
synthetic identifiers with real consumer data in this repository, do not emit
fixture contents into logs/traces, and do not treat the synthetic creditor
names as evidence about real creditors. Real creditor names are used only
because the user explicitly required normalization-test spelling variants such
as `CAPITAL ONE` versus `CAPITAL ONE NA`; all accounts, balances, and payment
histories are invented.

## Licensing and redistribution notes

- MISMO DTD/schema materials are copyrighted MISMO materials governed by the
  MISMO end-user license referenced inside the files. They are copied
  unmodified, including their license/disclaimer notices. Public availability
  is not the same as public domain.
- The Equifax comparison PDF is copyrighted implementation material hosted
  publicly by Equifax; no open-source license was found. It is cited but not
  redistributed in this corpus.
- The public HouseCanary gist has no explicit license. Only its response shape
  informed the synthetic AVM fixture; no real address, valuation, or response
  body was copied.
- Xactus, TazWorks, TransUnion, Experian, Fannie Mae, and Freddie Mac pages
  were used as evidence citations. Their content is not reproduced wholesale.
- All `*-sample.xml` files at the directory root, the malformed XML fixture,
  the AVM JSON fixture, and `validate_fixtures.py` were created for this
  corpus and contain only fictional data.

## Dead ends / gaps encountered

1. No authentic public native Equifax consumer-credit XML instance was found.
   Equifax's public developer material confirms legacy Full File Fixed/XML and
   newer JSON APIs, but detailed API references and test cases are portal/
   customer gated.
2. TransUnion publicly indexes TUXML user guides, schema 2.45, and TruVision
   credit-report request/response samples, but the host returned HTTP 403 in
   this environment and the actual sample/schema files could not be harvested.
3. Experian's public Connect documentation exposes useful credit-profile field
   names and a JSON sample, but no authoritative public US mortgage-bureau XML
   response instance was found.
4. The official MISMO v2.3.1 Brick response sample is public and authentic,
   but not copied because it contains realistic-shaped PII/account values and
   because public availability does not make it public domain.
5. MISMO v2.3.1 score-model enums predate FICO 10T and VantageScore 4.0. The
   fixtures intentionally use the standard `Other` + other-description escape
   hatch rather than invalid enum literals.
6. No authentic public HouseCanary production response was copied. The exact
   JSON shape is supported by a public gist and HouseCanary documentation, but
   the fixture values and metadata are synthetic.
7. Freddie Mac HVE evidence is mostly human-report/feedback-certificate
   documentation rather than a standalone public machine-readable AVM schema.
   LPA v6.0 documentation also says the HVE document request enumeration was
   removed from that XML response flow, so HVE evidence here is conceptual
   rather than a target integration format.
