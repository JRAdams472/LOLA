#!/usr/bin/env python3
"""Seed synthetic demo applications for wiki screenshots.

Runs inside the isolated `lolashots` compose project (docker-compose.e2e.yml
`seed` profile) or on the host against any LOLA API:

    API_URL=http://localhost:8200 python tools/wiki-shots/seed_demo.py

PII RULE: every borrower below is unmistakably synthetic — Demo/Test
names, 000-xx-xxxx-area SSNs (bare 9-digit form) (the 000 area is never issued), example.test
emails. Never put real or realistic PII in this file.

The flow: PKCE auth-code login as the dev officer (the same OIDC dance
the console performs) -> create + merge-patch drafts -> submit one ->
drive fee eval / pricing / rate lock / an le-package through the STUB
adapters the e2e stack runs by default.
"""

import base64
import hashlib
import json
import os
import secrets
import sys
import time
import urllib.parse

import requests

API = os.environ.get("API_URL", "http://localhost:8200").rstrip("/")
EMAIL = os.environ.get("OFFICER_EMAIL", "officer@lola.dev")
PASSWORD = os.environ.get("OFFICER_PASSWORD", "lola-officer-dev")
TENANT = os.environ.get("TENANT", "demo")
# The console client's registered callback — the seeder never follows it,
# it only needs the `code` param OpenIddict puts on the Location.
REDIRECT_URI = "http://localhost:3200/callback"
CLIENT_ID = "lola-officer-console"
SCOPE = "openid profile offline_access lola-api"


def login() -> str:
    """Auth-code + PKCE against the API's own authorize endpoint."""
    s = requests.Session()
    verifier = secrets.token_urlsafe(64)
    challenge = (
        base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest())
        .decode()
        .rstrip("=")
    )
    r = s.get(
        f"{API}/connect/authorize",
        params={
            "client_id": CLIENT_ID,
            "redirect_uri": REDIRECT_URI,
            "response_type": "code",
            "scope": SCOPE,
            "state": secrets.token_urlsafe(16),
            "code_challenge": challenge,
            "code_challenge_method": "S256",
        },
        allow_redirects=True,
    )
    # Lands on /account/login?returnUrl=<authorize-url>
    parsed = urllib.parse.urlparse(r.url)
    return_url = urllib.parse.parse_qs(parsed.query).get("returnUrl", ["/"])[0]
    r = s.post(
        f"{API}/account/login",
        data={"email": EMAIL, "password": PASSWORD, "returnUrl": return_url},
        allow_redirects=False,
    )
    assert r.status_code == 302, f"login POST -> {r.status_code}"
    # The api runs ASPNETCORE_ENVIRONMENT=Production in the shots stack, so
    # lola.auth lands with `Secure` — which http.cookiejar then refuses to
    # send back over plain http. Browsers treat localhost as a trustworthy
    # origin and send it anyway; for this script, just un-flag the cookie.
    for c in s.cookies:
        c.secure = False
    # Follow the 302 back through authorize — now carrying lola.auth —
    # until the Location is the client callback carrying `code`.
    loc = r.headers["Location"]
    for _ in range(6):
        if loc.startswith(REDIRECT_URI):
            break
        r = s.get(urllib.parse.urljoin(API, loc), allow_redirects=False)
        loc = r.headers.get("Location", "")
    code = urllib.parse.parse_qs(urllib.parse.urlparse(loc).query)["code"][0]
    tok = s.post(
        f"{API}/connect/token",
        data={
            "grant_type": "authorization_code",
            "client_id": CLIENT_ID,
            "redirect_uri": REDIRECT_URI,
            "code": code,
            "code_verifier": verifier,
        },
    )
    tok.raise_for_status()
    return tok.json()["access_token"]


def api(method: str, path: str, token: str, **kw):
    r = requests.request(
        method,
        f"{API}{path}",
        headers={"authorization": f"Bearer {token}"},
        timeout=30,
        **kw,
    )
    if r.status_code >= 400:
        raise SystemExit(f"{method} {path} -> {r.status_code}: {r.text[:300]}")
    return r


def gql(token: str, query: str, variables: dict):
    r = api("POST", "/graphql", token, json={"query": query, "variables": variables})
    body = r.json()
    if body.get("errors"):
        raise SystemExit(f"graphql errors: {body['errors'][:1]}")
    return body["data"]


def create_and_patch(token: str, patch: dict) -> str:
    app = api("POST", f"/api/tenants/{TENANT}/applications", token, json={}).json()
    api(
        "PATCH",
        f"/api/tenants/{TENANT}/applications/{app['id']}",
        token,
        json={"patch": patch},
    )
    return app["id"]


def wait_eval(token: str, app_id: str, field: str, want: str, tries: int = 40):
    # `fees` is a comparison batch — its rows carry the status. Other
    # eval fields (pricing) return a single object with a top-level one.
    sel = "evals { status }" if field == "fees" else "status"
    for _ in range(tries):
        d = gql(
            token,
            f"query($t:String!,$id:String!){{ {field}(tenantId:$t,appId:$id){{ {sel} }} }}",
            {"t": TENANT, "id": app_id},
        )
        if field == "fees":
            if d[field] and any(e["status"] == want for e in d[field]["evals"]):
                return
        elif d[field] and d[field]["status"] == want:
            return
        time.sleep(0.75)
    raise SystemExit(f"{field} never reached {want}")


