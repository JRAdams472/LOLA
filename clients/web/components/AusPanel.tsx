import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";

import type { AusFinding, AusReport, AusVendorPosture } from "@/lib/aus";

const REC_COLOR: Record<string, "success" | "warning" | "error" | "default"> = {
  "approve-eligible": "success",
  accept: "success",
  refer: "warning",
  caution: "warning",
  ineligible: "error",
  "unable-to-evaluate": "error",
};

const SEVERITY_COLOR: Record<string, "error" | "warning" | "default"> = {
  fatal: "error",
  required: "error",
  warning: "warning",
};

const VENDOR_LABEL: Record<string, string> = {
  du: "Desktop Underwriter",
  lpa: "Loan Product Advisor",
};

function FindingChip({ finding }: { finding: AusFinding }) {
  const label = `${finding.vendorCode} ${finding.severity}`;
  return (
    <Tooltip
      title={
        finding.message +
        (finding.conditionId ? " — condition opened" : "")
      }
      arrow
    >
      <Chip
        size="small"
        color={SEVERITY_COLOR[finding.severity] ?? "default"}
        variant={finding.severity === "info" ? "outlined" : "filled"}
        label={label}
        sx={{ mr: 0.5, mb: 0.5 }}
        data-testid={`finding-${finding.vendorCode}`}
      />
    </Tooltip>
  );
}

function VendorCard({ posture }: { posture: AusVendorPosture }) {
  return (
    <Box sx={{ mb: 2 }} data-testid={`vendor-${posture.vendor}`}>
      <Box sx={{ display: "flex", alignItems: "center", gap: 1, mb: 0.5 }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {VENDOR_LABEL[posture.vendor] ?? posture.vendor}
        </Typography>
        <Chip
          size="small"
          color={REC_COLOR[posture.recommendation] ?? "default"}
          label={posture.recommendation}
          data-testid={`rec-${posture.vendor}`}
        />
        {posture.riskClass && (
          <Chip
            size="small"
            variant="outlined"
            label={`risk: ${posture.riskClass}`}
            data-testid={`risk-${posture.vendor}`}
          />
        )}
      </Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: "block", mb: 0.5 }}>
        {posture.caseId ? `case ${posture.caseId} · ` : ""}
        {posture.createdUtc?.slice(0, 19).replace("T", " ") ?? ""} ·{" "}
        {posture.findingsCount} finding{posture.findingsCount === 1 ? "" : "s"}
      </Typography>
      {posture.findings.length > 0 && (
        <Box sx={{ display: "flex", flexWrap: "wrap" }}>
          {posture.findings.map((f) => (
            <FindingChip key={`${f.vendorCode}-${f.message}`} finding={f} />
          ))}
        </Box>
      )}
    </Box>
  );
}

/// M25 P3 — AUS posture on the loan workspace. Presentational: the
/// report comes in as a prop; staff affordances only render when
/// `canAct`. Null report = resolver hidden (non-staff). Empty vendors
/// = no submissions yet — a clean empty state, never invented values.
export default function AusPanel({
  report,
  canAct = false,
  submitPending = false,
  onSubmit,
}: {
  report: AusReport | null | undefined;
  canAct?: boolean;
  submitPending?: boolean;
  onSubmit?: (vendor: "du" | "lpa") => void;
}) {
  const vendors = report?.vendors ?? [];
  const lineageCount = report?.lineage.length ?? 0;
  const blocking = vendors.flatMap((v) =>
    v.findings.filter((f) => f.severity === "fatal" || f.severity === "required"),
  );

  return (
    <Box data-testid="aus-panel">
      {blocking.length > 0 && (
        <Alert severity="warning" sx={{ mb: 1 }} data-testid="aus-blocking-banner">
          {blocking.length} finding{blocking.length === 1 ? "" : "s"} require
          action — conditions have been opened on this application.
        </Alert>
      )}

      {vendors.length === 0 && (
        <Typography
          variant="body2"
          color="text.secondary"
          data-testid="aus-empty"
        >
          No automated underwriting submissions yet.
        </Typography>
      )}

      {vendors.map((v) => (
        <VendorCard key={v.vendor} posture={v} />
      ))}

      {lineageCount > 0 && (
        <Typography
          variant="caption"
          color="text.secondary"
          data-testid="aus-lineage"
        >
          {lineageCount} submission{lineageCount === 1 ? "" : "s"} on record —
          results are append-only.
        </Typography>
      )}

      {canAct && (
        <Box sx={{ mt: 1.5, display: "flex", gap: 1 }}>
          <Button
            size="small"
            variant="contained"
            disabled={submitPending}
            onClick={() => onSubmit?.("du")}
            data-testid="submit-du"
          >
            Submit to DU
          </Button>
          <Button
            size="small"
            variant="outlined"
            disabled={submitPending}
            onClick={() => onSubmit?.("lpa")}
            data-testid="submit-lpa"
          >
            Submit to LPA
          </Button>
        </Box>
      )}
    </Box>
  );
}
