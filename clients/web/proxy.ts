import { NextRequest, NextResponse } from "next/server";

import { HANDLE_COOKIE } from "@/lib/session-cookies";

/**
 * Bounce unauthenticated page loads to the login flow. Presence-only
 * check on the opaque `__Host-` handle — the API session store is the
 * arbiter; a stale or foreign handle still lands on login (the BFF/auth
 * routes resolve it server-side and a dead handle bounces back on 401).
 */
export function proxy(req: NextRequest) {
  if (req.cookies.getAll().some((c) => c.name === HANDLE_COOKIE)) {
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
