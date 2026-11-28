"""Dependency-free validation for the credit/AVM fixture corpus.

This checks well-formedness and the contract-test invariants encoded in the
synthetic fixtures. It also performs a lightweight DTD shape audit (all element
and attribute names used must exist in the harvested MISMO DTD), but it is not
a full DTD validator.
"""
from pathlib import Path
import json
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
REPORTS = {
    "equifax": ("equifax-sample.xml", "FICO 10T"),
    "transunion": ("transunion-sample.xml", "VantageScore 4.0"),
    "experian": ("experian-sample.xml", "FICO 10T"),
}


def fail(message: str) -> None:
    print(f"FAIL {message}")
    sys.exit(1)


def parse_report(key: str):
    filename, expected_model = REPORTS[key]
    try:
        tree = ET.parse(ROOT / filename)
    except ET.ParseError as exc:
        fail(f"{filename} is not well-formed: {exc}")
    root = tree.getroot()
    if root.tag != "RESPONSE_GROUP":
        fail(f"{filename} root is {root.tag}, expected RESPONSE_GROUP")
    trades = root.findall(".//CREDIT_LIABILITY")
    if len(trades) != 5:
        fail(f"{filename} has {len(trades)} tradelines, expected 5")
    scores = root.findall(".//CREDIT_SCORE")
    if len(scores) != 1 or scores[0].get("_ModelNameTypeOtherDescription") != expected_model:
        fail(f"{filename} does not carry expected distinct model tag {expected_model!r}")
    for trade in trades:
        for attr in (
            "_AccountIdentifier",
            "_AccountOpenedDate",
            "_AccountReportedDate",
            "_AccountStatusType",
            "_AccountType",
            "_MonthlyPaymentAmount",
            "_UnpaidBalanceAmount",
        ):
            if trade.get(attr) is None:
                fail(f"{filename} tradeline {trade.get('CreditLiabilityID')} lacks {attr}")
        if trade.find("_CREDITOR") is None or not trade.find("_CREDITOR").get("_Name"):
            fail(f"{filename} tradeline {trade.get('CreditLiabilityID')} lacks creditor name")
        if trade.find("_PAYMENT_PATTERN") is None:
            fail(f"{filename} tradeline {trade.get('CreditLiabilityID')} lacks payment pattern")
    dates = [x.get("_Date") for x in root.findall(".//_PRIOR_ADVERSE_RATING")]
    # Month-precision fixture convention: 2024-10 and earlier are outside the
    # 2024-11-15 24-month boundary; 2024-12 and later are inside. No fixture
    # uses ambiguous 2024-11.
    if not any(d <= "2024-10" for d in dates):
        fail(f"{filename} has no out-of-window late")
    if not any(d >= "2024-12" for d in dates):
        fail(f"{filename} has no in-window late")
    print(f"PASS {filename}: well-formed; 5 tradelines; score model {expected_model}; in/out window lates")
    return root


def rows_for(reports, account):
    rows = []
    for bureau, root in reports.items():
        for trade in root.findall(".//CREDIT_LIABILITY"):
            if trade.get("_AccountIdentifier") == account:
                rows.append(
                    (
                        bureau,
                        trade.find("_CREDITOR").get("_Name"),
                        trade.get("_UnpaidBalanceAmount"),
                        trade.get("_AccountReportedDate"),
                    )
                )
    return rows


def audit_dtd_shape(reports) -> None:
    """Check every used element/attribute name exists in the harvested DTD.

    This intentionally does not attempt content-model or datatype validation;
    it catches spelling/placement drift in the synthetic files without adding a
    dependency such as lxml.
    """
    dtd_path = ROOT / "mismo" / "CREDIT_RESPONSE_v2_3_1.dtd"
    dtd = dtd_path.read_text(encoding="utf-8", errors="replace")
    elements = set(re.findall(r"<!ELEMENT\s+([^\s>]+)", dtd))
    attlists = {}
    for match in re.finditer(r"<!ATTLIST\s+([^\s>]+)([\s\S]*?)>", dtd):
        entity, body = match.group(1), match.group(2)
        attrs = []
        for line in body.splitlines():
            stripped = line.strip()
            if not stripped or stripped.startswith(("(", "|", ")", "#")):
                continue
            token = stripped.split()[0]
            if re.match(r"^[A-Za-z_][A-Za-z0-9_.-]*$", token) and token not in attrs:
                attrs.append(token)
        attlists[entity] = set(attrs)

    errors = []
    for filename, root in ((REPORTS[key][0], root) for key, root in reports.items()):
        for element in root.iter():
            if element.tag not in elements:
                errors.append(f"{filename}: element {element.tag} not in DTD")
            allowed = attlists.get(element.tag, set())
            for attr in element.attrib:
                if attr not in allowed:
                    errors.append(f"{filename}: {element.tag}@{attr} not in DTD ATTLIST")
    if errors:
        fail("DTD shape audit failed: " + "; ".join(errors[:10]))
    print("PASS MISMO v2.3.1 DTD shape audit: all used element/attribute names exist")


def main() -> None:
    reports = {key: parse_report(key) for key in REPORTS}
    audit_dtd_shape(reports)

    try:
        ET.parse(ROOT / "malformed-truncated-credit.xml")
        fail("malformed-truncated-credit.xml unexpectedly parsed")
    except ET.ParseError:
        print("PASS malformed-truncated-credit.xml: expected XML parse failure")

    try:
        avm = json.loads((ROOT / "avm-response-sample.json").read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        fail(f"avm-response-sample.json is invalid JSON: {exc}")
    value = avm[0]["property/value"]["result"]["value"]
    for key in ("fsd", "price_lwr", "price_mean", "price_upr"):
        if key not in value:
            fail(f"AVM value lacks {key}")
    print("PASS avm-response-sample.json: valid JSON with HouseCanary-shaped value/FSD keys")

    capital = rows_for(reports, "****7788")
    if len(capital) != 3 or len({r[2] for r in capital}) != 3 or len({r[3] for r in capital}) != 3:
        fail("shared Capital One account does not appear in all bureaus with distinct balances/report dates")
    print(f"PASS shared-account conflicts: {capital}")

    mortgage = rows_for(reports, "****0456")
    if len(mortgage) != 3 or len({r[2] for r in mortgage}) != 3:
        fail("shared mortgage account lacks three-bureau balance conflicts")
    print(f"PASS mortgage conflicts: {mortgage}")

    expected_uniques = {
        "equifax": "****3300",
        "transunion": "****4402",
        "experian": "****6505",
    }
    for bureau, account in expected_uniques.items():
        rows = rows_for(reports, account)
        if len(rows) != 1 or rows[0][0] != bureau:
            fail(f"{account} is not unique to {bureau}: {rows}")
    print("PASS bureau-unique tradelines: Equifax ****3300, TransUnion ****4402, Experian ****6505")

    names = {r[1] for r in capital}
    if not {"CAPITAL ONE", "CAPITAL ONE NA", "CAPITAL ONE BANK"}.issubset(names):
        fail(f"Capital One spelling variants missing: {sorted(names)}")
    print("PASS creditor-normalization spellings: CAPITAL ONE / CAPITAL ONE NA / CAPITAL ONE BANK")
    print("ALL FIXTURE CHECKS PASSED")


if __name__ == "__main__":
    main()
