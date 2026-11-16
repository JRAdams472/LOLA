"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";

import AppShell from "@/components/AppShell";
import {
  CLASSIFICATIONS,
  classificationLabel,
  downloadUrl,
  uploadDoc,
} from "@/lib/documents";
import { usePortalLoans, type PortalLoan } from "@/lib/loans";
import { useMe } from "@/lib/me";

const CHIP: Record<string, string> = {
  received: "border-emerald-700 bg-emerald-950/40 text-emerald-300",
  checking: "border-border bg-surface-2 text-muted",
  rejected: "border-danger/40 bg-danger/10 text-danger",
};

function formatSize(bytes: number): string {
  if (bytes >= 1 << 20) return `${(bytes / (1 << 20)).toFixed(1)} MB`;
  if (bytes >= 1 << 10) return `${Math.round(bytes / (1 << 10))} KB`;
  return `${bytes} B`;
}

function DocumentsInner() {
  const me = useMe();
  const params = useSearchParams();
  const loans = usePortalLoans(me.status === "ready" ? me.me.tid : undefined);
  const [classification, setClassification] = useState("paystub");
  const [uploading, setUploading] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const loan: PortalLoan | undefined =
    loans.status === "ready"
      ? (loans.loans.find((l) => l.id === params.get("app")) ?? loans.loans[0])
      : undefined;
  const tid = me.status === "ready" ? me.me.tid : null;
  const reload = loans.status === "ready" ? loans.reload : undefined;

  // Poll while the scanner is still deciding — clean/infected lands
  // off the request path.
  const checking = loan?.uploads.some((u) => u.statusKey === "checking");
  useEffect(() => {
    if (!checking || !reload) return;
    const t = setInterval(reload, 4000);
    return () => clearInterval(t);
  }, [checking, reload]);

  async function onUpload() {
    const file = fileRef.current?.files?.[0];
    if (!file || !tid || !loan) return;
    setUploading(true);
    setNotice(null);
    setProblem(null);
    const res = await uploadDoc(tid, loan.id, classification, file);
    setUploading(false);
    if (!res.ok) {
      setProblem(res.message);
      return;
    }
    setNotice("Received — we're checking it now. This usually takes a few seconds.");
    if (fileRef.current) fileRef.current.value = "";
    reload?.();
  }

  /** Rejected row → preset the classification and reopen the picker. */
  function retry(key: string) {
    setClassification(key);
    setProblem(null);
    fileRef.current?.click();
  }

  return (
    <AppShell>
      <h1 className="text-xl font-semibold">Documents</h1>
      <p className="mt-2 text-sm text-muted">
        Your loan officer may ask for documents like pay stubs, W-2s, or
        bank statements — they help verify your application. Everything
        you send is checked for safety and stored encrypted. You can
        download anything you&apos;ve sent, and any packages we&apos;ve
        prepared for you.
      </p>

      {loans.status === "loading" && (
        <p className="mt-6 text-sm text-muted">Loading your documents…</p>
      )}
      {loans.status === "error" && (
        <div
          role="alert"
          className="mt-6 rounded-lg border border-danger/40 bg-danger/10 p-4 text-sm text-danger"
        >
          We couldn&apos;t load your documents. Please try again in a moment.
        </div>
      )}

      {loan && tid && (
        <>
          {/* Upload */}
          <section className="mt-6 rounded-lg border border-border bg-surface p-5">
            <h2 className="text-sm font-medium">Send us a document</h2>
            <p className="mt-1 text-xs text-muted">
              PDF, photo (PNG/JPG/TIFF), or Word document — up to 25 MB.
            </p>
            <div className="mt-4 flex flex-wrap items-end gap-3">
              <label className="block">
                <span className="text-xs text-muted">Document type</span>
                <select
                  value={classification}
                  onChange={(e) => setClassification(e.target.value)}
                  className="mt-1 block rounded-md border border-border bg-surface-2 px-3 py-2 text-sm text-text"
                >
                  {CLASSIFICATIONS.map((c) => (
                    <option key={c.key} value={c.key}>
                      {c.label}
                    </option>
                  ))}
                </select>
              </label>
              <label className="block">
                <span className="text-xs text-muted">File</span>
                <input
                  ref={fileRef}
                  type="file"
                  accept=".pdf,.png,.jpg,.jpeg,.tiff,.tif,.docx"
                  className="mt-1 block text-sm text-muted file:mr-3 file:rounded-md file:border-0 file:bg-surface-2 file:px-3 file:py-2 file:text-sm file:text-text hover:file:bg-border"
                />
              </label>
              <button
                type="button"
                onClick={onUpload}
                disabled={uploading}
                className="rounded-md bg-accent-strong px-4 py-2 text-sm font-medium text-white hover:bg-accent disabled:opacity-50"
              >
                {uploading ? "Sending…" : "Upload"}
              </button>
            </div>
            {notice && (
              <p className="mt-3 text-sm text-emerald-300" role="status">
                {notice}
              </p>
            )}
            {problem && (
              <p className="mt-3 text-sm text-danger" role="alert">
                {problem}
              </p>
            )}
          </section>

          {/* Uploads */}
          <section className="mt-6 rounded-lg border border-border bg-surface p-5">
            <h2 className="text-sm font-medium">Files you&apos;ve sent us</h2>
            {loan.uploads.length === 0 ? (
              <p className="mt-2 text-sm text-muted">
                Nothing yet — when you upload a document it will show up
                here.
              </p>
            ) : (
              <ul className="mt-3 divide-y divide-border">
                {loan.uploads.map((u) => (
                  <li
                    key={u.vaultDocId}
                    className="flex flex-wrap items-center gap-x-3 gap-y-1 py-3 text-sm"
                  >
                    <span className="min-w-0 flex-1 truncate text-text">
                      {u.filename}
                    </span>
                    <span className="text-xs text-muted">
                      {classificationLabel(u.classification)} ·{" "}
                      {formatSize(u.sizeBytes)}
                    </span>
                    <span
                      className={`rounded-full border px-2 py-0.5 text-xs ${CHIP[u.statusKey] ?? CHIP.checking}`}
                    >
                      {u.statusLabel}
                    </span>
                    {u.statusKey === "received" && (
                      <a
                        href={downloadUrl(tid, loan.id, u.vaultDocId, true)}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-xs text-accent hover:underline"
                      >
                        Download
                      </a>
                    )}
                    {u.statusKey === "rejected" && (
                      <button
                        type="button"
                        onClick={() => retry(u.classification)}
                        className="text-xs text-accent hover:underline"
                      >
                        Try again
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </section>

          {/* Disclosures / packages */}
          <section className="mt-6 rounded-lg border border-border bg-surface p-5">
            <h2 className="text-sm font-medium">Documents from us</h2>
            {loan.documents.length === 0 ? (
              <p className="mt-2 text-sm text-muted">
                Nothing ready yet — when your loan officer prepares a
                document package it will appear here.
              </p>
            ) : (
              <ul className="mt-3 divide-y divide-border">
                {loan.documents.map((d) => (
                  <li
                    key={d.docId}
                    className="flex flex-wrap items-center gap-x-3 gap-y-1 py-3 text-sm"
                  >
                    <span className="min-w-0 flex-1 truncate text-text">
                      {d.packageName}
                    </span>
                    <span className="text-xs text-muted">{d.kindLabel}</span>
                    {d.ready ? (
                      <a
                        href={downloadUrl(tid, loan.id, d.docId)}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="rounded-md border border-border px-3 py-1 text-xs text-text hover:bg-surface-2"
                      >
                        Download
                      </a>
                    ) : (
                      <span className="text-xs text-muted">Not ready yet</span>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}

      {loans.status === "ready" && loans.loans.length === 0 && (
        <section className="mt-6 rounded-lg border border-border bg-surface p-6 text-sm text-muted">
          You don&apos;t have an application yet —{" "}
          <Link href="/apply" className="text-accent hover:underline">
            start one
          </Link>{" "}
          first, then come back here to send documents.
        </section>
      )}
    </AppShell>
  );
}

export default function DocumentsPage() {
  return (
    <Suspense>
      <DocumentsInner />
    </Suspense>
  );
}
