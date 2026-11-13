import { useState } from "react";
import Dialog from "@mui/material/Dialog";
import DialogTitle from "@mui/material/DialogTitle";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogActions from "@mui/material/DialogActions";
import TextField from "@mui/material/TextField";
import Button from "@mui/material/Button";
import Alert from "@mui/material/Alert";

/// Audited PII reveal — the reason is REQUIRED and lands verbatim in
/// the append-only audit chain alongside actor + field classes
/// (invariant 8). The dialog owns no state after close.
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
          your identity, the field classes, and this reason are written
          to the tamper-evident audit log.
        </DialogContentText>
        {error && (
          <Alert severity="error" variant="outlined" sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}
        <TextField
          autoFocus
          label="Reason (required)"
          size="small"
          fullWidth
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          placeholder="e.g. borrower SSN verification for LE"
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!reason.trim() || pending}
          onClick={() => onConfirm(reason.trim())}
        >
          Reveal
        </Button>
      </DialogActions>
    </Dialog>
  );
}
