# AUS wire-format fixtures (DU / LPA / MISMO 3.4)

Harvested 2026-11-27. This directory is a **test fixture corpus**, not a
certification kit and not legal advice. Several upstream artifacts are
copyrighted implementation materials rather than open-source-licensed content;
review the license notes below before copying them outside this repository.

## Quick validation

The schema-backed fixtures were checked with `lxml` on 2026-11-27:

```text
PASS DU request / current Fannie DU wrapper
PASS LPA v6.1 service request
PASS lpa-response-accept-synthetic.xml inner MISMO MESSAGE
PASS lpa-response-caution-synthetic.xml inner MISMO MESSAGE
PASS du-response-approve-eligible-synthetic.xml (well-formed only)
PASS du-response-refer-with-caution-synthetic.xml (well-formed only)
PASS Freddie ULDD Phase 3 scenario (well-formed only)
PASS MISMO iLAD wrapper sample (well-formed only)
```

To rerun locally, install `lxml` and run:

```bash
python tests/fixtures/aus/validate_samples.py
```

The helper validates the three schema-backed fixtures and checks the remaining
XML files for well-formedness. It does **not** claim that the synthetic DU
Codified Findings files conform to Fannie Mae's subscriber-only CFF schema.

## Inventory and provenance

### `mismo/` — MISMO reference model and shared wrapper schemas

| File | Status | Source / notes |
| --- | --- | --- |
| `MISMO_3.4.0_B324.xsd` | Authentic, unmodified extraction | Official MISMO 3.4 B324 combined schema from `ReferenceModel_v3.4.0_B324/Combined/` in the publication zip. |
| `xlinkMISMOB324.xsd`, `xml.xsd` | Authentic, unmodified extraction | Same official combined-schema directory. |
| `MISMODataTypesB324.xsd`, `MISMOEnumeratedTypesB324.xsd`, `MISMOExtensionDetailsB324.xsd`, `xlink.xsd` | Authentic, unmodified extraction | Official split-schema satellite files. They are required because the current Fannie DU/ULAD extension files import the chameleon-style `MISMODataTypesB324.xsd` rather than importing the combined schema with a namespace prefix. |
| `DU_Wrapper_3.4.0_B324.xsd`, `DU_ExtensionV3_4.xsd`, `ULAD_ExtensionV3_4.xsd` | Authentic Fannie Mae files, obtained indirectly | Extracted without modification from `resources/Fannie Mae DU Schema Files updated 080825.zip` in the public third-party repo `https://github.com/eeexde/MISMO3.4Validator`. The original tiny zip is preserved at `du/fannie-mae-du-schema-files-updated-080825.zip`. |
| `ilad-wrapper-sample-authentic.xml` | Authentic, unmodified extraction | `iLAD_Wrapper_Sample_Test_Data_20200508.xml` from the public MISMO iLAD 2.0.1 materials zip. It is retained as a small official MISMO sample exercising ULAD/LPA/DU extension namespaces; its full wrapper schema set is not duplicated here. |

Official MISMO source zips:

- MISMO 3.4 B324 publication: <https://resources.mismo.org/work-products/files/mismo-version-3-4/v3-4-b324-cr-2016-01-28-publication-43cac18774.zip>  
  Downloaded zip SHA-256: `d54cb3660a83da66d0133c0274836ee31742d5de39eaf836beadaf743cc5f43b` (12,116,973 bytes). The large LDD reports and release-note collateral were not copied into this fixture corpus.
- MISMO iLAD 2.0.1 materials: <https://resources.mismo.org/work-products/files/industry-loan-application-dataset-ilad/ilad-materials-f109b75852.zip>  
  Downloaded zip SHA-256: `6f37a9f8a2f70a133de63ff3e3f06fb98b5ee0d5d0db1620199b767d3c55dc25` (1,990,779 bytes).

### `du/` — Desktop Underwriter fixtures

