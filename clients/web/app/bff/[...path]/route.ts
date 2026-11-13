import { NextRequest } from "next/server";

import { getSession } from "@/lib/auth";
import { isVaultUpload } from "@/lib/bff-paths";

const API_URL = process.env.API_URL ?? "http://localhost:8200";
// Mirror the API's body ceiling — the BFF buffers bodies before proxying,
// so an oversized upload must be rejected before `arrayBuffer()` runs.
const MAX_BODY = 2 * 1024 * 1024;
// Vault document uploads are capped at 25 MiB server-side — allow that
// plus multipart framing on the uploads route only.
const MAX_UPLOAD_BODY = 27 * 1024 * 1024;
const TOO_LARGE = Symbol("too-large");

/** Read the request body up to cap; TOO_LARGE sentinel past it. */
async function boundedBody(
  req: NextRequest,
  cap: number,
): Promise<ArrayBuffer | typeof TOO_LARGE> {
  const reader = req.body?.getReader();
  if (!reader) return new ArrayBuffer(0);
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > cap) {
      await reader.cancel();
      return TOO_LARGE;
    }
    chunks.push(value);
  }
  const out = new Uint8Array(total);
  let off = 0;
  for (const c of chunks) {
    out.set(c, off);
    off += c.byteLength;
  }
  return out.buffer;
}

/**
 * Same-origin proxy for every API call the browser makes — the access token
 * never reaches the client. `/bff/api/...` → `${API_URL}/api/...` (and
 * `/bff/graphql` → `/graphql`, ADR-0011).
 */
async function proxy(
  req: NextRequest,
  ctx: { params: Promise<{ path: string[] }> },
) {
  const session = await getSession();
  if (!session) {
    return Response.json({ error: "unauthenticated" }, { status: 401 });
  }
  const { path } = await ctx.params;
  const upload = isVaultUpload(path);
  const cap = upload ? MAX_UPLOAD_BODY : MAX_BODY;
  const declared = Number(req.headers.get("content-length") ?? 0);
  if (declared > cap) {
    return Response.json({ error: "body too large" }, { status: 413 });
  }
  // Vault uploads forward the request stream untouched — buffering 25 MB
  // of multipart in the BFF gains nothing; the API enforces its own
  // VAULT_MAX_BYTES cap. Everything else is buffered under `cap`.
  // content-length can be absent/lie under chunked transfer — bound the
  // actual read too (the stream case can't lie past the API's cap).
  const body = ["GET", "HEAD"].includes(req.method)
    ? undefined
    : upload
      ? req.body
      : await boundedBody(req, cap);
  if (body === TOO_LARGE) {
    return Response.json({ error: "body too large" }, { status: 413 });
  }
  const upstream = `${API_URL}/${path.join("/")}${req.nextUrl.search}`;
  const headers = new Headers();
  headers.set("authorization", `Bearer ${session.at}`);
  const contentType = req.headers.get("content-type");
  if (contentType) headers.set("content-type", contentType);

  const res = await fetch(upstream, {
    method: req.method,
    headers,
    body,
    // @ts-expect-error — required for streaming request bodies in undici
    duplex: "half",
  });
  return new Response(res.body, {
    status: res.status,
    headers: {
      "content-type": res.headers.get("content-type") ?? "application/json",
    },
  });
}

export {
  proxy as DELETE,
  proxy as GET,
  proxy as PATCH,
  proxy as POST,
  proxy as PUT,
};
