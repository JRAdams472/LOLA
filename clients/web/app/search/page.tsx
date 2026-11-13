"use client";

import Typography from "@mui/material/Typography";
import Alert from "@mui/material/Alert";

/// Loan search (blind index: name/SSN/phone/email/address) + create-from-
/// scratch land in Phase 2 (MPE-209).
export default function SearchPage() {
  return (
    <>
      <Typography variant="h6" sx={{ mb: 2 }}>
        Loan Search
      </Typography>
      <Alert severity="info" variant="outlined">
        Search by borrower name, SSN, phone, email, or property address —
        landing with the pipeline read-model phase.
      </Alert>
    </>
  );
}