| File | Status | Source / notes |
| --- | --- | --- |
| `du-request-di-c01-public-copy.xml` | **Unofficial third-party sample; schema-valid; not GSE-published** | Copied from public gist <https://gist.github.com/baharalidurrani/6ae5951337488ae8659c04da144a67ad> (`mismo-sample.xml`, created 2023-01-18). The gist has no explicit license. This copy was normalized by trimming leading/trailing whitespace in text nodes and pretty-printing; the original contained leading spaces in some enumeration values. No borrower data was added. It validates against the current Fannie DU wrapper in `../mismo/`. |
| `fannie-mae-du-schema-files-updated-080825.zip` | Authentic Fannie Mae payload in a third-party public repo, byte-preserved | SHA-256: `326965cedb3889fe93d7dcb7ef7f386ee75d6926c097a68743f3a99f66f718bd` (5,589 bytes). Preserved as evidence of the exact schema bundle recovered indirectly. |
| `du-response-approve-eligible-synthetic.xml` | **SYNTHETIC / non-authoritative** | Invented fixture. See warning comments inside the file. |
| `du-response-refer-with-caution-synthetic.xml` | **SYNTHETIC / non-authoritative** | Invented negative-path fixture. See warning comments inside the file. |

Important DU response limitation: no authentic public DU Codified Findings
File (CFF) sample or schema was found. Fannie Mae states that draft DU message
files and CFFs are distributed by email/Fannie Mae Connect to **authorized
subscribers**. Consequently, the two response XML files here deliberately use
the publicly documented root name `CodifiedFindings` and some publicly
mentioned names (`DocumentationLevelCodes`, `RequiredDocumentationLevelCode`,
`SpecialFeatureCode`) but invent the detailed nesting. They are useful parser
plumbing fixtures only.

Useful public references:

- Ocrolus AUS upload documentation confirming root/copy-code names `CodifiedFindings` and `LPALoanAssessmentServiceResponse`: <https://docs.ocrolus.com/reference/upload-aus-xml>
- Fannie Mae DU Integration Impact Memo, Aug. 8 2025, including subscriber-only CFF distribution and `DocumentationLevelCodes` / `RequiredDocumentationLevelCode`: <https://singlefamily.fanniemae.com/media/43136/display>
- Fannie Mae XIS Services Integration Impact Memo, May 27 2026, including XML/RES/TEXT retirement on 2028-10-27 and JSON file type 18: <https://singlefamily.fanniemae.com/media/47151/display>
- Fannie Mae VantageScore update memo mentioning `SpecialFeatureCode` and the Credit Risk Score section in CFF: <https://singlefamily.fanniemae.com/media/48081/display>

### `lpa/` — Loan Product Advisor and ULDD fixtures

| File | Status | Source / notes |
| --- | --- | --- |
| `lpa-request-synthetic.xml` | **SYNTHETIC/DERIVED; schema-valid** | Fictional request data adapted from the public third-party MISMO sample; wrapped in `LPALoanAssessmentServiceRequest`. Validates against the official Freddie Mac v6.1 request XSD. |
| `lpa-response-accept-synthetic.xml` | **SYNTHETIC; inner MESSAGE schema-valid** | Hand-built Accept/Eligible response-shaped fixture. Its inner `mismo:MESSAGE` validates against `GSE_Schema_WrapperV1_1.xsd`. The outer `LPALoanAssessmentServiceResponse` root is reconstructed from public documentation because the public v6.1 **request** bundle does not include a response wrapper XSD. |
| `lpa-response-caution-synthetic.xml` | **SYNTHETIC; inner MESSAGE schema-valid** | Negative-path variant with Caution risk classification, FreddieMacIneligible, and mixed/not-eligible relief values. Same outer-wrapper caveat. |
| `uldd-phase3-scenario1-authentic.xml` | Authentic, unmodified extraction | `Scenario 1-Freddie Mac Phase 3 Refinance 30 Year - Joint Test Case.xml` from Freddie Mac ULDD Appendix C. Useful representative borrower/loan/property MISMO delivery XML. |
| `appendix-c-freddie-mac-xml-samples.zip` | Authentic archive, byte-preserved | Freddie Mac ULDD Appendix C zip, SHA-256: `52c9dff95e449535f5a74f6673c2b04018eb9a80301c77875dba2d555270849a` (55,477 bytes). Contains 40 ULDD Phase 3/4a/5 samples. |
| `lpa-specification-bulletin-v6.1.pdf` | Authentic, unmodified extraction, renamed | Official `LPA Specification Bulletin v6.1.00_12-11-25.pdf`. |
| `lpa-v6.1-xml-request-file-data-requirements.xlsx` | Authentic, unmodified extraction, renamed | Official `LPA v6.1 XML Request File Data Requirements_January_2026.xlsx`. |
| `schema-v6.1/*.xsd` | Authentic schema content, repackaged flat | Copied from the official Freddie Mac v6.1 request requirements zip. Files were flattened from `lpa/common` and `lpa/request` into one directory; the only textual edit was replacing `schemaLocation="../common/` with `schemaLocation="` so the flattened set resolves. Because `schema-v6.1` is two levels below `tests/fixtures/aus/`, the unmodified `../../mismo/` references resolve to the shared schema files in `tests/fixtures/aus/mismo/`. |

