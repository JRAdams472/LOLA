import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";

import type { RespaReport, ServicingNotice } from "@/lib/respa";

const money = (n: number | null | undefined) =>
  n == null
    ? "—"
    : n.toLocaleString("en-US", {
        style: "currency",
        currency: "USD",
        maximumFractionDigits: 2,
      });

const VERDICT_LABEL: Record<string, string> = {
  within: "within cushion",
  shortage: "shortage",
  surplus: "surplus",
  deficient: "deficient",
  incomplete: "incomplete",
};

function verdictColor(verdict: string) {
  if (verdict === "within") return "success";
  if (verdict === "incomplete") return "warning";
  return "error";
}

/// M23 P3 — RESPA servicing/escrow posture on the loan workspace.
/// Presentational: the report comes in as a prop; staff affordances
/// only render when `canAct`. Null report = resolver hidden (non-staff).
export default function RespaPanel({
  report,
  canAct = false,
  analyzePending = false,
  onAnalyze,
  onRecordTransfer,
  onMarkSent,
  onMarkReceived,
  transferPending = false,
}: {
  report: RespaReport | null | undefined;
  canAct?: boolean;
  analyzePending?: boolean;
  transferPending?: boolean;
  onAnalyze?: () => void;
  onRecordTransfer?: () => void;
  onMarkSent?: (noticeId: string) => void;
  onMarkReceived?: (noticeId: string) => void;
}) {
  const latest = report?.latestAnalysis ?? null;
  const notices = report?.notices ?? [];
  const overdueCount = notices.filter((n) => n.overdue).length;

  return (
    <Box data-testid="respa-panel">
      {report?.afbaDisclosureRequired && (
        <Alert severity="warning" sx={{ mb: 1 }} data-testid="afba-banner">
          Section 8 affiliated-business arrangement — the AfBA disclosure
          is required for this loan (§1024.15).
        </Alert>
      )}

      {/* ——— escrow analysis ——— */}
      <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
        Escrow analysis (§1024.17)
      </Typography>
      {!latest && (
        <Typography
          variant="body2"
          color="text.secondary"
          sx={{ mb: 1 }}
          data-testid="escrow-empty"
        >
          No escrow analysis yet
          {canAct ? " — run one to posture this account." : "."}
        </Typography>
      )}
      {latest && (
        <Box data-testid="escrow-analysis">
          <Box sx={{ display: "flex", alignItems: "center", gap: 1, mb: 0.5 }}>
            <Chip
              size="small"
              color={verdictColor(latest.verdict)}
              variant="outlined"
              label={VERDICT_LABEL[latest.verdict] ?? latest.verdict}
              data-testid="escrow-verdict"
            />
            {latest.refundRequired && (
              <Chip
                size="small"
                color="error"
                label="refund clock — 30 days"
                data-testid="refund-clock"
              />
            )}
          </Box>
          {latest.verdict === "incomplete" ? (
            <Typography
              variant="body2"
              color="text.secondary"
              data-testid="escrow-missing"
            >
              Missing inputs: {latest.missingInputs.join(", ")}
            </Typography>
          ) : (
            <Box
              sx={{ display: "flex", gap: 2, flexWrap: "wrap" }}
              data-testid="escrow-figures"
            >
              <Typography variant="body2">
                Annual disbursements{" "}
                <strong>{money(latest.annualDisbursements)}</strong>
              </Typography>
              <Typography variant="body2">
                Cushion target <strong>{money(latest.cushionTarget)}</strong>
              </Typography>
              <Typography variant="body2">
                Low point <strong>{money(latest.lowPoint)}</strong>
              </Typography>
              {latest.determinationAmount != null &&
                latest.determinationAmount !== 0 && (
                  <Typography variant="body2">
                    {latest.determinationAmount > 0 ? "Shortage" : "Surplus"}{" "}
                    <strong>{money(Math.abs(latest.determinationAmount))}</strong>
                  </Typography>
                )}
            </Box>
          )}
          {(report?.analysisHistory.length ?? 0) > 1 && (
            <Typography variant="caption" color="text.secondary">
              {report!.analysisHistory.length} analyses recorded — append-only.
            </Typography>
          )}
        </Box>
      )}
      {canAct && (
        <Button
          size="small"
          variant="outlined"
          sx={{ mt: 1, mb: 2 }}
          disabled={analyzePending}
          onClick={() => onAnalyze?.()}
          data-testid="analyze-escrow-button"
        >
          {latest ? "Re-analyze" : "Analyze"}
        </Button>
      )}

      {/* ——— servicing transfers ——— */}
      <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
        Servicing transfers (§1024.33)
      </Typography>
      {overdueCount > 0 && (
        <Alert severity="error" sx={{ mb: 1 }} data-testid="overdue-banner">
          {overdueCount} notice{overdueCount === 1 ? "" : "s"} past the
          statutory deadline — blocking conditions open.
        </Alert>
      )}
      {notices.length === 0 && (
        <Typography
          variant="body2"
          color="text.secondary"
          data-testid="notices-empty"
        >
          No servicing transfers recorded.
        </Typography>
      )}
      {notices.map((n) => (
        <NoticeRow
          key={n.noticeId}
          notice={n}
          canAct={canAct}
          onMarkSent={onMarkSent}
          onMarkReceived={onMarkReceived}
        />
      ))}
      {canAct && (
        <Button
          size="small"
          variant="outlined"
          sx={{ mt: 1 }}
          disabled={transferPending}
          onClick={() => onRecordTransfer?.()}
          data-testid="record-transfer-button"
        >
          Record transfer
        </Button>
      )}
    </Box>
  );
}

function NoticeRow({
  notice: n,
  canAct,
  onMarkSent,
  onMarkReceived,
}: {
  notice: ServicingNotice;
  canAct: boolean;
  onMarkSent?: (noticeId: string) => void;
  onMarkReceived?: (noticeId: string) => void;
}) {
  const statusColor =
    n.status === "received"
      ? "success"
      : n.overdue || n.sentLate
        ? "error"
        : n.status === "sent"
          ? "info"
          : "warning";
  const detail = `${n.noticeKind} — send by ${n.sendBy}${
    n.sentLate ? " — SENT LATE" : ""
  }${n.overdue ? " — OVERDUE" : ""}`;

  return (
    <Box
      data-testid={`notice-${n.noticeId}`}
      sx={{ display: "flex", alignItems: "center", gap: 1, py: 0.5 }}
    >
      <Chip
        size="small"
        color={statusColor}
        variant="outlined"
        label={n.status}
        data-testid={`notice-status-${n.noticeId}`}
      />
      <Tooltip title={detail}>
        <Typography variant="body2" sx={{ flexGrow: 1 }}>
          {n.noticeKind} · {n.transferorLabel} → {n.transfereeLabel} · due{" "}
          {n.sendBy}
        </Typography>
      </Tooltip>
      {canAct && n.status === "pending" && (
        <Button
          size="small"
          onClick={() => onMarkSent?.(n.noticeId)}
          data-testid={`mark-sent-${n.noticeId}`}
        >
          Mark sent
        </Button>
      )}
      {canAct && n.status === "sent" && (
        <Button
          size="small"
          onClick={() => onMarkReceived?.(n.noticeId)}
          data-testid={`mark-received-${n.noticeId}`}
        >
          Mark received
        </Button>
      )}
    </Box>
  );
}
