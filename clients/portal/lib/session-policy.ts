/**
 * Portal session bounds (M12, MPE-220; custody moved server-side in
 * MPE-290): "short-lived sessions + idle timeout". These constants are
 * handed to the API session store at create — enforcement (idle slide,
 * absolute bound, revoke) lives in the store, not in a sealed cookie.
 *
 *   idle window    — `PORTAL_SESSION_IDLE_MIN` (default 30m), sliding:
 *                    the store moves `last_active` on live resolves
 *   absolute bound — `PORTAL_SESSION_MAX_H` (default 4h): write-once at
 *                    create; no refresh or activity can extend it
 */

export const IDLE_MS =
  Number(process.env.PORTAL_SESSION_IDLE_MIN ?? 30) * 60_000;
export const ABSOLUTE_MS =
  Number(process.env.PORTAL_SESSION_MAX_H ?? 4) * 3_600_000;
