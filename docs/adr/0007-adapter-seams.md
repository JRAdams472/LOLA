# ADR 0007: adapter: seams

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

External services — adapter seams

## Consequences

All outbound integrations go through adapter interfaces so vendors swap via DTS-style transform with zero core changes: IPricingService (MPEx first), IFeeService (itemized fees → LE sections), IDocumentService (ISPPr first), IVendorAdapter (credit/VOE/flood/fraud/AUS stubs). Egress contracts: PII-free to MPEx (reflection-guarded firewall); minimal-template-fields to ISPPr. E-signature is ISPPr-side future work, not LOLA scope.
# ADR Honors the MVP modularity requirement; vendor selection deferred to a later plan. All service calls are OIDC client-credentials + TLS.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


