import { useState } from "react";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";

import type { ToleranceReport, ToleranceViolation } from "@/lib/closing";

const money = (n: number | null | undefined) =>
  n == null ? "—" : `$${n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

const BUCKET_LABEL: Record<string, string> = {
  Zero: "0% — cannot increase",
  TenPercent: "10% cumulative",
  Unlimited: "no tolerance limit",
};

const BUCKET_ORDER = ["Zero", "TenPercent", "Unlimited"];

function bucketColor(bucket: string, violations: ToleranceViolation[]) {
  if (violations.some((v) => v.bucket === bucket)) return "error";
  if (bucket === "Zero") return "warning";
  if (bucket === "TenPercent") return "info";
  return "default";
}

/// M21 P3 — TRID tolerance posture on the closing-costs surface.
/// Presentational: the report comes in as a prop; the cure affordance
/// only renders when `canCure` (staff role) AND a condition is open.
export default function TolerancePanel({
  report,
  canCure = false,
  curePending = false,
  onRecordCure,
}: {
  report: ToleranceReport;
  canCure?: boolean;
  curePending?: boolean;
  onRecordCure?: (reason: string) => void;
}) {
  const [cureOpen, setCureOpen] = useState(false);
  const [reason, setReason] = useState("");
  const open = report.openCureConditionId != null;
  const buckets = [...report.buckets].sort(
    (a, b) => BUCKET_ORDER.indexOf(a.bucket) - BUCKET_ORDER.indexOf(b.bucket),
  );

  return (
    <Box data-testid="tolerance-panel">
      {report.violations.length > 0 && (
        <Alert severity="error" sx={{ mb: 1 }} data-testid="tolerance-violation-banner">
          TRID tolerance violation — a cure must be recorded before this
          loan can proceed.
          {report.violations.map((v) => (
            <Typography
              key={`${v.bucket}-${v.feeCode ?? "aggregate"}-${v.rule}`}
              variant="body2"
              component="div"
            >
              {BUCKET_LABEL[v.bucket] ?? v.bucket}
              {v.feeCode ? ` · ${v.feeCode}` : ""}: {money(v.baseline)} →{" "}
              {money(v.compare)} (allowed {money(v.allowed)})
            </Typography>
          ))}
        </Alert>
      )}

      {report.violations.length === 0 && buckets.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          Within tolerance — no increases over the LE baseline.
        </Typography>
      )}

      {buckets.map((b) => {
        const delta = b.compare - b.baseline;
        const up = delta > 0;
        return (
          <Box
            key={b.bucket}
            data-testid={`tolerance-bucket-${b.bucket}`}
            sx={{ display: "flex", alignItems: "center", gap: 1, py: 0.5 }}
          >
            <Chip
              size="small"
              variant="outlined"
              color={bucketColor(b.bucket, report.violations)}
              label={BUCKET_LABEL[b.bucket] ?? b.bucket}
            />
            <Typography variant="body2" sx={{ flexGrow: 1 }}>
              {money(b.baseline)} → {money(b.compare)}
            </Typography>
            <Tooltip
              title={b.lines
                .map((l) => `${l.feeCode}: ${money(l.baseline)} → ${money(l.compare)}`)
                .join("\n")}
            >
              <Typography
                variant="body2"
                color={up ? "error.main" : "success.main"}
                data-testid={`tolerance-delta-${b.bucket}`}
              >
                {up ? "+" : ""}
                {money(delta)}
              </Typography>
            </Tooltip>
          </Box>
        );
      })}

      {report.resets.length > 0 && (
        <Typography variant="caption" color="text.secondary">
          {report.resets.length} changed-circumstance reset
          {report.resets.length === 1 ? "" : "s"} recorded
        </Typography>
      )}

      {open && canCure && (
        <Button
          size="small"
          variant="outlined"
          sx={{ mt: 1 }}
          onClick={() => setCureOpen(true)}
          data-testid="record-cure-button"
        >
          Record cure
        </Button>
      )}

      <Dialog open={cureOpen} onClose={() => setCureOpen(false)}>
        <DialogTitle>Record tolerance cure</DialogTitle>
        <DialogContent>
          <TextField
            autoFocus
            required
            fullWidth
            label="Reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            sx={{ mt: 1 }}
            slotProps={{ htmlInput: { "data-testid": "cure-reason" } }}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setCureOpen(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!reason.trim() || curePending}
            onClick={() => {
              onRecordCure?.(reason.trim());
              setCureOpen(false);
              setReason("");
            }}
            data-testid="cure-submit"
          >
            Record
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
