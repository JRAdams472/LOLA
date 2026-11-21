"use client";

import { use, useRef, useState } from "react";

import AppShell from "@/components/AppShell";
import { useMe } from "@/lib/me";
import {
  uploadDoc,
  usePartnerApplication,
  usePartnerConditions,
  usePartnerUploads,
} from "@/lib/partner";

/**
 * Partner application detail — metadata, conditions, and the
 * partner's own uploads. The masked metadata projection is all the
 * server sends; nothing here can reach staff fields.
 */
export default function PartnerApplicationPage({
  params,
}: {
  params: Promise<{ appId: string }>;
}) {
  const { appId } = use(params);
  const me = useMe();
  const tid = me.status === "ready" ? me.me.tid : undefined;
  const app = usePartnerApplication(tid, appId);
  const conditions = usePartnerConditions(tid, appId);
  const uploads = usePartnerUploads(tid, appId);

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);

  const onUpload = async (file: File) => {
    if (!tid) return;
    setUploading(true);
    setUploadError(null);
    const result = await uploadDoc(tid, appId, "other", file);
    setUploading(false);
    if (!result.ok) setUploadError(result.message);
    else if (uploads.status === "ready") uploads.reload();
  };

  return (
    <AppShell>
      <h1 className="text-xl font-semibold">Application {appId}</h1>

      {app.status === "ready" && (
        <section className="mt-4 rounded-lg border border-border bg-surface p-4">
          <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
            <dt className="text-muted">Status</dt>
            <dd>{app.data.status}</dd>
            <dt className="text-muted">Step</dt>
            <dd>{app.data.pipelineStep}</dd>
            {app.data.loanAmount && (
              <>
                <dt className="text-muted">Loan amount</dt>
                <dd>{app.data.loanAmount}</dd>
              </>
            )}
            {app.data.submittedUtc && (
              <>
                <dt className="text-muted">Submitted</dt>
                <dd>{app.data.submittedUtc.slice(0, 10)}</dd>
              </>
            )}
          </dl>
        </section>
      )}

      {app.status === "error" && (
        <div
          role="alert"
          className="mt-4 rounded-lg border border-danger/40 bg-danger/10 p-4 text-sm text-danger"
        >
          {app.error}
        </div>
      )}

      <section className="mt-6">
        <h2 className="text-sm font-medium">Conditions</h2>
        {conditions.status === "ready" &&
          (conditions.data.length === 0 ? (
            <p className="mt-2 text-sm text-muted">
              No conditions on this application yet.
            </p>
          ) : (
            <ul className="mt-2 flex flex-col gap-2">
              {conditions.data.map((c) => (
                <li
                  key={c.conditionId}
                  className="flex items-center justify-between rounded-lg border border-border bg-surface px-4 py-2 text-sm"
                >
                  <span>{c.label}</span>
                  <span className="text-xs text-muted">{c.statusLabel}</span>
                </li>
              ))}
            </ul>
          ))}
      </section>

      <section className="mt-6">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-medium">Your documents</h2>
          <button
            type="button"
            disabled={uploading || !tid}
            onClick={() => fileInputRef.current?.click()}
            className="rounded-md border border-accent px-3 py-1 text-xs text-accent disabled:opacity-50"
          >
            {uploading ? "Uploading…" : "Upload a document"}
          </button>
          <input
            ref={fileInputRef}
            type="file"
            className="hidden"
            onChange={(e) => {
              const f = e.target.files?.[0];
              if (f) void onUpload(f);
              e.target.value = "";
            }}
          />
        </div>
        {uploadError && (
          <p role="alert" className="mt-2 text-xs text-danger">
            {uploadError}
          </p>
        )}
        {uploads.status === "ready" &&
          (uploads.data.length === 0 ? (
            <p className="mt-2 text-sm text-muted">
              Documents you upload for this application appear here.
            </p>
          ) : (
            <ul className="mt-2 flex flex-col gap-2">
              {uploads.data.map((d) => (
                <li
                  key={d.vaultDocId}
                  className="flex items-center justify-between rounded-lg border border-border bg-surface px-4 py-2 text-sm"
                >
                  <span>{d.filename}</span>
                  <span className="text-xs text-muted">{d.statusLabel}</span>
                </li>
              ))}
            </ul>
          ))}
      </section>
    </AppShell>
  );
}
