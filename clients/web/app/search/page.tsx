"use client";

import { useState } from "react";
import Typography from "@mui/material/Typography";
import TextField from "@mui/material/TextField";
import MenuItem from "@mui/material/MenuItem";
import Button from "@mui/material/Button";
import Paper from "@mui/material/Paper";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import SearchIcon from "@mui/icons-material/Search";

import ErrorPanel from "@/components/ErrorPanel";
import StatusChip from "@/components/StatusChip";
import TridCountdown from "@/components/TridCountdown";
import { useMe } from "@/lib/me";
import {
  SEARCH_FIELDS,
  useApplications,
  useLoanSearch,
} from "@/lib/loans";

/// Blind-index loan search — the term travels in the GraphQL POST body
/// only, so an SSN never lands in a URL, browser history, or access
/// log. Results join the pipeline read-model for name/status display.
export default function SearchPage() {
  const { me } = useMe();
  const apps = useApplications(me?.tid);
  const search = useLoanSearch(me?.tid);
  const [field, setField] = useState<string>(SEARCH_FIELDS[0].field);
  const [value, setValue] = useState("");
  const [ran, setRan] = useState(false);

  const hits = (search.data ?? []).map(
    (id) => apps.data?.find((a) => a.id === id) ?? { id },
  );

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!value.trim()) return;
    setRan(true);
    search.mutate({ field, value: value.trim() });
  };

  return (
    <>
      <Typography variant="h6" sx={{ mb: 2 }}>
        Loan Search
      </Typography>

      <Paper
        component="form"
        variant="outlined"
        onSubmit={submit}
        sx={{ display: "flex", gap: 2, p: 2, mb: 2, alignItems: "center" }}
      >
        <TextField
          select
          label="Field"
          size="small"
          value={field}
          onChange={(e) => setField(e.target.value)}
          sx={{ minWidth: 180 }}
        >
          {SEARCH_FIELDS.map((f) => (
            <MenuItem key={f.field} value={f.field}>
              {f.label}
            </MenuItem>
          ))}
        </TextField>
        <TextField
          label="Value"
          size="small"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          autoComplete="off"
          slotProps={
            field === "ssn" ? { htmlInput: { inputMode: "numeric" } } : undefined
          }
          sx={{ flexGrow: 1 }}
        />
        <Button
          type="submit"
          variant="contained"
          startIcon={<SearchIcon />}
          disabled={search.isPending || !value.trim()}
        >
          Search
        </Button>
      </Paper>

      <Typography variant="caption" color="text.secondary" sx={{ mb: 2, display: "block" }}>
        Exact-match search over the blind index — terms are sent in the
        request body and never appear in URLs or history.
      </Typography>

      {search.isError && <ErrorPanel title="Search failed" error={search.error} />}

      {ran && !search.isError && (
        <TableContainer component={Paper} variant="outlined">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Borrower</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Step</TableCell>
                <TableCell>LE due</TableCell>
                <TableCell>Application</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {hits.map((h) => (
                <TableRow key={h.id} hover>
                  <TableCell>{"borrowerName" in h ? (h.borrowerName ?? "—") : "—"}</TableCell>
                  <TableCell>
                    {"status" in h ? <StatusChip status={h.status} /> : "—"}
                  </TableCell>
                  <TableCell>{"pipelineStep" in h ? h.pipelineStep : "—"}</TableCell>
                  <TableCell>
                    <TridCountdown
                      deadlineUtc={"tridDeadlineUtc" in h ? h.tridDeadlineUtc : null}
                    />
                  </TableCell>
                  <TableCell sx={{ fontFamily: "monospace", fontSize: 12 }}>
                    {h.id.slice(0, 8)}
                  </TableCell>
                </TableRow>
              ))}
              {search.isSuccess && hits.length === 0 && (
                <TableRow>
                  <TableCell colSpan={5} align="center" sx={{ py: 6 }}>
                    <Typography color="text.secondary">
                      No matching loans.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </>
  );
}
