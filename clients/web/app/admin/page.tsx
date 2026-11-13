"use client";

import Typography from "@mui/material/Typography";
import Alert from "@mui/material/Alert";

/// Users & Groups, borrower invites, tenant audit, and retention —
/// Phase 4 (MPE-211). Staff-only nav already gates this route.
export default function AdminPage() {
  return (
    <>
      <Typography variant="h6" sx={{ mb: 2 }}>
        Admin
      </Typography>
      <Alert severity="info" variant="outlined">
        User &amp; group management, borrower invites, and the tenant audit
        trail land with the closing-costs phase.
      </Alert>
    </>
  );
}
