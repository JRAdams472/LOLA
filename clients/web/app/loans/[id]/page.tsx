"use client";

import { useMemo, useState } from "react";
import { useParams } from "next/navigation";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";
import Tabs from "@mui/material/Tabs";
import Tab from "@mui/material/Tab";
import Paper from "@mui/material/Paper";
import Button from "@mui/material/Button";
import Alert from "@mui/material/Alert";
import Skeleton from "@mui/material/Skeleton";
import Chip from "@mui/material/Chip";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import List from "@mui/material/List";
import ListItem from "@mui/material/ListItem";
import SaveIcon from "@mui/icons-material/Save";
import SendIcon from "@mui/icons-material/Send";
import VisibilityIcon from "@mui/icons-material/Visibility";
import VisibilityOffIcon from "@mui/icons-material/VisibilityOff";

import ErrorPanel from "@/components/ErrorPanel";
import StatusChip from "@/components/StatusChip";
import TridCountdown from "@/components/TridCountdown";
import FieldInput from "@/components/FieldInput";
import RevealDialog from "@/components/RevealDialog";
import { useMe } from "@/lib/me";
import { mutationError } from "@/lib/api";
import {
  fieldErrors,
  useApplication,
  useApplicationAudit,
  useCorrect,
  usePatchDraft,
  useReveal,
  useSteps,
  useSubmit,
  type RevealedApplication,
} from "@/lib/loans";
import { FIELD_MAP, fieldsFor, SECTIONS, type SectionId } from "@/lib/field-map";
import { getPath, setPath, type Doc } from "@/lib/doc";

type TabId = SectionId | "steps" | "audit";

