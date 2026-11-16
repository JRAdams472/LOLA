/**
 * Portal session bounds (M12, MPE-220): "short-lived sessions + idle
 * timeout" — pure decision functions so the policy is unit-testable
 * without the Next cookie store. `lib/auth.ts` wires these into the
 * sealed-JWE lifecycle.
 *
 *   idle window    — `PORTAL_SESSION_IDLE_MIN` (default 30m), sliding:
 *                    activity through the BFF pushes `lastActive` forward
 *   absolute bound — `PORTAL_SESSION_MAX_H` (default 4h): refresh can
 *                    never extend a session past `absExp`
 *   touch throttle — re-sealing every BFF call churns cookies for no
 *                    gain; `needsTouch` rewrites at most once/minute,
 *                    so idle accuracy is ±60s
 */

export const IDLE_MS =
  Number(process.env.PORTAL_SESSION_IDLE_MIN ?? 30) * 60_000;
export const ABSOLUTE_MS =
  Number(process.env.PORTAL_SESSION_MAX_H ?? 4) * 3_600_000;
export const TOUCH_MIN_MS = 60_000;

/** No lastActive recorded → treat as expired (defense: never honor a
 *  session that can't prove when it last acted). */
export function idleExpired(
  lastActive: number | undefined,
  now: number,
): boolean {
  return lastActive === undefined || now - lastActive > IDLE_MS;
}

export function absoluteExpired(
  absExp: number | undefined,
  now: number,
): boolean {
  return absExp === undefined || now >= absExp;
}

/** Slide the idle window only when it's meaningfully stale. */
export function needsTouch(
  lastActive: number | undefined,
  now: number,
): boolean {
  return lastActive === undefined || now - lastActive >= TOUCH_MIN_MS;
}

/** Cookie maxAge (seconds) bounded by cap, refresh-grant expiry, and the
 *  absolute session bound — whichever dies first. `capS` is seconds;
 *  `absExp`/`rtExp`/`now` are epoch-ms — work in ms and convert once. */
export function sessionMaxAgeS(
  absExp: number | undefined,
  rtExp: number | undefined,
  now: number,
  capS: number,
): number {
  let boundMs = capS * 1000;
  if (rtExp !== undefined) boundMs = Math.min(boundMs, rtExp - now);
  if (absExp !== undefined) boundMs = Math.min(boundMs, absExp - now);
  return Math.max(1, Math.floor(boundMs / 1000));
}
