"use client";

import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import Link from "next/link";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";
import Paper from "@mui/material/Paper";
import Button from "@mui/material/Button";
import IconButton from "@mui/material/IconButton";
import Tooltip from "@mui/material/Tooltip";
import Alert from "@mui/material/Alert";
import Skeleton from "@mui/material/Skeleton";
import Chip from "@mui/material/Chip";
import Divider from "@mui/material/Divider";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import Dialog from "@mui/material/Dialog";
import DialogTitle from "@mui/material/DialogTitle";
import DialogContent from "@mui/material/DialogContent";
import DialogActions from "@mui/material/DialogActions";
import TextField from "@mui/material/TextField";
import MenuItem from "@mui/material/MenuItem";
import DownloadIcon from "@mui/icons-material/Download";
import EditIcon from "@mui/icons-material/Edit";
import LockIcon from "@mui/icons-material/Lock";
import LockOpenIcon from "@mui/icons-material/LockOpen";
import RefreshIcon from "@mui/icons-material/Refresh";
import RequestQuoteIcon from "@mui/icons-material/RequestQuote";
import DescriptionIcon from "@mui/icons-material/Description";
import ArrowBackIcon from "@mui/icons-material/ArrowBack";

import ErrorPanel from "@/components/ErrorPanel";
import StatusChip from "@/components/StatusChip";
import { useMe } from "@/lib/me";
import { useApplication } from "@/lib/loans";
import {
  downloadUrl,
  PACKAGE_KINDS,
  useDocuments,
  useEvaluateFees,
  useFees,
  useLockRate,
  useOverrideFee,
  usePrice,
  usePricing,
  useRateLock,
  useRequestPackage,
  useUnlockRate,
  useUploads,
  type FeeLine,
  type PricedProduct,
  type RateOffer,
} from "@/lib/closing";