Freddie Mac source zips:

- LPA v6.1 request requirements: <https://sf.freddiemac.com/docs/zip/loan-product-advisor-v6.1.00-request-file-requirements.zip>  
  Downloaded zip SHA-256: `5685b38f165626e0aaea270924e8e0b36f2cffc7e216b8bf40ae54fecbbdb96c` (1,529,870 bytes).
- ULDD Appendix C samples: <https://sf.freddiemac.com/docs/zip/update/appendix_c_freddie_mac_xml_samples.zip>  
  Downloaded zip SHA-256: `52c9dff95e449535f5a74f6673c2b04018eb9a80301c77875dba2d555270849a` (55,477 bytes).

## Wire-format facts encoded by these fixtures

### Shared MISMO 3.4 application/AUS payload

- Root data payload: `MESSAGE` in namespace `http://www.mismo.org/residential/2009/schemas`.
- Reference model attribute: `MISMOReferenceModelIdentifier="3.4.032420160128"`.
- Common extension namespace URIs:
  - DU: `http://www.datamodelextension.org/Schema/DU`
  - ULAD: `http://www.datamodelextension.org/Schema/ULAD`
  - LPA: `http://www.datamodelextension.org/Schema/LPA`
- Typical path shape: `MESSAGE / ABOUT_VERSIONS / DEAL_SETS / DEAL_SET / DEALS / DEAL / {ASSETS,COLLATERALS,EXPENSES,LIABILITIES,LOANS,PARTIES,...}`.
- In the GSE wrapper, `ABOUT_VERSIONS` is mandatory and first. Validating a bare sample against the unwrapped MISMO model can miss that GSE requirement.
- Relationship/arc support uses xlink attributes; many payload elements carry optional `SequenceNumber` and xlink labels.

### DU submission

- DU request fixtures are ordinary MISMO `MESSAGE` documents validated through `DU_Wrapper_3.4.0_B324.xsd` rather than a separate service-envelope root in this corpus.
- `DU_Wrapper_3.4.0_B324.xsd` uses `xsd:redefine`. With `lxml`, the practical layout that compiled and validated was the official **combined** `MISMO_3.4.0_B324.xsd` plus the split satellite datatype files required by the Fannie extension schemas. This is a schema-processor/layout gotcha worth preserving.
- The original gist sample's leading spaces inside enumeration text were schema-invalid; the checked-in copy trims text-node whitespace.

### LPA service-to-service request and response

