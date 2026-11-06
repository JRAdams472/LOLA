# ADR 0007: adapter: seams

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

External services — adapter seams

## Consequences

All outbound integrations go through adapter interfaces so vendors swap via DTS-style transform with zero core changes: IPricingService (MPEx first), IFeeService (itemized fees → LE sections), IDocumentService (ISPPr first), IVendorAdapter (credit/VOE/flood/fraud/AUS stubs). Egress contracts: PII-free to MPEx (reflection-guarded firewall); minimal-template-fields to ISPPr. E-signature is ISPPr-side future work, not LOLA scope.
# ADR Honors the MVP modularity requirement; vendor selection deferred to a later plan. All service calls are OIDC client-credentials + TLS.

**Status:** Accepted (locked — MPE-63 megaplan)

## Amendment (MPE-72 — document vendor auth)

The "all service calls are OIDC client-credentials" line is amended:

- **OIDC client-credentials remains preferred** wherever a vendor
  supports it (MPEx/Dex already does).
- Where a vendor has no OIDC surface, a **vendor-native credential**
  is permitted behind the same adapter seam — ISPPr's tenant-scoped
  `X-API-Key` is the first such case. The credential lives ONLY in
  the adapter's HTTP layer; core logic sees `IDocumentService`
  exactly as before.
- Vendor-native credentials are issued per tenant at provisioning,
  stored **OpenBao-wrapped** (never plaintext in the tenant file,
  never in source), never logged or traced (invariant 3), and
  revoked on tenant erasure.

Everything else stands: adapter seams, egress minimization,
TLS on every hop.

