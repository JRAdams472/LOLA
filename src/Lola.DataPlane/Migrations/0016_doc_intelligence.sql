-- Tenant data-plane doc intelligence (M18 P2).
-- The classifier's result rides on the vault doc row —
-- classification_json is vault: ciphertext like filename (the
-- extracted fields are PII-class, invariant 1); classified_utc
-- stamps when the classify step ran inside the scan drain.
ALTER TABLE vault_docs ADD COLUMN classification_json TEXT;
ALTER TABLE vault_docs ADD COLUMN classified_utc TEXT;
