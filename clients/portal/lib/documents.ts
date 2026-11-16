"use client";

const BFF = "/bff/api";

/** Classifications the vault allowlist accepts — keep in sync with
 *  UploadAllowlist.Classifications; the label is borrower language. */
export const CLASSIFICATIONS: { key: string; label: string }[] = [
  { key: "paystub", label: "Pay stub" },
  { key: "w2", label: "W-2" },
  { key: "bank-statement", label: "Bank statement" },
  { key: "id", label: "Photo ID" },
  { key: "other", label: "Other document" },
];

export const classificationLabel = (key: string): string =>
  CLASSIFICATIONS.find((c) => c.key === key)?.label ?? "Document";

export type UploadResult =
  | { ok: true }
  | { ok: false; message: string };

/**
 * Multipart upload through the BFF — the browser sends FormData; the
 * BFF streams it through with the session token. The API sniffs magic
 * bytes and enforces a 25 MiB cap server-side; disallowed types come
 * back 415, oversize 413.
 */
export async function uploadDoc(
  tid: string,
  appId: string,
  classification: string,
  file: File,
): Promise<UploadResult> {
  const form = new FormData();
  form.append("file", file, file.name);
  try {
    const res = await fetch(
      `${BFF}/tenants/${tid}/applications/${appId}/uploads` +
        `?classification=${encodeURIComponent(classification)}`,
      { method: "POST", body: form },
    );
    if (res.status === 401) {
      // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- plain lib code (no useRouter); /auth/login is a route handler, needs a document navigation
      window.location.assign(
        `/auth/login?next=${encodeURIComponent(window.location.pathname)}`,
      );
      return { ok: false, message: "Please sign in again." };
    }
    if (res.status === 413)
      return {
        ok: false,
        message: "That file is too large — the limit is 25 MB.",
      };
    if (res.status === 415)
      return {
        ok: false,
        message:
          "We can't read that file type — please upload a PDF, image, " +
          "or Word document.",
      };
    if (res.status >= 400)
      return {
        ok: false,
        message:
          "The upload didn't go through — please try again in a moment.",
      };
    return { ok: true };
  } catch {
    return {
      ok: false,
      message: "The upload didn't go through — please try again in a moment.",
    };
  }
}

/** Audited download — the endpoint 302s to a minted capability URL
 *  which the BFF forwards; a new tab follows it to the content. The
 *  bytes never transit the BFF and no token reaches the browser. */
export const downloadUrl = (
  tid: string,
  appId: string,
  docId: string,
  vault = false,
): string =>
  `${BFF}/tenants/${tid}/applications/${appId}/` +
  `${vault ? "uploads" : "documents"}/${docId}/download`;