/// Loan workspace — tabbed catalog-driven 1003. The doc arrives masked
/// per the caller's visibility (DocMasker); editable while `draft`, and
/// staff append corrections on submitted states. Reveal is a separate
/// audited action that swaps in the unmasked doc until re-mask.
export default function LoanWorkspacePage() {
  const params = useParams<{ id: string }>();
  const appId = params.id;
  const { me, isStaff } = useMe();
  const tid = me?.tid;

  const app = useApplication(tid, appId);
  const [tab, setTab] = useState<TabId>("borrower");
  // Lazy per-tab queries — they fetch on first open and refetch on
  // revisit, so a reveal/save's audit event always shows fresh.
  const steps = useSteps(tid, appId, tab === "steps");
  const audit = useApplicationAudit(tid, appId, isStaff && tab === "audit");
  const patchDraft = usePatchDraft(tid, appId);
  const submit = useSubmit(tid, appId);
  const correct = useCorrect(tid, appId);
  const reveal = useReveal(tid, appId);

  const [patch, setPatch] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [revealed, setRevealed] = useState<RevealedApplication | null>(null);
  const [revealOpen, setRevealOpen] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const doc = useMemo<Doc>(() => {
    if (revealed) return revealed.doc;
    try {
      return JSON.parse(app.data?.docJson ?? "{}") as Doc;
    } catch {
      return {};
    }
  }, [revealed, app.data]);

  const status = revealed?.status ?? app.data?.status;
  const isDraft = status === "draft";
  const correctable = !!isStaff && !!status && !["draft", "decisioned"].includes(status);
  const editable = isDraft || correctable;
  const dirty = Object.keys(patch).length;

  const onChange = (path: string, value: string) => {
    setPath(doc, path, value);
    setPatch((p) => ({ ...p, [path]: value }));
    setErrors((e) => {
      const { [path]: _drop, ...rest } = e;
      return rest;
    });
  };

  const applyFieldErrors = (problem: unknown) => {
    const list = fieldErrors(problem);
    const mapped = Object.fromEntries(list.map((e) => [e.path, e.message]));
    setErrors(mapped);
    const first = list[0] && FIELD_MAP[list[0].path];
    if (first) setTab(first.section);
    return list.length;
  };

  const save = () => {
    setSaveError(null);
    const m = isDraft ? patchDraft : correct;
    m.mutate(patch, {
      onSuccess: (res) => {
        if (res.status === 422) {
          applyFieldErrors(res.data);
          return;
        }
        if (res.status >= 400) {
          setSaveError(`Save failed: HTTP ${res.status}`);
          return;
        }
        setPatch({});
        setRevealed(null);
      },
      onError: (e) => setSaveError(`${e}`),
    });
  };

  const doSubmit = () => {
    setSaveError(null);
    submit.mutate(undefined, {
      onSuccess: (res) => {
        if (res.status === 422) {
          applyFieldErrors(res.data);
          return;
        }
        if (res.status >= 400) {
          setSaveError(`Submit failed: HTTP ${res.status}`);
          return;
        }
        setPatch({});
        setRevealed(null);
      },
      onError: (e) => setSaveError(`${e}`),
    });
  };

  const doReveal = (reason: string) =>
    reveal.mutate(reason, {
      onSuccess: (res) => {
        if (res.status === 403 || res.status === 401) {
          setRevealOpen(false);
          setSaveError("Reveal denied — staff authorization required.");
          return;
        }
        if (res.status >= 400) {
          setRevealOpen(false);
          setSaveError(`Reveal failed: HTTP ${res.status}`);
          return;
        }
        setRevealed(res.data);
        setRevealOpen(false);
      },
      onError: (e) => {
        setRevealOpen(false);
        setSaveError(`${e}`);
      },
    });

  const borrowerName =
    getPath(doc, "party[0].first_name") || getPath(doc, "party[0].last_name")
      ? [getPath(doc, "party[0].first_name"), getPath(doc, "party[0].last_name")]
          .filter(Boolean)
          .join(" ")
      : null;

  if (app.isError) {
    return <ErrorPanel title="Loan failed to load" error={app.error} />;
  }

  return (
    <>
      <Box sx={{ display: "flex", alignItems: "center", gap: 1.5, mb: 1, flexWrap: "wrap" }}>
        <Typography variant="h6" sx={{ fontFamily: "monospace", fontSize: 18 }}>
          {appId}
        </Typography>
        {app.isLoading ? (
          <Skeleton variant="text" width={320} />
        ) : (
          <>
            <Typography variant="body1">{borrowerName ?? "—"}</Typography>
            <StatusChip status={status ?? ""} />
            <TridCountdown deadlineUtc={revealed?.tridDeadlineUtc ?? app.data?.tridDeadlineUtc} />
            <Chip label={app.data?.pipelineStep} size="small" variant="outlined" />
            <Chip label={`v${app.data?.currentVersion ?? "?"}`} size="small" variant="outlined" />
          </>
        )}
        <Box sx={{ flexGrow: 1 }} />
        {isStaff && !revealed && (
          <Button
            size="small"
            startIcon={<VisibilityIcon />}
            onClick={() => setRevealOpen(true)}
          >
            Reveal PII
          </Button>
        )}
        {revealed && (
          <Button
            size="small"
            startIcon={<VisibilityOffIcon />}
            onClick={() => setRevealed(null)}
          >
            Re-mask
          </Button>
        )}
        {isDraft && (
          <Button
            size="small"
            variant="outlined"
            startIcon={<SendIcon />}
            onClick={doSubmit}
            disabled={submit.isPending}
          >
            Submit
          </Button>
        )}
        {editable && (
          <Button
            size="small"
            variant="contained"
            startIcon={<SaveIcon />}
            disabled={dirty === 0 || patchDraft.isPending || correct.isPending}
            onClick={save}
          >
            {isDraft ? `Save draft${dirty ? ` (${dirty})` : ""}` : `Correct${dirty ? ` (${dirty})` : ""}`}
          </Button>
        )}
      </Box>

      {revealed && (
        <Alert severity="warning" variant="outlined" sx={{ mb: 1.5 }}>
          Unmasked PII visible — the reveal was written to the audit log.
        </Alert>
      )}
      {saveError && (
        <Alert severity="error" variant="outlined" sx={{ mb: 1.5 }}>
          {saveError}
        </Alert>
      )}
      {Object.keys(errors).length > 0 && (
        <Alert severity="error" variant="outlined" sx={{ mb: 1.5 }}>
          {Object.keys(errors).length} field
          {Object.keys(errors).length === 1 ? " needs" : "s need"} attention —
          invalid fields are marked below.
        </Alert>
      )}

      <Paper variant="outlined">
        <Tabs
          value={tab}
          onChange={(_, v) => setTab(v)}
          variant="scrollable"
          sx={{ borderBottom: 1, borderColor: "divider", px: 1 }}
        >
          {SECTIONS.map((s) => (
            <Tab key={s.id} value={s.id} label={s.label} />
          ))}
          <Tab value="steps" label="Steps" />
          {isStaff && <Tab value="audit" label="Audit" />}
        </Tabs>

        {SECTIONS.map(
          (s) =>
            tab === s.id && (
              <Box
                key={s.id}
                sx={{
                  display: "grid",
                  gridTemplateColumns: "repeat(auto-fill, minmax(240px, 1fr))",
                  gap: 2,
                  p: 2,
                }}
              >
                {fieldsFor(s.id).map((spec) => (
                  <FieldInput
                    key={spec.path}
                    spec={spec}
                    value={getPath(doc, spec.path)}
                    editable={editable}
                    error={errors[spec.path]}
                    canReveal={isStaff && !revealed}
                    onReveal={() => setRevealOpen(true)}
                    onChange={onChange}
                  />
                ))}
              </Box>
            ),
        )}

        {tab === "steps" && (
          <Box sx={{ p: 2 }}>
            {steps.isLoading && <Skeleton variant="rectangular" height={120} />}
            {steps.isError && <ErrorPanel title="Steps failed" error={steps.error} />}
            {steps.isSuccess && steps.data.length === 0 && (
              <Typography color="text.secondary">No pipeline events yet.</Typography>
            )}
            <List dense disablePadding>
              {(steps.data ?? []).map((e) => (
                <ListItem
                  key={e.id}
                  disableGutters
                  sx={{ borderLeft: 2, borderColor: "divider", pl: 2, py: 1 }}
                >
                  <Box>
                    <Typography variant="body2">
                      <strong>{e.step}</strong> · {e.event} · {e.actor}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">
                      {new Date(e.createdUtc).toLocaleString()}
                      {e.reason ? ` — ${e.reason}` : ""}
                    </Typography>
                  </Box>
                </ListItem>
              ))}
            </List>
          </Box>
        )}

        {tab === "audit" && isStaff && (
          <Box sx={{ p: 2 }}>
            {audit.isLoading && <Skeleton variant="rectangular" height={120} />}
            {audit.isError && <ErrorPanel title="Audit failed" error={audit.error} />}
            {audit.isSuccess && (
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>#</TableCell>
                    <TableCell>Action</TableCell>
                    <TableCell>Actor</TableCell>
                    <TableCell>Field classes</TableCell>
                    <TableCell>Reason</TableCell>
                    <TableCell>Hash</TableCell>
                    <TableCell>Time (UTC)</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {audit.data.map((e) => (
                    <TableRow key={e.eventId}>
                      <TableCell>{e.seq}</TableCell>
                      <TableCell>{e.action}</TableCell>
                      <TableCell>{e.actor}</TableCell>
                      <TableCell>{e.fieldClasses.join(", ") || "—"}</TableCell>
                      <TableCell>{e.reason ?? "—"}</TableCell>
                      <TableCell sx={{ fontFamily: "monospace", fontSize: 11 }}>
                        {e.hash.slice(0, 12)}
                      </TableCell>
                      <TableCell>{e.createdUtc}</TableCell>
                    </TableRow>
                  ))}
                  {audit.data.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={7} align="center" sx={{ py: 4 }}>
                        <Typography color="text.secondary">No audit events.</Typography>
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
            )}
          </Box>
        )}
      </Paper>

      <RevealDialog
        open={revealOpen}
        onClose={() => setRevealOpen(false)}
        onConfirm={doReveal}
        pending={reveal.isPending}
        error={mutationError(reveal)}
      />
    </>
  );
}
