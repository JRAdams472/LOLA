import { NextRequest, NextResponse } from "next/server";

import { clearSession, CLIENT_ID } from "@/lib/auth";

const ISSUER = process.env.OIDC_ISSUER ?? "http://localhost:8200";

/**
 * Drop the session cookie, then end the IdP session — /connect/logout
 * clears the API's login cookie and honors the registered
 * post_logout_redirect_uri back to /auth/login, so sign-out actually
 * signs out (a lingering IdP cookie would silently re-issue a code).
 */
export async function GET(req: NextRequest) {
  await clearSession();
  const back = new URL("/auth/login", req.nextUrl.origin).toString();
  const url = new URL("/connect/logout", ISSUER);
  url.searchParams.set("post_logout_redirect_uri", back);
  url.searchParams.set("client_id", CLIENT_ID);
  return NextResponse.redirect(url);
}
