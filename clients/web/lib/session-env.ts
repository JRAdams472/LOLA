/**
 * Production session-env gate (MPE-277): a production build of the BFF
 * must have explicitly safe session configuration — real independent
 * SESSION_SECRET, Secure cookies, https on every OIDC/API hop.
 *
 * `next start` sets NODE_ENV=production, and the dev compose runs those
 * same production-built images — so NODE_ENV alone can't distinguish a
 * deployment from the dev stack. Strictness is ON by default and only
 * an explicit insecure flag (`ALLOW_INSECURE_SESSION_ENV=1`, set only
 * by the dev compose) relaxes it — fail-closed, never opt-in-safe.
 *
 * Pure functions for table-driven jest tests; auth.ts calls
 * assertSessionEnv lazily from sessionKey()/beginLogin so `next build`
 * module imports never throw.
 */

const DEV_SECRETS = new Set([
  "dev-session-secret-not-for-prod",
  "dev-portal-session-secret-not-for-prod",
]);

const REQUIRED_HTTPS_KEYS = [
  "OIDC_ISSUER",
  "OIDC_TOKEN_URL",
  "OIDC_AUTHORIZE_URL",
  "OIDC_REDIRECT_URI",
  "API_URL",
] as const;

type Env = Record<string, string | undefined>;

/** True when the production session-env rules apply. */
export function isStrictSessionEnv(env: Env = process.env): boolean {
  return (
    env.NODE_ENV === "production" &&
    env.ALLOW_INSECURE_SESSION_ENV !== "1"
  );
}

/** Throws unless every production session requirement holds. */
export function assertSessionEnv(env: Env = process.env): void {
  if (!isStrictSessionEnv(env)) return;
  const problems: string[] = [];

  const secret = env.SESSION_SECRET;
  if (!secret || Buffer.byteLength(secret) < 32 || DEV_SECRETS.has(secret))
    problems.push(
      "SESSION_SECRET must be >=32 bytes of real key material " +
        "(not a repo dev default — BFFs each need their own)",
    );

  if (env.SESSION_SECURE !== "1")
    problems.push(
      "SESSION_SECURE=1 is required — the sealed session cookie " +
        "must be Secure in production",
    );

  for (const key of REQUIRED_HTTPS_KEYS) {
    const value = env[key];
    if (!value) {
      problems.push(`${key} is required in production`);
      continue;
    }
    try {
      if (new URL(value).protocol !== "https:")
        problems.push(`${key} must be an https URL`);
    } catch {
      problems.push(`${key} must be an absolute https URL`);
    }
  }

  if (problems.length > 0)
    throw new Error(
      "Production BFF env invalid — refusing unsafe session config:\n" +
        problems.map((p) => `  - ${p}`).join("\n"),
    );
}
