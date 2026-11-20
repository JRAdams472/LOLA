"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import FormControlLabel from "@mui/material/FormControlLabel";
import MenuItem from "@mui/material/MenuItem";
import Paper from "@mui/material/Paper";
import Switch from "@mui/material/Switch";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";

import ErrorPanel from "@/components/ErrorPanel";
import { useMe } from "@/lib/me";
import {
  TASK_KIND_LABEL,
  useClaimTask,
  useCompleteTask,
  useNextTask,
  useTasks,
  type QueueTask,
} from "@/lib/tasks";

const SKILLS = ["processing", "underwriting", "closing"] as const;

const PRIORITY_CHIP: Record<string, "error" | "warning" | "info" | "default"> = {
  urgent: "error",
  high: "warning",
  normal: "info",
  low: "default",
};

function priorityName(p: number): string {
  if (p >= 80) return "urgent";
  if (p >= 50) return "high";
  if (p >= 30) return "normal";
  return "low";
}

function fmtDue(dueUtc: string | null): string {
  if (!dueUtc) return "—";
  const d = new Date(dueUtc);
  const overdue = d.getTime() < Date.now();
  return `${overdue ? "overdue · " : ""}${d.toLocaleDateString()}`;
}

/// Officer work queue (M16) — the unified ranked list of everything the
/// engine wants worked next. Highest-priority first; "Do next" claims
/// the top task and lands on its application workspace. Claim/complete
/// transitions are audited server-side; rows are PII-free (refs only).
export default function QueuePage() {
  const router = useRouter();
  const { me } = useMe();
  const [skill, setSkill] = useState<string>("");
  const [mineOnly, setMineOnly] = useState(false);
  const [conflict, setConflict] = useState<string | null>(null);

  const tasks = useTasks(me?.tid, skill ? [skill] : undefined);
  const next = useNextTask(me?.tid);
  const claim = useClaimTask(me?.tid);
  const complete = useCompleteTask(me?.tid);

  const rows = (tasks.data ?? []).filter(
    (t) => !mineOnly || t.claimedBy === me?.sub,
  );

  /** Claim-then-navigate: the do-next flow takes the top task into this
   *  officer's name and lands on the loan workspace. A 409 means another
   *  officer claimed first — the refetched queue shows who. */
  const doNext = async (t: QueueTask) => {
    setConflict(null);
    if (t.state === "open") {
      const res = await claim.mutateAsync(t.taskId).catch(() => null);
      if (!res) {
        setConflict("That task was just claimed by another officer.");
        return;
      }
    }
    router.push(`/loans/${t.applicationId}`);
  };

  return (
    <Box>
      <Typography variant="h5" sx={{ mb: 1 }}>
        Queue
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Everything the task engine wants worked, ranked. Claims and
        completions are audited; rows carry refs, never borrower data.
      </Typography>

      {next.data && (
        <Paper
          variant="outlined"
          sx={{
            display: "flex",
            alignItems: "center",
            gap: 2,
            p: 2,
            mb: 2,
          }}
        >
          <Box sx={{ flexGrow: 1 }}>
            <Typography variant="subtitle2">Next best task</Typography>
            <Typography variant="body2" color="text.secondary">
              {next.data.title} · {TASK_KIND_LABEL[next.data.kind] ?? next.data.kind}
              {next.data.dueUtc ? ` · due ${fmtDue(next.data.dueUtc)}` : ""}
            </Typography>
          </Box>
          <Chip
            label={priorityName(next.data.priority)}
            size="small"
            color={PRIORITY_CHIP[priorityName(next.data.priority)]}
          />
          <Button
            variant="contained"
            size="small"
            startIcon={<PlayArrowIcon />}
            onClick={() => doNext(next.data!)}
            disabled={claim.isPending}
          >
            Do next
          </Button>
        </Paper>
      )}

      {conflict && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          {conflict}
        </Alert>
      )}

      <Box sx={{ display: "flex", gap: 2, mb: 2, alignItems: "center" }}>
        <TextField
          select
          label="Skill"
          size="small"
          value={skill}
          onChange={(e) => setSkill(e.target.value)}
          sx={{ minWidth: 160 }}
        >
          <MenuItem value="">All skills</MenuItem>
          {SKILLS.map((s) => (
            <MenuItem key={s} value={s}>
              {s}
            </MenuItem>
          ))}
        </TextField>
        <FormControlLabel
          control={
            <Switch
              size="small"
              checked={mineOnly}
              onChange={(e) => setMineOnly(e.target.checked)}
            />
          }
          label="Claimed by me"
        />
      </Box>

      {tasks.isError && (
        <ErrorPanel title="Queue failed to load" error={tasks.error} />
      )}

      {!tasks.isError && (
        <TableContainer component={Paper} variant="outlined">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Priority</TableCell>
                <TableCell>Task</TableCell>
                <TableCell>Kind</TableCell>
                <TableCell>Skills</TableCell>
                <TableCell>Due</TableCell>
                <TableCell>State</TableCell>
                <TableCell align="right">Actions</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((t) => {
                const mine = t.claimedBy === me?.sub;
                const claimable = t.state === "open";
                const completable = t.state === "open" || mine;
                return (
                  <TableRow key={t.taskId} hover>
                    <TableCell>
                      <Chip
                        label={priorityName(t.priority)}
                        size="small"
                        color={PRIORITY_CHIP[priorityName(t.priority)]}
                        variant={t.priority >= 80 ? "filled" : "outlined"}
                      />
                    </TableCell>
                    <TableCell>
                      <Button
                        size="small"
                        onClick={() =>
                          router.push(`/loans/${t.applicationId}`)
                        }
                        sx={{ textTransform: "none", p: 0, minWidth: 0 }}
                      >
                        {t.title}
                      </Button>
                    </TableCell>
                    <TableCell>
                      {TASK_KIND_LABEL[t.kind] ?? t.kind}
                    </TableCell>
                    <TableCell>
                      {t.skillTags.length
                        ? t.skillTags.map((s) => (
                            <Chip
                              key={s}
                              label={s}
                              size="small"
                              variant="outlined"
                              sx={{ mr: 0.5 }}
                            />
                          ))
                        : "—"}
                    </TableCell>
                    <TableCell>{fmtDue(t.dueUtc)}</TableCell>
                    <TableCell>
                      {t.state === "claimed" && !mine
                        ? `claimed · other`
                        : t.state}
                    </TableCell>
                    <TableCell align="right">
                      {claimable && (
                        <Button
                          size="small"
                          onClick={() => claim.mutate(t.taskId)}
                          disabled={claim.isPending}
                        >
                          Claim
                        </Button>
                      )}
                      {completable && (
                        <Button
                          size="small"
                          color="success"
                          onClick={() => complete.mutate(t.taskId)}
                          disabled={complete.isPending}
                        >
                          Complete
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                );
              })}
              {rows.length === 0 && !tasks.isLoading && (
                <TableRow>
                  <TableCell colSpan={7}>
                    <Typography
                      variant="body2"
                      color="text.secondary"
                      sx={{ py: 4, textAlign: "center" }}
                    >
                      Queue is clear — new tasks appear as applications
                      move through the pipeline.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
