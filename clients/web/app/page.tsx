"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogTitle from "@mui/material/DialogTitle";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogActions from "@mui/material/DialogActions";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Paper from "@mui/material/Paper";
import Skeleton from "@mui/material/Skeleton";
import Alert from "@mui/material/Alert";
import AddIcon from "@mui/icons-material/Add";

import ErrorPanel from "@/components/ErrorPanel";
import StatusChip from "@/components/StatusChip";
import TridCountdown from "@/components/TridCountdown";
import { useMe } from "@/lib/me";
import { useApplications, useCreateDraft } from "@/lib/loans";
import { mutationError } from "@/lib/api";

/// Officer pipeline — every active application with the masked-aware
/// borrower name, lifecycle status, step, and the TRID LE countdown.
/// Sorted soonest-deadline-first so the clock that expires next always
/// sits on top; un-stamped loans sink to the bottom.
export default function PipelinePage() {
  const router = useRouter();
  const { me } = useMe();
  const apps = useApplications(me?.tid);
  const create = useCreateDraft(me?.tid);
  const [newOpen, setNewOpen] = useState(false);

  const rows = useMemo(
    () =>
      [...(apps.data ?? [])].sort((a, b) => {
        if (!a.tridDeadlineUtc && !b.tridDeadlineUtc) return 0;
        if (!a.tridDeadlineUtc) return 1;
        if (!b.tridDeadlineUtc) return -1;
        return a.tridDeadlineUtc.localeCompare(b.tridDeadlineUtc);
      }),
    [apps.data],
  );

  const createError = mutationError(create);

  return (
    <>
      <Box sx={{ display: "flex", alignItems: "center", mb: 2, gap: 2 }}>
        <Typography variant="h6" sx={{ flexGrow: 1 }}>
          Pipeline
        </Typography>
        <Button
          variant="contained"
          size="small"
          startIcon={<AddIcon />}
          onClick={() => setNewOpen(true)}
        >
          New loan
        </Button>
      </Box>

      {apps.isError && <ErrorPanel title="Pipeline failed to load" error={apps.error} />}
      {createError && (
        <Alert severity="error" variant="outlined" sx={{ mb: 2 }}>
          {createError}
        </Alert>
      )}

      <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Borrower</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Step</TableCell>
              <TableCell>LE due</TableCell>
              <TableCell>Ver</TableCell>
              <TableCell>Application</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {apps.isLoading &&
              [0, 1, 2].map((i) => (
                <TableRow key={i}>
                  {Array.from({ length: 6 }, (_, c) => (
                    <TableCell key={c}>
                      <Skeleton variant="text" />
                    </TableCell>
                  ))}
                </TableRow>
              ))}
            {rows.map((a) => (
              <TableRow
                key={a.id}
                hover
                onClick={() => router.push(`/loans/${a.id}`)}
                sx={{ cursor: "pointer" }}
              >
                <TableCell>{a.borrowerName ?? "—"}</TableCell>
                <TableCell>
                  <StatusChip status={a.status} />
                </TableCell>
                <TableCell>{a.pipelineStep}</TableCell>
                <TableCell>
                  <TridCountdown deadlineUtc={a.tridDeadlineUtc} />
                </TableCell>
                <TableCell>{a.currentVersion}</TableCell>
                <TableCell sx={{ fontFamily: "monospace", fontSize: 12 }}>
                  {a.id.slice(0, 8)}
                </TableCell>
              </TableRow>
            ))}
            {apps.isSuccess && rows.length === 0 && (
              <TableRow>
                <TableCell colSpan={6} align="center" sx={{ py: 6 }}>
                  <Typography color="text.secondary">
                    No loans yet — start one with “New loan”.
                  </Typography>
                </TableCell>
              </TableRow>
            )}
          </TableBody>
        </Table>
      </TableContainer>

      <Dialog open={newOpen} onClose={() => setNewOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>New loan</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Creates a blank URLA/1003 draft. Borrower, property, and loan
            fields are filled in the loan workspace after creation.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setNewOpen(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={create.isPending}
            onClick={() =>
              create.mutate(undefined, {
                onSuccess: (res) => {
                  setNewOpen(false);
                  if (res.status < 400 && res.data?.id) {
                    router.push(`/loans/${res.data.id}`);
                  }
                },
              })
            }
          >
            Create draft
          </Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
