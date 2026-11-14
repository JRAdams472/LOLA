/**
 * Post-login redirect target: a same-origin path or "/". `//host` is an
 * obvious off-site hop, but `/\host` is one too — WHATWG URL parsing
 * treats `\` as a path separator on special schemes, so `new URL` would
 * normalize it to `//host`. Both routes that accept `next` use this.
 */
export function safeNextPath(next: string | null | undefined): string {
  if (
    !next ||
    !next.startsWith("/") ||
    next.startsWith("//") ||
    next.includes("\\")
  ) {
    return "/";
  }
  return next;
}
