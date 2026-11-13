"use client";

import Typography from "@mui/material/Typography";
import Alert from "@mui/material/Alert";

/// Pipeline view lands in Phase 2 (MPE-209) — this placeholder proves
/// the shell, session, and React Query wiring end-to-end.
export default function PipelinePage() {
  return (
    <>
      <Typography variant="h6" sx={{ mb: 2 }}>
        Pipeline
      </Typography>
      <Alert severity="info" variant="outlined">
        Active loans will appear here — the pipeline read-model lands with
        the next phase.
      </Alert>
    </>
  );
}