- Request root: `LPALoanAssessmentServiceRequest` in namespace `http://www.freddiemac.com/lpa/LPALoanAssessmentService/schemas`, wrapping one `mismo:MESSAGE`.
- Public evidence indicates the response root is `LPALoanAssessmentServiceResponse` in the same service namespace, also wrapping `mismo:MESSAGE`.
- The synthetic response models feedback messages under `DEAL / SERVICES / SERVICE / AUTOMATED_UNDERWRITING_SYSTEM / AUTOMATED_UNDERWRITING_SYSTEM_RESPONSE / AUTOMATED_UNDERWRITING_SYSTEM_RESPONSE_MESSAGES` and loan-level decision data under `LOAN / UNDERWRITING / AUTOMATED_UNDERWRITINGS / AUTOMATED_UNDERWRITING`. This layout is schema-valid against the public GSE wrapper, but it is not attested by an official public response instance.
- LPA extension response fields are nested through extension containers such as `EXTENSION / OTHER / LPA:AUTOMATED_UNDERWRITING_EXTENSION`; exact prefix spelling is insignificant, but the namespace URI is significant.
- Response concepts represented in the synthetic file include `LPATransactionIdentifier`, `LPAServiceEngineTransactionIdentifier`, submission count/date, `Accept` risk class, `FreddieMacEligible`, and asset/income/employment/collateral representation-and-warranty relief eligibility.
- LPA feedback-message detail/items use `LPA:AUTOMATED_UNDERWRITING_SYSTEM_RESPONSE_MESSAGE_EXTENSION` beneath the core response-message extension.
- Publicly observed relief enums: asset/income/employment = `Eligible`, `NotEligible`, `Partial`, `Unavailable`; collateral adds `Pending` in v6.1.
- LPA version lifecycle noted during harvest: v5.3 retired March 2026; v5.4 retires March 2027; v6.0 retires March 2028; v6.1 is current in the harvested bundle.

### DU findings output

- DU findings output is **not** a MISMO `MESSAGE`; public third-party documentation identifies the XML copy code/root as `CodifiedFindings`.
- Fannie Mae is introducing JSON underwriting findings and states that current RES/TEXT/XML formats retire 2028-10-27. XML file type 5 is therefore legacy-transition material, not a long-term integration target.
- Because CFF schema/examples are subscriber-gated, only the synthetic files in `du/` should be used until an authorized authentic sample is available.

## Data privacy / fixture-data status

These are test documents, not production loan files. The ULDD and iLAD files
are vendor-published test scenarios and contain obvious dummy identifiers
(such as repeated or sequential taxpayer-ID-shaped values). The DU/LPA request
fixtures likewise use fictional names, accounts, addresses, and identifiers.
They should still be treated as schema-shaped sensitive data in downstream
tooling: do not replace the dummy values with real consumer data in this
repository, and do not emit these files or values into logs/traces.

## Licensing and redistribution notes

- MISMO schemas and iLAD materials are governed by the MISMO Product End-User License and MISMO Intellectual Property Rights Policy. See <https://www.mismo.org/about-MISMO/policies-and-procedures>. The schema files themselves contain the license/disclaimer notice and must retain it.
- Fannie Mae and Freddie Mac implementation files are copyrighted GSE materials made available as integration resources; no open-source license was found in the harvested zips. Treat them as proprietary implementation artifacts preserved for local conformance/parser testing.
- The GitHub gist used for the DU request has no explicit license. Its data appears fictional, but its provenance is unofficial.
- The files explicitly named `*-synthetic.xml` were created for this fixture corpus and contain only fictional data.

## Dead ends / gaps encountered

1. Direct downloads from `singlefamily.fanniemae.com` were blocked by Cloudflare in the harvest environment. The current Fannie DU schema bundle was recovered indirectly from a public GitHub repo; its zip is preserved for traceability.
2. No authentic public DU `CodifiedFindings` XML instance or schema was found. Fannie Mae distributes CFF materials to authorized subscribers.
3. No authentic public LPA service response XML instance was found. Freddie Mac's fuller service-to-service specification material appears Developer Portal/access gated.
4. The public LPA v6.1 request zip contains the request wrapper XSD but not a matching response wrapper XSD. Therefore only the response's inner `mismo:MESSAGE` is schema-validated here.
