# LOLA M4 — Tenant data plane (MPE-68)

Physically isolated, encrypted, migration-managed data plane — one
SQLCipher SQLite file per tenant. Much of the substrate shipped in
M2/M3 (file creation, wrapped keys, pragmas, context-scoped factory,
`ITenantKeyManager.DestroyAsync`); M4 builds the delta.

## Decisions (locked for the milestone)

- **Migrations:** provision-time step for new tenants +
  `POST /api/admin/migrations` fleet rollout; per-tenant status in the
  control plane. DbUp journals inside each tenant file stay
  authoritative for applied state.
- **Backups:** `VACUUM INTO` from the keyed connection → timestamped
  file under `data/backups/<tenant>/` + sha256 manifest.
- **Erasure:** `TenantErasureService` mechanics + guarded
  `DELETE /api/admin/tenants/{id}`; keys destroyed before files
  (invariant 9).
- **Repository seam:** generic `TenantRepository` base class —
  **provisional**; M5's real MISMO repos may reshape it (noted on
  MPE-69).

## Phases

| Phase | Scope | ADRs |
|---|---|---|
| 1 | Tenant migration machinery — `tenant_migrations` table, keyed `IConnectionManager`, `TenantMigrationRunner` (per-tenant + fleet, per-tenant failure isolation), `data-file → migrate` pipeline step, `POST /api/admin/migrations` | ADR-0002 (DbUp), ADR-0003 |
| 2 | `TenantRepository` base + `SchemaInfoRepository`; isolation proofs — wrong-key reject, cross-tenant impossibility, `secure_delete` byte-scan, FK enforcement | ADR-0003, ADR-0008 |
| 3 | `ITenantBackup`/`TenantBackupService` — `VACUUM INTO` + manifest; guarded `POST .../backup`, `GET .../backups`, `POST .../restore` | ADR-0003, ADR-0007 |
| 4 | `TenantErasureService` + guarded `DELETE`; `docs/data-plane-runbook.md` (ACL + WAL concurrency); marquee e2e; milestone hardening gate | ADR-0003, inv. 9 |

## Acceptance mapping

| Ticket bullet | Proof |
|---|---|
| Migrations roll across N files w/ per-tenant status | Phase 1 rollout test: 3 tenants incl. pre-version file → all latest, tracking rows; failure isolated per tenant |
| Restored backup opens only with correct tenant key | Phase 3: restore-verify + wrong-key `file is not a database` |
| `secure_delete` verified | Phase 2: marker insert → delete → VACUUM → byte-scan file+wal/shm → absent |
| Cross-tenant access impossible | Phase 2: no tenant-id param anywhere (structural) + A's key can't open B's file |

Milestone ends with the required hardening gate on MPE-68.
