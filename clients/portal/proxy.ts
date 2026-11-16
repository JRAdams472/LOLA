import { NextRequest, NextResponse } from "next/server";

/**
 * Bounce unauthenticated page loads to the login flow. Presence-only check —
 * the JWE is verified server-side in the BFF/auth routes (a stale cookie
 * fails there and the client bounces back to login on the 401).
 */
export function proxy(req: NextRequest) {
  // The session JWE is chunked across lola_portal.N cookies — presence of
  // the first chunk (or a legacy unchunked cookie) counts as a session.
  if (
    req.cookies
      .getAll()
      .some((c) => c.name === "lola_portal" || c.name.startsWith("lola_portal."))
  ) {
    return NextResponse.next();
  }
  const url = req.nextUrl.clone();
  url.pathname = "/auth/login";
  url.search = `?next=${encodeURIComponent(req.nextUrl.pathname)}`;
  return NextResponse.redirect(url);
}

export const config = {
  // Public paths: auth + callback (OIDC flow), bff (session-checked
  // itself), api (the public invite-accept route), invites/accept
  // (the borrower has no account yet — the invite token IS the
  // credential).
  matcher: [
    "/((?!auth|callback|bff|api|invites/accept|_next/static|_next/image|favicon.ico).*)",
  ],
};
