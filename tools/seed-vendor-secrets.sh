#!/usr/bin/env bash
# Seed vendor credentials into OpenBao KV for dev/e2e (MPE-228).
# Writes `lola-kv/vendors/{tenant}/{kind}` = {api_key, webhook_secret}
# — secrets live in KV only, never the repo (invariant 7).
#
# Usage: ./tools/seed-vendor-secrets.sh [tenant] [kind...]
#   tenant   default "demo"
#   kind     any of credit|voe|voi|flood|fraud|aus (default: all)
#
# Env: OPENBAO_ADDR (default http://localhost:8300),
#      OPENBAO_TOKEN or OPENBAO_TOKEN_FILE (default
#      data/openbao/root-token).
set -euo pipefail

ADDR="${OPENBAO_ADDR:-http://localhost:8300}"
TOKEN="${OPENBAO_TOKEN:-$(cat "$(dirname "$0")/../data/openbao/root-token" 2>/dev/null || true)}"
if [ -z "$TOKEN" ]; then
    echo "no OpenBao token — set OPENBAO_TOKEN or run the dev stack first" >&2
    exit 1
fi

TENANT="${1:-demo}"; shift || true
KINDS=("$@")
if [ ${#KINDS[@]} -eq 0 ]; then
    KINDS=(credit voe voi flood fraud aus)
fi

rand() { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'; }

for kind in "${KINDS[@]}"; do
    api_key="stub-key-$(rand | head -c 16)"
    webhook_secret="whsec-$(rand)"
    curl -sf -X POST \
        -H "X-Vault-Token: $TOKEN" \
        -H "Content-Type: application/json" \
        -d "{\"data\":{\"api_key\":\"$api_key\",\"webhook_secret\":\"$webhook_secret\"}}" \
        "$ADDR/v1/lola-kv/data/vendors/$TENANT/$kind" >/dev/null
    echo "$TENANT/$kind  webhook_secret=$webhook_secret"
done
