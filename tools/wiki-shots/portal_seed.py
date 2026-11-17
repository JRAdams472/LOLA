"""Seed the borrower-portal story into the lolashots stack.

Run after `seed_demo.py` (which creates the officer + hero app):
    python tools/wiki-shots/portal_seed.py

Flow: officer issues an invite → borrower accepts via the public API →
borrower PKCE-logs in → files a complete application (draft → submit) →
uploads a vault document (waits for ClamAV clean) → officer prices,
locks, evaluates fees, and generates the LE package so the borrower's
Documents page shows both halves. A second, unaccepted invite is left
behind for the invite-acceptance screenshot.

Everything here is synthetic — the borrower is "Demo Portal" at a
.test address, SSNs are 000-prefixed fakes.
"""

import io
import time
import urllib.parse
from pathlib import Path

import requests

from seed_demo import API, TENANT, api, base_patch, gql, login, wait_eval

PORTAL_CLIENT = "lola-borrower-portal"
PORTAL_REDIRECT = "http://localhost:3300/callback"
BORROWER_EMAIL = "portal-demo@example.test"
BORROWER_NAME = "Demo Portal"
BORROWER_PASSWORD = "wiki-portal-demo-pass"

# Minimal valid PDF — the allowlist sniffs %PDF magic bytes; ClamAV
# passes a clean text payload through quarantine.
PDF_BYTES = (
    b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n"
    b"2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
    b"3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\n"
    b"trailer<</Root 1 0 R>>\n%%EOF\n"
)


def create_invite(officer: str, email: str) -> str:
    r = api(
        "POST",
        f"/api/admin/tenants/{TENANT}/invites",
        officer,
        json={"email": email},
    )
    url = r.json()["inviteUrl"]
    return urllib.parse.parse_qs(urllib.parse.urlparse(url).query)["token"][0]


def accept_invite(token: str) -> None:
    r = requests.post(
        f"{API}/api/invites/accept",
        json={
            "token": token,
            "displayName": BORROWER_NAME,
            "password": BORROWER_PASSWORD,
        },
        timeout=30,
    )
    if r.status_code >= 400:
        raise SystemExit(f"invite accept -> {r.status_code}: {r.text[:300]}")


def upload_pdf(borrower: str, app_id: str, classification: str, name: str) -> str:
    r = requests.post(
        f"{API}/api/tenants/{TENANT}/applications/{app_id}/uploads",
        params={"classification": classification},
        headers={"authorization": f"Bearer {borrower}"},
        files={"file": (name, io.BytesIO(PDF_BYTES), "application/pdf")},
        timeout=30,
    )
    if r.status_code >= 400:
        raise SystemExit(f"upload -> {r.status_code}: {r.text[:300]}")
    return r.json()["vaultDocId"]


def wait_clean(borrower: str, app_id: str, tries: int = 60) -> None:
    for _ in range(tries):
        rows = api(
            "GET",
            f"/api/tenants/{TENANT}/applications/{app_id}/uploads",
            borrower,
        ).json()
        if rows and all(r["status"] in ("clean", "infected") for r in rows):
            bad = [r["filename"] for r in rows if r["status"] != "clean"]
            if bad:
                raise SystemExit(f"uploads rejected: {bad}")
            return
        time.sleep(1)
    raise SystemExit("uploads never reached a terminal scan state")


def main() -> None:
    officer = login()
    print("officer ok")

    # Borrower invite → public accept → portal-credential login.
    token = create_invite(officer, BORROWER_EMAIL)
    accept_invite(token)
    print("invite accepted")

    borrower = login(
        BORROWER_EMAIL, BORROWER_PASSWORD, PORTAL_CLIENT, PORTAL_REDIRECT
    )
    print("borrower ok")

    # A complete, submitted application owned by the borrower.
    app_id = api(
        "POST", f"/api/tenants/{TENANT}/applications", borrower, json={}
    ).json()["id"]
    api(
        "PATCH",
        f"/api/tenants/{TENANT}/applications/{app_id}",
        borrower,
        json={
            "patch": base_patch(
                "Demo", "Portal", "000222444", "11800", "392000", "80.0"
            )
        },
    )
    upload_pdf(borrower, app_id, "bank-statement", "bank-statement.pdf")
    wait_clean(borrower, app_id)
    print("upload clean")

    api(
        "POST",
        f"/api/tenants/{TENANT}/applications/{app_id}/submit",
        borrower,
        json={},
    )
    print("submitted", app_id)

    # Officer drives the package so "Documents from us" has content.
    api("POST", f"/api/tenants/{TENANT}/applications/{app_id}/fees", officer, json={})
    wait_eval(officer, app_id, "fees", "done")
    api("POST", f"/api/tenants/{TENANT}/applications/{app_id}/price", officer, json={})
    wait_eval(officer, app_id, "pricing", "done")
    d = gql(
        officer,
        """query($t:String!,$id:String!){ pricing(tenantId:$t,appId:$id){
            evalId result { products { productCode eligible
              offers { rate lockTermDays } } } } }""",
        {"t": TENANT, "id": app_id},
    )
    ev = d["pricing"]
    product = next(p for p in ev["result"]["products"] if p["eligible"] and p["offers"])
    offer = product["offers"][0]
    api(
        "POST",
        f"/api/tenants/{TENANT}/applications/{app_id}/lock",
        officer,
        json={
            "evalId": ev["evalId"],
            "productCode": product["productCode"],
            "rate": offer["rate"],
            "lockTermDays": offer.get("lockTermDays"),
        },
    )
    api(
        "POST",
        f"/api/tenants/{TENANT}/applications/{app_id}/packages",
        officer,
        json={"kind": "le-package", "reasonCodes": []},
    )
    print("package requested")

    # A second, partial draft — the wizard + "Application in progress"
    # dashboard card need an in-flight application to render.
    draft_id = api(
        "POST", f"/api/tenants/{TENANT}/applications", borrower, json={}
    ).json()["id"]
    api(
        "PATCH",
        f"/api/tenants/{TENANT}/applications/{draft_id}",
        borrower,
        json={
            "patch": {
                "party[0].role": "borrower",
                "party[0].first_name": "Demo",
                "party[0].last_name": "Portal",
                "party[0].ssn": "000222444",
                "party[0].email": BORROWER_EMAIL,
                "loan_purpose": "purchase",
                "loan_amount": "392000",
            }
        },
    )
    print("draft", draft_id)

    # A live (unaccepted) invite for the acceptance-page screenshot —
    # handed to portal-capture.mjs through the out dir, along with the
    # submitted app's id (the documents page defaults to the draft).
    pending = create_invite(officer, "portal-pending@example.test")
    url = f"http://localhost:3300/invites/accept?token={urllib.parse.quote(pending)}"
    out = Path(__file__).parent / "out"
    out.mkdir(exist_ok=True)
    (out / "pending-invite.txt").write_text(url)
    (out / "submitted-app.txt").write_text(app_id)
    print("portal seed done")


if __name__ == "__main__":
    main()
