import { NextRequest, NextResponse } from "next/server";

import { beginLogin } from "@/lib/auth";
import { safeNextPath } from "@/lib/nav";

/** Start auth-code+PKCE: stash verifier/state, bounce to the IdP. */
export async function GET(req: NextRequest) {
  const next = safeNextPath(req.nextUrl.searchParams.get("next"));
  return NextResponse.redirect(await beginLogin(next));
}
