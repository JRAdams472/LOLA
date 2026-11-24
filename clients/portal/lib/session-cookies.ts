/**
 * Session-cookie mechanics (MPE-290): the browser holds ONE opaque
 * `__Host-` cookie — a random handle into the API's server-side session
 * store. Token material never touches the browser. Pre-cutover chunked
 * JWE cookies are detected for unconditional rejection + cleanup.
 *
 * Pure helpers (no `server-only`, no next/headers) so proxy.ts and the
 * jest tables can use the same constants.
 */

/** The opaque session handle cookie — `__Host-` pins host scope; the
 *  Secure flag is mandatory for the prefix so it's always set. */
export const HANDLE_COOKIE = "__Host-lola_portal";
/** Pre-cutover sealed-JWE cookie base (chunked `lola_portal.N`). */
export const LEGACY_COOKIE = "lola_portal";

/** Any pre-cutover token-bearing cookie — never honored post-MPE-290. */
export function isLegacySessionCookie(name: string): boolean {
  return name === LEGACY_COOKIE || name.startsWith(`${LEGACY_COOKIE}.`);
}

/** Minimal surface of the Next cookie store the helpers need —
 *  structurally compatible with `await cookies()`. */
export interface CookieStore {
  get(name: string): { value: string } | undefined;
  getAll(): { name: string; value: string }[];
  delete(name: string): void;
}

/** Sweep every legacy chunk — cutover cleanup and dead-session paths. */
export function clearLegacyCookies(store: CookieStore): void {
  for (const c of store.getAll()) {
    if (isLegacySessionCookie(c.name)) store.delete(c.name);
  }
}
