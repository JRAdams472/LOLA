-- ISPPr vendor binding per tenant (M9 P2, MPE-190, ADR-0007 amended).
-- The tenant's ISPPr client/customer ids + the transit-wrapped tenant
-- X-API-Key live on the registry row — routing metadata, never
-- borrower PII. `isppr_key_wrapped` is `vault:` ciphertext under
-- `tenant-<id>`; `isppr_key_id` is kept so erasure can revoke the key
-- vendor-side (MPE-191) without ever seeing plaintext.
ALTER TABLE tenants ADD COLUMN isppr_client_id TEXT;
ALTER TABLE tenants ADD COLUMN isppr_customer_id TEXT;
ALTER TABLE tenants ADD COLUMN isppr_key_id TEXT;
ALTER TABLE tenants ADD COLUMN isppr_key_wrapped TEXT;