# ── synthetic borrowers (never realistic PII) ───────────────────────
def base_patch(first: str, last: str, ssn: str, income: str, loan: str, ltv: str) -> dict:
    return {
        "party[0].role": "borrower",
        "party[0].first_name": first,
        "party[0].last_name": last,
        "party[0].ssn": ssn,
        "party[0].dob": "1985-04-12",
        "party[0].email": f"{first.lower()}@example.test",
        "party[0].phone": "5550100",
        "party[0].credit_score": "760",
        "party[0].address[0].street": "1 Prime Plaza",
        "party[0].address[0].city": "Springfield",
        "party[0].address[0].state": "IL",
        "party[0].address[0].zip": "62701",
        "party[0].employment[0].employer_name": "Example Holdings",
        "party[0].employment[0].start_date": "2019-06-01",
        "party[0].employment[0].self_employed": "false",
        "party[0].employment[0].monthly_income": income,
        "asset[0].asset_type": "checking",
        "asset[0].institution": "First Bank",
        "asset[0].account_number": "000001",
        "asset[0].balance": "85000",
        "liability[0].liability_type": "auto",
        "liability[0].creditor": "Auto Finance Co",
        "liability[0].monthly_payment": "410",
        "liability[0].balance": "12000",
        "loan_amount": loan,
        "loan_purpose": "purchase",
        "amortization_type": "fixed",
        "loan_term_months": "360",
        "loan.base_amount": loan,
        "loan.ltv": ltv,
        "loan.lien_position": "first",
        "loan.product_type": "conventional",
        "subject_property.street": "1 Prime Plaza",
        "subject_property.city": "Springfield",
        "subject_property.state": "IL",
        "subject_property.county": "Sangamon",
        "subject_property.zip": "62701",
        "subject_property.est_value": "560000",
        "subject_property.purchase_price": "560000",
        "subject_property.property_type": "single_family",
        "subject_property.usage": "primary_residence",
        "subject_property.units": "1",
        "declaration.intent_to_occupy": "yes",
        "declaration.first_time_buyer": "false",
        "declaration.property_will_be": "primary_residence",
        "demographics.ethnicity": "not_provided",
        "demographics.race": "not_provided",
        "demographics.sex": "not_provided",
    }


def main() -> None:
    token = login()
    print("login ok")

    # Hero application — complete 1003, submitted, then driven through
    # fees/price/lock/package so every closing surface has content.
    hero = create_and_patch(
        token, base_patch("Demo", "Borrower", "000123456", "14200", "425000", "75.9")
    )
    print("hero draft", hero)
    api("POST", f"/api/tenants/{TENANT}/applications/{hero}/submit", token, json={})
    print("submitted")

    api("POST", f"/api/tenants/{TENANT}/applications/{hero}/fees", token, json={})
    wait_eval(token, hero, "fees", "done")
    print("fees done")

    api("POST", f"/api/tenants/{TENANT}/applications/{hero}/price", token, json={})
    wait_eval(token, hero, "pricing", "done")
    d = gql(
        token,
        """query($t:String!,$id:String!){ pricing(tenantId:$t,appId:$id){
            evalId result { products { productCode eligible
              offers { rate lockTermDays } } } } }""",
        {"t": TENANT, "id": hero},
    )
    ev = d["pricing"]
    product = next(p for p in ev["result"]["products"] if p["eligible"] and p["offers"])
    offer = product["offers"][0]
    api(
        "POST",
        f"/api/tenants/{TENANT}/applications/{hero}/lock",
        token,
        json={
            "evalId": ev["evalId"],
            "productCode": product["productCode"],
            "rate": offer["rate"],
            "lockTermDays": offer.get("lockTermDays"),
        },
    )
    print("locked", product["productCode"], offer["rate"])

    api(
        "POST",
        f"/api/tenants/{TENANT}/applications/{hero}/packages",
        token,
        json={"kind": "le-package", "reasonCodes": []},
    )
    print("package requested")

    # Two drafts for pipeline density — partial 1003s stay drafts.
    create_and_patch(
        token,
        {
            "party[0].role": "borrower",
            "party[0].first_name": "Test",
            "party[0].last_name": "Applicant",
            "party[0].ssn": "000987654",
            "loan_purpose": "rateTermRefi",
            "loan_amount": "310000",
            "subject_property.state": "IL",
            "subject_property.est_value": "410000",
        },
    )
    create_and_patch(
        token,
        {
            "party[0].role": "borrower",
            "party[0].first_name": "Sample",
            "party[0].last_name": "Homebuyer",
            "party[0].ssn": "000551212",
            "loan_purpose": "purchase",
            "loan_amount": "255000",
            "subject_property.state": "MO",
        },
    )
    print("seeded")


if __name__ == "__main__":
    main()