const money = (n: number | null | undefined) =>
  n == null ? "—" : `$${n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

const SECTION_LABEL: Record<string, string> = {
  A: "A — Origination charges",
  B: "B — Services you cannot shop for",
  C: "C — Services you can shop for",
  E: "E — Taxes & government fees",
  F: "F — Prepaids",
  H: "H — Other",
};

const statusColor = (s: string) =>
  s === "done" || s === "completed" || s === "clean" || s === "active"
    ? "success"
    : s === "failed" || s === "infected" || s === "dlq"
      ? "error"
      : "default";

/// Closing Costs & Docs (MPE-211) — three columns: itemized LE fees
/// with audited overrides, pricing/rate-lock, and generated packages
/// + borrower-vault uploads. Every outbound call stays behind the API
/// (ADR-0007) — the browser never talks to MPEx/ISPPr/MinIO directly.
export default function ClosingPage() {
  const params = useParams<{ id: string }>();
  const appId = params.id;
  const { me, isStaff } = useMe();
  const tid = me?.tid;
  const app = useApplication(tid, appId);

  // Poll window: a 202 enqueue resolves via the outbox worker —
  // refetch the reads until nothing is pending (or a 30s cap).
  const [polling, setPolling] = useState(false);
  const startPoll = () => setPolling(true);

  const pricing = usePricing(tid, appId, polling);
  const rateLock = useRateLock(tid, appId);
  const fees = useFees(tid, appId, polling);
  const documents = useDocuments(tid, appId, polling);
  const uploads = useUploads(tid, appId);

  const latestEval = fees.data?.evals?.[fees.data.evals.length - 1];
  const products = pricing.data?.result?.products ?? [];

  useEffect(() => {
    if (!polling) return;
    const pending =
      pricing.data?.status === "pending" ||
      latestEval?.status === "pending" ||
      (documents.data ?? []).some((d) => d.status === "pending" || d.status === "requested");
    if (!pending) {
      const settle = setTimeout(() => setPolling(false), 3000);
      return () => clearTimeout(settle);
    }
    const cap = setTimeout(() => setPolling(false), 30_000);
    return () => clearTimeout(cap);
  }, [polling, pricing.data?.status, latestEval?.status, documents.data]);

  const price = usePrice(tid, appId);
  const evalFees = useEvaluateFees(tid, appId);
  const overrideFee = useOverrideFee(tid, appId);
  const lockRate = useLockRate(tid, appId);
  const unlockRate = useUnlockRate(tid, appId);
  const requestPackage = useRequestPackage(tid, appId);

  const [overrideFor, setOverrideFor] = useState<FeeLine | null>(null);
  const [overrideAmount, setOverrideAmount] = useState("");
  const [overrideReason, setOverrideReason] = useState("");
  const [unlockOpen, setUnlockOpen] = useState(false);
  const [unlockReason, setUnlockReason] = useState("");
  const [pkgOpen, setPkgOpen] = useState(false);
  const [pkgKind, setPkgKind] = useState<string>("le-package");
  const [pkgReasons, setPkgReasons] = useState("");
  const [pageError, setPageError] = useState<string | null>(null);

  const doPrice = () =>
    price.mutate(undefined, {
      onSuccess: (r) => (r.status < 400 ? startPoll() : setPageError(`Re-price failed: HTTP ${r.status}`)),
      onError: (e) => setPageError(`${e}`),
    });
  const doFees = () =>
    evalFees.mutate(undefined, {
      onSuccess: (r) => (r.status < 400 ? startPoll() : setPageError(`Recalculate failed: HTTP ${r.status}`)),
      onError: (e) => setPageError(`${e}`),
    });
  const doLock = (p: PricedProduct, o: RateOffer) =>
    lockRate.mutate(
      {
        evalId: pricing.data!.evalId,
        productCode: p.productCode,
        rate: o.rate,
        lockTermDays: o.lockTermDays,
      },
      {
        onSuccess: (r) => {
          if (r.status >= 400) setPageError(`Lock failed: HTTP ${r.status}`);
        },
        onError: (e) => setPageError(`${e}`),
      },
    );
  const doUnlock = () =>
    unlockRate.mutate(unlockReason.trim(), {
      onSuccess: (r) => {
        setUnlockOpen(false);
        if (r.status >= 400) setPageError(`Unlock failed: HTTP ${r.status}`);
      },
      onError: (e) => setPageError(`${e}`),
    });
  const doOverride = () =>
    overrideFee.mutate(
      {
        itemId: overrideFor!.itemId,
        amount: overrideAmount === "" ? null : Number(overrideAmount),
        reason: overrideReason.trim(),
      },
      {
        onSuccess: (r) => {
          setOverrideFor(null);
          if (r.status >= 400) setPageError(`Override failed: HTTP ${r.status}`);
        },
        onError: (e) => setPageError(`${e}`),
      },
    );
  const doPackage = () =>
    requestPackage.mutate(
      {
        kind: pkgKind,
        reasonCodes: pkgReasons.split(",").map((s) => s.trim()).filter(Boolean),
      },
      {
        onSuccess: (r) => {
          setPkgOpen(false);
          if (r.status < 400) startPoll();
          else setPageError(`Package request failed: HTTP ${r.status}`);
        },
        onError: (e) => setPageError(`${e}`),
      },
    );

  if (app.isError) return <ErrorPanel title="Loan failed to load" error={app.error} />;

  return (
    <>
      <Box sx={{ display: "flex", alignItems: "center", gap: 1.5, mb: 1.5, flexWrap: "wrap" }}>
        <Button
          component={Link}
          href={`/loans/${appId}`}
          size="small"
          startIcon={<ArrowBackIcon />}
        >
          Workspace
        </Button>
        <Typography variant="h6" sx={{ fontFamily: "monospace", fontSize: 18 }}>
          {appId}
        </Typography>
        <Typography variant="subtitle2" color="text.secondary">
          Closing Costs &amp; Docs
        </Typography>
        {app.data && <StatusChip status={app.data.status} />}
        {polling && <Chip label="evaluating…" size="small" color="info" variant="outlined" />}
      </Box>

      {pageError && (
        <Alert severity="error" variant="outlined" sx={{ mb: 1.5 }} onClose={() => setPageError(null)}>
          {pageError}
        </Alert>
      )}

      <Box
        sx={{
          display: "grid",
          gridTemplateColumns: { xs: "1fr", lg: "1fr 1fr 1fr" },
          gap: 2,
          alignItems: "start",
        }}
      >
        {/* —— Column 1: Fees —— */}
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: "flex", alignItems: "center", mb: 1 }}>
            <Typography variant="subtitle1" sx={{ flexGrow: 1 }}>
              Loan Estimate fees
            </Typography>
            <Button
              size="small"
              startIcon={<RefreshIcon />}
              onClick={doFees}
              disabled={!isStaff || evalFees.isPending}
            >
              Recalculate
            </Button>
          </Box>
          {fees.isLoading && <Skeleton variant="rectangular" height={140} />}
          {fees.isError && <ErrorPanel title="Fees failed" error={fees.error} />}
          {fees.isSuccess && !latestEval && (
            <Typography color="text.secondary" variant="body2">
              No evaluation yet — Recalculate prices the fee schedule.
            </Typography>
          )}
          {latestEval?.status === "pending" && (
            <Alert severity="info" variant="outlined" sx={{ mb: 1 }}>
              Evaluation running…
            </Alert>
          )}
          {latestEval?.status === "failed" && (
            <Alert severity="error" variant="outlined" sx={{ mb: 1 }}>
              {latestEval.error ?? "fee evaluation failed"}
            </Alert>
          )}
          {latestEval?.sections.map((s) => (
            <Box key={s.section} sx={{ mb: 1.5 }}>
              <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
                {SECTION_LABEL[s.section] ?? `Section ${s.section}`}
              </Typography>
              <Table size="small">
                <TableBody>
                  {s.items.map((item) => (
                    <TableRow key={item.itemId}>
                      <TableCell sx={{ pl: 0, border: 0, py: 0.5 }}>
                        {item.label ?? item.feeCode}
                      </TableCell>
                      <TableCell align="right" sx={{ border: 0, py: 0.5, whiteSpace: "nowrap" }}>
                        {item.overrideAmount != null ? (
                          <Tooltip title={`Override: ${item.overrideReason ?? ""}`}>
                            <span>
                              <s style={{ opacity: 0.5 }}>{money(item.computed)}</s>{" "}
                              <strong>{money(item.overrideAmount)}</strong>
                            </span>
                          </Tooltip>
                        ) : (
                          money(item.computed)
                        )}
                        {isStaff && (
                          <IconButton
                            size="small"
                            aria-label={`override ${item.label ?? item.feeCode}`}
                            onClick={() => {
                              setOverrideFor(item);
                              setOverrideAmount(
                                item.overrideAmount != null ? String(item.overrideAmount) : "",
                              );
                              setOverrideReason(item.overrideReason ?? "");
                            }}
                          >
                            <EditIcon sx={{ fontSize: 13 }} />
                          </IconButton>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                  <TableRow>
                    <TableCell sx={{ pl: 0, py: 0.5, fontWeight: 700 }}>Section total</TableCell>
                    <TableCell align="right" sx={{ py: 0.5, fontWeight: 700 }}>
                      {s.computedTotal === s.effectiveTotal
                        ? money(s.effectiveTotal)
                        : `${money(s.effectiveTotal)} (was ${money(s.computedTotal)})`}
                    </TableCell>
                  </TableRow>
                </TableBody>
              </Table>
            </Box>
          ))}
          {latestEval && (
            <>
              <Divider sx={{ my: 1 }} />
              <Box sx={{ display: "flex", justifyContent: "space-between" }}>
                <Typography variant="subtitle2">Total</Typography>
                <Typography variant="subtitle2">
                  {money(latestEval.effectiveTotal)}
                  {latestEval.computedTotal !== latestEval.effectiveTotal &&
                    ` (was ${money(latestEval.computedTotal)})`}
                </Typography>
              </Box>
              {latestEval.staleOverrides.length > 0 && (
                <Alert severity="warning" variant="outlined" sx={{ mt: 1 }}>
                  {latestEval.staleOverrides.length} stale override
                  {latestEval.staleOverrides.length > 1 ? "s" : ""} — the overridden fee
                  no longer appears in the latest eval.
                </Alert>
              )}
            </>
          )}
        </Paper>

        {/* —— Column 2: Pricing & rate lock —— */}
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: "flex", alignItems: "center", mb: 1 }}>
            <Typography variant="subtitle1" sx={{ flexGrow: 1 }}>
              Pricing &amp; rate lock
            </Typography>
            <Button
              size="small"
              startIcon={<RequestQuoteIcon />}
              onClick={doPrice}
              disabled={!isStaff || price.isPending}
            >
              Re-Price
            </Button>
          </Box>

          {rateLock.isLoading && <Skeleton variant="rectangular" height={60} />}
          {rateLock.data && rateLock.data.status === "active" && (
            <Alert
              severity="success"
              variant="outlined"
              icon={<LockIcon fontSize="small" />}
              sx={{ mb: 1.5 }}
              action={
                isStaff && (
                  <Button
                    size="small"
                    startIcon={<LockOpenIcon />}
                    onClick={() => {
                      setUnlockReason("");
                      setUnlockOpen(true);
                    }}
                  >
                    Unlock
                  </Button>
                )
              }
            >
              <strong>{rateLock.data.productCode}</strong> @ {rateLock.data.rate}% ·{" "}
              {rateLock.data.lockTermDays}d — expires{" "}
              {new Date(rateLock.data.expiresUtc).toLocaleDateString()}
            </Alert>
          )}
          {rateLock.data && rateLock.data.status !== "active" && (
            <Alert severity="info" variant="outlined" sx={{ mb: 1.5 }}>
              Lock {rateLock.data.status}
              {rateLock.data.unlockReason ? ` — ${rateLock.data.unlockReason}` : ""}
            </Alert>
          )}

          {pricing.isLoading && <Skeleton variant="rectangular" height={120} />}
          {pricing.isError && <ErrorPanel title="Pricing failed" error={pricing.error} />}
          {pricing.isSuccess && !pricing.data && (
            <Typography color="text.secondary" variant="body2">
              No pricing eval yet — Re-Price requests current offers.
            </Typography>
          )}
          {pricing.data?.status === "pending" && (
            <Alert severity="info" variant="outlined" sx={{ mb: 1 }}>
              Pricing evaluation running…
            </Alert>
          )}
          {pricing.data?.status === "failed" && (
            <Alert severity="error" variant="outlined" sx={{ mb: 1 }}>
              {pricing.data.error ?? "pricing evaluation failed"}
            </Alert>
          )}
          {products.map((p) => (
            <Box key={p.productCode} sx={{ mb: 1.5 }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                {p.productName ?? p.productCode}
                {!p.eligible && (
                  <Chip
                    size="small"
                    color="warning"
                    label="ineligible"
                    sx={{ ml: 1 }}
                    title={p.ineligibilityReasons.join("; ")}
                  />
                )}
              </Typography>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ pl: 0, py: 0.5 }}>Rate</TableCell>
                    <TableCell align="right" sx={{ py: 0.5 }}>Price</TableCell>
                    <TableCell align="right" sx={{ py: 0.5 }}>Term</TableCell>
                    <TableCell align="right" sx={{ py: 0.5 }} />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {p.offers.map((o) => (
                    <TableRow key={`${o.rate}-${o.lockTermDays}-${o.price}`}>
                      <TableCell sx={{ pl: 0, border: 0, py: 0.5 }}>{o.rate}%</TableCell>
                      <TableCell align="right" sx={{ border: 0, py: 0.5 }}>
                        {o.price ?? "—"}
                      </TableCell>
                      <TableCell align="right" sx={{ border: 0, py: 0.5 }}>
                        {o.lockTermDays ? `${o.lockTermDays}d` : "—"}
                      </TableCell>
                      <TableCell align="right" sx={{ border: 0, py: 0.5 }}>
                        {isStaff && p.eligible && rateLock.data?.status !== "active" && (
                          <Button
                            size="small"
                            startIcon={<LockIcon />}
                            onClick={() => doLock(p, o)}
                            disabled={lockRate.isPending}
                            aria-label={`lock ${p.productCode} ${o.rate}`}
                          >
                            Lock
                          </Button>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
          ))}
        </Paper>

        {/* —— Column 3: Documents & vault —— */}
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Box sx={{ display: "flex", alignItems: "center", mb: 1 }}>
            <Typography variant="subtitle1" sx={{ flexGrow: 1 }}>
              Packages &amp; documents
            </Typography>
            <Button
              size="small"
              startIcon={<DescriptionIcon />}
              onClick={() => setPkgOpen(true)}
              disabled={!isStaff}
            >
              Generate
            </Button>
          </Box>
          {documents.isLoading && <Skeleton variant="rectangular" height={80} />}
          {documents.isError && <ErrorPanel title="Documents failed" error={documents.error} />}
          <Table size="small">
            <TableBody>
              {(documents.data ?? []).map((d) => (
                <TableRow key={d.docId}>
                  <TableCell sx={{ pl: 0, py: 0.5 }}>
                    <Typography variant="body2">{d.packageName}</Typography>
                    <Typography variant="caption" color="text.secondary">
                      {d.kind}
                      {d.error ? ` — ${d.error}` : ""}
                    </Typography>
                  </TableCell>
                  <TableCell align="right" sx={{ py: 0.5, whiteSpace: "nowrap" }}>
                    <Chip
                      size="small"
                      label={d.status}
                      color={statusColor(d.status) as "success" | "error" | "default"}
                      variant="outlined"
                    />
                    {d.status === "completed" && tid && (
                      <IconButton
                        size="small"
                        component="a"
                        href={downloadUrl(tid, appId, d.docId)}
                        target="_blank"
                        aria-label={`download ${d.packageName}`}
                      >
                        <DownloadIcon sx={{ fontSize: 15 }} />
                      </IconButton>
                    )}
                  </TableCell>
                </TableRow>
              ))}
              {documents.isSuccess && documents.data!.length === 0 && (
                <TableRow>
                  <TableCell sx={{ pl: 0, border: 0 }}>
                    <Typography color="text.secondary" variant="body2">
                      No packages yet — Generate requests one.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>

          <Divider sx={{ my: 1.5 }} />
          <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
            Borrower vault uploads
          </Typography>
          {uploads.isLoading && <Skeleton variant="rectangular" height={60} />}
          <Table size="small">
            <TableBody>
              {(uploads.data ?? []).map((u) => (
                <TableRow key={u.vaultDocId}>
                  <TableCell sx={{ pl: 0, py: 0.5 }}>
                    <Typography variant="body2">{u.filename}</Typography>
                    <Typography variant="caption" color="text.secondary">
                      {u.classification} · {(u.sizeBytes / 1024).toFixed(0)} KB
                      {u.error ? ` — ${u.error}` : ""}
                    </Typography>
                  </TableCell>
                  <TableCell align="right" sx={{ py: 0.5, whiteSpace: "nowrap" }}>
                    <Chip
                      size="small"
                      label={u.status}
                      color={statusColor(u.status) as "success" | "error" | "default"}
                      variant="outlined"
                    />
                    {u.status === "clean" && tid && (
                      <IconButton
                        size="small"
                        component="a"
                        href={downloadUrl(tid, appId, u.vaultDocId, true)}
                        target="_blank"
                        aria-label={`download ${u.filename}`}
                      >
                        <DownloadIcon sx={{ fontSize: 15 }} />
                      </IconButton>
                    )}
                  </TableCell>
                </TableRow>
              ))}
              {uploads.isSuccess && uploads.data!.length === 0 && (
                <TableRow>
                  <TableCell sx={{ pl: 0, border: 0 }}>
                    <Typography color="text.secondary" variant="body2">
                      No borrower uploads.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </Paper>
      </Box>

      {/* Fee override dialog */}
      <Dialog open={!!overrideFor} onClose={() => setOverrideFor(null)} maxWidth="xs" fullWidth>
        <DialogTitle>Fee override — {overrideFor?.label ?? overrideFor?.feeCode}</DialogTitle>
        <DialogContent>
          <TextField
            label="Override amount (empty = clear override)"
            size="small"
            fullWidth
            sx={{ mt: 1 }}
            value={overrideAmount}
            onChange={(e) => setOverrideAmount(e.target.value)}
          />
          <TextField
            label="Reason (required)"
            size="small"
            fullWidth
            sx={{ mt: 2 }}
            value={overrideReason}
            onChange={(e) => setOverrideReason(e.target.value)}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setOverrideFor(null)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!overrideReason.trim() || overrideFee.isPending}
            onClick={doOverride}
          >
            Apply
          </Button>
        </DialogActions>
      </Dialog>

      {/* Unlock dialog */}
      <Dialog open={unlockOpen} onClose={() => setUnlockOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>Unlock rate</DialogTitle>
        <DialogContent>
          <TextField
            autoFocus
            label="Reason (required)"
            size="small"
            fullWidth
            sx={{ mt: 1 }}
            value={unlockReason}
            onChange={(e) => setUnlockReason(e.target.value)}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setUnlockOpen(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!unlockReason.trim() || unlockRate.isPending}
            onClick={doUnlock}
          >
            Unlock
          </Button>
        </DialogActions>
      </Dialog>

      {/* Generate package dialog */}
      <Dialog open={pkgOpen} onClose={() => setPkgOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>Generate document package</DialogTitle>
        <DialogContent>
          <TextField
            select
            label="Kind"
            size="small"
            fullWidth
            sx={{ mt: 1 }}
            value={pkgKind}
            onChange={(e) => setPkgKind(e.target.value)}
          >
            {PACKAGE_KINDS.map((k) => (
              <MenuItem key={k.value} value={k.value}>
                {k.label}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            label="Reason codes (comma-separated, optional)"
            size="small"
            fullWidth
            sx={{ mt: 2 }}
            value={pkgReasons}
            onChange={(e) => setPkgReasons(e.target.value)}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setPkgOpen(false)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={requestPackage.isPending}
            onClick={doPackage}
          >
            Generate
          </Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
