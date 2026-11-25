import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";

import type { AtrReport } from "@/lib/atr";

const pct = (n: number | null | undefined) =>
  n == null ? "—" : `${n.toFixed(2)}%`;

const TEST_LABEL: Record<string, string> = {
  atr_general: "ATR — general (§1026.43(c))",
  qm_general: "QM — general (§1026.43(e))",
  qm_hud: "QM — HUD (24 CFR 203.19)",
  hoepa: "HOEPA high-cost (§1026.32)",
  hpml: "HPML (§1026.35)",
  state_high_cost: "State high-cost",
};

const TEST_ORDER = [
  "atr_general",
  "qm_general",
  "qm_hud",
  "hoepa",
  "hpml",
  "state_high_cost",
];

function verdictColor(verdict: string) {
  if (verdict === "fail") return "error";
  if (verdict === "warn") return "warning";
  return "success";
}

/// M22 P3 — ATR/QM battery posture on the loan workspace. Presentational:
/// the report comes in as a prop; the Evaluate affordance only renders
/// for staff (`canEvaluate`). Null report = not yet evaluated.
export default function AtrQmPanel({
  report,
  canEvaluate = false,
  evaluatePending = false,
  onEvaluate,
}: {
  report: AtrReport | null | undefined;
  canEvaluate?: boolean;
  evaluatePending?: boolean;
  onEvaluate?: () => void;
}) {
  const latest = report?.latest ?? null;
  const verdicts = [...(latest?.verdicts ?? [])].sort(
    (a, b) => TEST_ORDER.indexOf(a.test) - TEST_ORDER.indexOf(b.test),
  );
  const failures = verdicts.filter((v) => v.verdict === "fail");
  const warnings = verdicts.filter((v) => v.verdict === "warn");

  return (
    <Box data-testid="atr-panel">
      {!latest && (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
          No ATR/QM evaluation yet
          {canEvaluate ? " — run one to score this loan." : "."}
        </Typography>
      )}

      {latest && (
        <>
          {failures.length > 0 && (
            <Alert severity="error" sx={{ mb: 1 }} data-testid="atr-fail-banner">
              {failures.length} test{failures.length === 1 ? "" : "s"} failing
              — review before proceeding.
            </Alert>
          )}
          {failures.length === 0 && warnings.length > 0 && (
            <Alert severity="warning" sx={{ mb: 1 }} data-testid="atr-warn-banner">
              {warnings.length} test{warnings.length === 1 ? "" : "s"} incomplete
              — missing inputs; verdicts are not conclusive.
            </Alert>
          )}
          {failures.length === 0 && warnings.length === 0 && (
            <Alert severity="success" sx={{ mb: 1 }} data-testid="atr-pass-banner">
              All tests passing.
            </Alert>
          )}

          <Box
            sx={{ display: "flex", gap: 2, flexWrap: "wrap", mb: 1 }}
            data-testid="atr-figures"
          >
            <Typography variant="body2">
              DTI <strong>{pct(latest.dti)}</strong>
            </Typography>
            <Typography variant="body2">
              APR <strong>{pct(latest.apr)}</strong>
            </Typography>
            <Typography variant="body2">
              APOR <strong>{pct(latest.apor)}</strong>
            </Typography>
          </Box>

          {verdicts.map((v) => (
            <Box
              key={v.test}
              data-testid={`atr-test-${v.test}`}
              sx={{ display: "flex", alignItems: "center", gap: 1, py: 0.5 }}
            >
              <Chip
                size="small"
                color={verdictColor(v.verdict)}
                variant="outlined"
                label={v.verdict}
                data-testid={`atr-verdict-${v.test}`}
              />
              <Tooltip title={v.detail}>
                <Typography variant="body2" sx={{ flexGrow: 1 }}>
                  {TEST_LABEL[v.test] ?? v.test}
                </Typography>
              </Tooltip>
            </Box>
          ))}

          {(report?.history.length ?? 0) > 1 && (
            <Typography variant="caption" color="text.secondary">
              {report!.history.length} evaluations recorded — results are
              append-only.
            </Typography>
          )}
        </>
      )}

      {canEvaluate && (
        <Button
          size="small"
          variant="outlined"
          sx={{ mt: 1 }}
          disabled={evaluatePending}
          onClick={() => onEvaluate?.()}
          data-testid="evaluate-atr-button"
        >
          {latest ? "Re-evaluate" : "Evaluate"}
        </Button>
      )}
    </Box>
  );
}
