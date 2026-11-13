import Chip from "@mui/material/Chip";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";

import { businessDaysUntil } from "@/lib/trid";

const fmt = new Intl.DateTimeFormat("en-US", {
  month: "short",
  day: "numeric",
  timeZone: "UTC",
});

/// Reg Z LE-due countdown — 3-business-day clock from the TRID stamp.
/// Overdue is red, ≤1 business day amber, otherwise info; un-stamped
/// loans show an em dash (the six-field trigger hasn't completed).
export default function TridCountdown({
  deadlineUtc,
}: {
  deadlineUtc?: string | null;
}) {
  if (!deadlineUtc) {
    return <Typography color="text.secondary">—</Typography>;
  }
  const left = businessDaysUntil(deadlineUtc);
  const label =
    left < 0 ? `${-left}d overdue` : left === 0 ? "due today" : `${left}d left`;
  const color = left < 0 ? "error" : left <= 1 ? "warning" : "info";
  return (
    <Tooltip title={`Loan Estimate due ${fmt.format(new Date(deadlineUtc))}`}>
      <Chip label={label} size="small" color={color} variant="outlined" />
    </Tooltip>
  );
}
