"""Validate the schema-backed AUS fixtures in this directory.

Requires lxml. This is a fixture-development helper, not production code.
"""
from pathlib import Path
import sys

try:
    from lxml import etree
except ImportError as exc:  # pragma: no cover - helper diagnostic
    raise SystemExit("lxml is required: python -m pip install lxml") from exc

ROOT = Path(__file__).resolve().parent
MISMO_NS = "http://www.mismo.org/residential/2009/schemas"


def schema_validate(label: str, schema_path: Path, xml_path: Path, inner_message: bool = False) -> bool:
    schema = etree.XMLSchema(etree.parse(str(schema_path)))
    doc = etree.parse(str(xml_path))
    if inner_message:
        message = doc.getroot().find(f"{{{MISMO_NS}}}MESSAGE")
        if message is None:
            print(f"FAIL {label}: no inner mismo:MESSAGE found")
            return False
        doc = etree.ElementTree(message)
    ok = schema.validate(doc)
    if ok:
        print(f"PASS {label}")
    else:
        print(f"FAIL {label}")
        for err in schema.error_log[:10]:
            print(f"  {err}")
    return ok


def well_formed(label: str, xml_path: Path) -> bool:
    try:
        etree.parse(str(xml_path))
    except etree.XMLSyntaxError as err:
        print(f"FAIL {label}: {err}")
        return False
    print(f"PASS {label} (well-formed only; no authoritative public schema in fixture set)")
    return True


def main() -> int:
    checks = [
        schema_validate(
            "DU request / current Fannie DU wrapper",
            ROOT / "mismo" / "DU_Wrapper_3.4.0_B324.xsd",
            ROOT / "du" / "du-request-di-c01-public-copy.xml",
        ),
        schema_validate(
            "LPA v6.1 service request",
            ROOT / "lpa" / "schema-v6.1" / "LPALoanAssessmentServiceRequest_v1.0.0.xsd",
            ROOT / "lpa" / "lpa-request-synthetic.xml",
        ),
    ]
    for xml in sorted((ROOT / "lpa").glob("lpa-response-*.xml")):
        checks.append(
            schema_validate(
                f"{xml.name} inner MISMO MESSAGE",
                ROOT / "lpa" / "schema-v6.1" / "GSE_Schema_WrapperV1_1.xsd",
                xml,
                inner_message=True,
            )
        )
    for xml in sorted((ROOT / "du").glob("du-response-*.xml")):
        checks.append(well_formed(xml.name, xml))
    checks.append(well_formed("Freddie ULDD Phase 3 scenario", ROOT / "lpa" / "uldd-phase3-scenario1-authentic.xml"))
    checks.append(well_formed("MISMO iLAD wrapper sample", ROOT / "mismo" / "ilad-wrapper-sample-authentic.xml"))
    return 0 if all(checks) else 1


if __name__ == "__main__":
    sys.exit(main())
