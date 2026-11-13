import { NextRequest, NextResponse } from "next/server";

/**
 * Bounce unauthenticated page loads to the login flow. Presence-only check —
 * the JWE is verified server-side in the BFF/auth routes (a stale cookie
 * fails there and the client bounces back to login on the 401).
 */
export function proxy(req: NextRequest) {
  // The session JWE is chunked across lola_session.N cookies — presence of
  // the first chunk (or a legacy unchunked cookie) counts as a session.
  if (
    req.cookies
      .getAll()
      .some((c) => c.name === "lola_session" || c.name.startsWith("lola_session."))
  ) {
    return NextResponse.next();
  }
  const url = req.nextUrl.clone();
  url.pathname = "/auth/login";
  url.search = `?next=${encodeURIComponent(req.nextUrl.pathname)}`;
  return NextResponse.redirect(url);
}

export const config = {
  matcher: ["/((?!auth|callback|bff|_next/static|_next/image|favicon.ico).*)"],
};
