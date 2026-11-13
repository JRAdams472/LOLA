import Chip from "@mui/material/Chip";

/// Application lifecycle / pipeline-step state: drafts and data-entry are
/// working states (warning), priced/fees are progress (info), and
/// disclosures-generated is the terminal MVP state (success).
const COLORS: Record<string, "default" | "info" | "success" | "warning"> = {
  draft: "default",
  data_entry: "warning",
  "Data Entry": "warning",
  in_review: "info",
  Priced: "info",
  priced: "info",
  "Fees Calced": "info",
  fees_calced: "info",
  "Disclosures Generated": "success",
  disclosures_generated: "success",
};

export default function StatusChip({ status }: { status: string }) {
  return (
    <Chip
      label={status}
      size="small"
      color={COLORS[status] ?? "default"}
      variant="outlined"
    />
  );
}
