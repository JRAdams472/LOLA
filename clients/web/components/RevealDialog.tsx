import { useState } from "react";
import Dialog from "@mui/material/Dialog";
import DialogTitle from "@mui/material/DialogTitle";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogActions from "@mui/material/DialogActions";
import TextField from "@mui/material/TextField";
import MenuItem from "@mui/material/MenuItem";
import Button from "@mui/material/Button";
import Alert from "@mui/material/Alert";

/// Audited PII reveal — the reason is REQUIRED and lands as a
/// validated code in the append-only audit chain (MPE-274). Codes,
/// not prose: a free-text prompt was an injection channel for PII
/// into the tamper-evident log. The dialog owns no state after close.
const REASON_CODES = [
  {
    value: "reveal.borrower_request",
    label: "Borrower request — document or data verification",
  },
  {
    value: "reveal.underwriting_review",
    label: "Underwriting review — condition or eligibility check",
  },
  {
    value: "reveal.compliance_audit",
    label: "Compliance audit — regulator or QC review",
  },
];

export default function RevealDialog({
  open,
  onClose,
  onConfirm,
  pending,
  error,
}: {
  open: boolean;
  onClose: () => void;
  onConfirm: (reason: string) => void;
  pending?: boolean;
  error?: string | null;
}) {
  const [reason, setReason] = useState("");
  const close = () => {
    setReason("");
    onClose();
  };
  return (
    <Dialog open={open} onClose={close} maxWidth="xs" fullWidth>
      <DialogTitle>Reveal protected fields</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          Unmasking this application&rsquo;s PII is an audited action —
          your identity, the field classes, and a reason code are
          written to the tamper-evident audit log.
        </DialogContentText>
        {error && (
          <Alert severity="error" variant="outlined" sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}
        <TextField
          autoFocus
          select
          label="Reason (required)"
          size="small"
          fullWidth
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          helperText="Choose the category — details belong in the case notes, not here."
        >
          {REASON_CODES.map((c) => (
            <MenuItem key={c.value} value={c.value}>
              {c.label}
            </MenuItem>
          ))}
        </TextField>
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!reason || pending}
          onClick={() => onConfirm(reason)}
        >
          Reveal
        </Button>
      </DialogActions>
    </Dialog>
  );
}
