import { NextRequest } from "next/server";

const API_URL = process.env.API_URL ?? "http://localhost:8200";
// The accept payload is three short fields — 8 KB is generous.
const MAX_BODY = 8 * 1024;

/**
 * Public invite acceptance — the one unauthenticated API call the portal
 * makes (the invite token is the credential). Proxied server-side so the
 * browser stays same-origin (CSP `connect-src 'self'`); upstream enforces
 * the `auth-tight` rate limit.
 */
export async function POST(req: NextRequest) {
  const declared = Number(req.headers.get("content-length") ?? 0);
  if (declared > MAX_BODY) {
    return Response.json({ error: "body too large" }, { status: 413 });
  }
  const body = await req.json().catch(() => null);
  if (
    !body ||
    typeof body.token !== "string" ||
    typeof body.displayName !== "string" ||
    typeof body.password !== "string"
  ) {
    return Response.json(
      { error: "token, displayName, and password are required" },
      { status: 400 },
    );
  }
  const res = await fetch(`${API_URL}/api/invites/accept`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      token: body.token,
      displayName: body.displayName,
      password: body.password,
    }),
  });
  return new Response(res.body, {
    status: res.status,
    headers: {
      "content-type": res.headers.get("content-type") ?? "application/json",
    },
  });
}
