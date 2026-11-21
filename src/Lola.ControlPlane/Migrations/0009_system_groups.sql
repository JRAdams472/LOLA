-- Protected system groups (MPE-273 / ADR-0005): invite acceptance
-- grants roles through per-tenant system groups identified by
-- (tenant_id, system_kind), never by an editable name. The index is
-- partial — ordinary groups carry NULL and are unaffected.
--
-- Backfill claims the stable identity ONLY for a legacy group whose
-- role set is exactly the expected singleton. A conflicting row
-- (reserved name, wrong roles) stays system_kind NULL — acceptance
-- then fails closed for operator reconciliation instead of silently
-- granting the wrong role.
ALTER TABLE groups ADD COLUMN system_kind TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_groups_tenant_system_kind
    ON groups(tenant_id, system_kind) WHERE system_kind IS NOT NULL;

UPDATE groups SET system_kind = 'borrower'
WHERE name = 'borrowers'
  AND (SELECT count(*) FROM group_roles gr
        WHERE gr.group_id = groups.id) = 1
  AND EXISTS (SELECT 1 FROM group_roles gr
        WHERE gr.group_id = groups.id AND gr.role = 'Borrower');

UPDATE groups SET system_kind = 'broker'
WHERE name = 'brokers'
  AND (SELECT count(*) FROM group_roles gr
        WHERE gr.group_id = groups.id) = 1
  AND EXISTS (SELECT 1 FROM group_roles gr
        WHERE gr.group_id = groups.id AND gr.role = 'Broker');

UPDATE groups SET system_kind = 'realtor'
WHERE name = 'realtors'
  AND (SELECT count(*) FROM group_roles gr
        WHERE gr.group_id = groups.id) = 1
  AND EXISTS (SELECT 1 FROM group_roles gr
        WHERE gr.group_id = groups.id AND gr.role = 'Realtor');
