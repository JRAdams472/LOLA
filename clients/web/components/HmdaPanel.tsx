import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";

import type { HmdaEdit, HmdaLarRow, HmdaReport } from "@/lib/hmda";

const ACTION_TAKEN: Record<string, string> = {
  "1": "originated",
  "2": "approved, not accepted",
  "3": "denied",
  "4": "withdrawn",
  "5": "incomplete",
  "6": "purchased",
  "7": "preapproval denied",
  "8": "preapproval approved, not accepted",
};

const EDIT_RESULT_COLOR: Record<string, "error" | "warning" | "success"> = {
  fail: "error",
  warn: "warning",
  pass: "success",
};

const fmtDate = (yyyymmdd: string | null | undefined) =>
  yyyymmdd && yyyymmdd.length === 8
    ? `${yyyymmdd.slice(0, 4)}-${yyyymmdd.slice(4, 6)}-${yyyymmdd.slice(6, 8)}`
    : yyyymmdd || "—";

function EditChip({ edit }: { edit: HmdaEdit }) {
  return (
    <Tooltip
      title={edit.detail || `${edit.editId} — ${edit.result}`}
      arrow
      disableHoverListener={edit.result === "pass"}
    >
      <Chip
        size="small"
        color={EDIT_RESULT_COLOR[edit.result] ?? "success"}
        variant={edit.result === "pass" ? "outlined" : "filled"}
        label={`${edit.editId} ${edit.result}`}
        sx={{ mr: 0.5, mb: 0.5 }}
        data-testid={`edit-${edit.editId}`}
      />
    </Tooltip>
  );
}

function EditTier({
  title,
  edits,
  testid,
}: {
  title: string;
  edits: HmdaEdit[];
  testid: string;
}) {
  const fails = edits.filter((e) => e.result === "fail").length;
  const warns = edits.filter((e) => e.result === "warn").length;
  return (
    <Box sx={{ mb: 1.5 }} data-testid={testid}>
      <Typography variant="body2" sx={{ mb: 0.5 }}>
        {title} — {fails > 0 ? `${fails} failing` : warns > 0 ? `${warns} warnings` : "clean"}
      </Typography>
      <Box sx={{ display: "flex", flexWrap: "wrap" }}>
        {edits.map((e) => (
          <EditChip key={e.editId} edit={e} />
        ))}
      </Box>
    </Box>
  );
}

/// M24 P3 — HMDA posture on the loan workspace. Presentational: the
/// report comes in as a prop; staff affordances only render when
/// `canAct`. Null report = resolver hidden (non-staff).
export default function HmdaPanel({
  report,
  canAct = false,
  assemblePending = false,
  onAssemble,
  onExport,
  onFreeze,
  freezePending = false,
}: {
  report: HmdaReport | null | undefined;
  canAct?: boolean;
  assemblePending?: boolean;
  freezePending?: boolean;
  onAssemble?: () => void;
  onExport?: () => void;
  onFreeze?: () => void;
}) {
  const latest = report?.latest ?? null;
  const figures = report?.figures;
  const lineageCount = report?.lineage.length ?? 0;

  const fields = (row: HmdaLarRow | null): Record<string, string> => {
    if (!row) return {};
    try {
      return JSON.parse(row.fieldsJson) as Record<string, string>;
    } catch {
      return {};
    }
  };
  const latestFields = fields(latest);

  const editsByTier = (tier: string) =>
    (latest?.edits ?? []).filter((e) => e.tier === tier);

  return (
    <Box data-testid="hmda-panel">
      {figures && figures.failCount > 0 && (
        <Alert severity="error" sx={{ mb: 1 }} data-testid="hmda-fail-banner">
          {figures.failCount} validity edit{figureCountPlural(figures.failCount)}{" "}
          failing — the LAR row is not fileable until these clear.
        </Alert>
      )}

      <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
        Loan application register (Reg C)
      </Typography>

      {!latest && (
        <Typography
          variant="body2"
          color="text.secondary"
          sx={{ mb: 1 }}
          data-testid="hmda-empty"
        >
          No assembled LAR row yet
          {canAct ? " — assemble one to posture this loan." : "."}
        </Typography>
      )}

      {latest && (
        <>
          <Box sx={{ display: "flex", gap: 3, flexWrap: "wrap", mb: 1.5 }} data-testid="hmda-figures">
            <Box>
              <Typography variant="caption" color="text.secondary">ULI</Typography>
              <Typography variant="body2" data-testid="hmda-uli" sx={{ wordBreak: "break-all" }}>
                {latest.uli}
              </Typography>
            </Box>
            <Box>
              <Typography variant="caption" color="text.secondary">Action taken</Typography>
              <Typography variant="body2" data-testid="hmda-action">
                {figures?.actionTaken
                  ? `${ACTION_TAKEN[figures.actionTaken] ?? figures.actionTaken} (${fmtDate(figures.actionTakenDate)})`
                  : "NA"}
              </Typography>
            </Box>
            <Box>
              <Typography variant="caption" color="text.secondary">Rate spread</Typography>
              <Typography variant="body2" data-testid="hmda-spread">
                {figures?.rateSpread != null ? `${figures.rateSpread} pp` : "NA"}
              </Typography>
            </Box>
            <Box>
              <Typography variant="caption" color="text.secondary">HOEPA</Typography>
              <Typography variant="body2" data-testid="hmda-hoepa">
                {figures?.hoepaStatus === "1"
                  ? "high-cost"
                  : figures?.hoepaStatus === "2"
                    ? "not high-cost"
                    : "NA"}
              </Typography>
            </Box>
            <Box>
              <Typography variant="caption" color="text.secondary">Loan amount</Typography>
              <Typography variant="body2" data-testid="hmda-amount">
                {latestFields.loan_amount && latestFields.loan_amount !== "NA"
                  ? `$${Number(latestFields.loan_amount).toLocaleString()}`
                  : "NA"}
              </Typography>
            </Box>
          </Box>

          <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
            Edit checks
          </Typography>
          <EditTier title="Syntactical" edits={editsByTier("syntactical")} testid="tier-syntactical" />
          <EditTier title="Validity" edits={editsByTier("validity")} testid="tier-validity" />
          <EditTier title="Quality" edits={editsByTier("quality")} testid="tier-quality" />

          <Typography
            variant="caption"
            color="text.secondary"
            sx={{ display: "block", mb: 1 }}
            data-testid="hmda-lineage"
          >
            append-only assembly lineage: {lineageCount} row{lineageCount === 1 ? "" : "s"}
          </Typography>
        </>
      )}

      {canAct && (
        <Box sx={{ display: "flex", gap: 1, mt: 1 }}>
          <Button
            variant="contained"
            size="small"
            disabled={assemblePending}
            onClick={onAssemble}
            data-testid="assemble-btn"
          >
            {latest ? "Re-assemble LAR" : "Assemble LAR"}
          </Button>
          {latest && (
            <>
              <Button
                variant="outlined"
                size="small"
                onClick={onExport}
                data-testid="export-btn"
              >
                Export {latest.reportingYear}
              </Button>
              <Button
                variant="outlined"
                size="small"
                disabled={freezePending}
                onClick={onFreeze}
                data-testid="freeze-btn"
              >
                Freeze snapshot
              </Button>
            </>
          )}
        </Box>
      )}
    </Box>
  );
}

function figureCountPlural(n: number) {
  return n === 1 ? "" : "s";
}
