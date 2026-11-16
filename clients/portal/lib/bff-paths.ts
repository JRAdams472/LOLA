/** `…/applications/{appId}/uploads` is the only route allowed past the
 *  default BFF body cap (vault documents ≤25 MiB stream through). */
export function isVaultUpload(path: string[]): boolean {
  return (
    path.length === 6 &&
    path[0] === "api" &&
    path[1] === "tenants" &&
    path[3] === "applications" &&
    path[5] === "uploads"
  );
}

/**
 * Proxy segments are re-joined onto API_URL — a `.`/`..` segment, a
 * backslash (WHATWG URL parsing normalizes `\` to `/` for special
 * schemes, so `..\..` still escapes), or a raw `/` left over from an
 * encoded `%2F` would traverse outside the `/api` prefix upstream.
 */
export function hasUnsafeSegment(path: string[]): boolean {
  return path.some(
    (s) => s === "." || s === ".." || s.includes("\\") || s.includes("/"),
  );
}
