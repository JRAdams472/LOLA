import {
  assertSessionEnv,
  isStrictSessionEnv,
} from "@/lib/session-env";

/**
 * MPE-277: the BFF production session gate — strict by default on a
 * production build, relaxed ONLY by the explicit dev opt-out flag.
 */
const PROD_BASE = {
  NODE_ENV: "production",
  SESSION_SECRET: "real-prod-secret-material-0123456789abcdef",
  SESSION_SECURE: "1",
  OIDC_ISSUER: "https://idp.example.com",
  OIDC_TOKEN_URL: "https://api.example.com/connect/token",
  OIDC_AUTHORIZE_URL: "https://idp.example.com/connect/authorize",
  OIDC_REDIRECT_URI: "https://console.example.com/callback",
  API_URL: "https://api.example.com",
};

describe("isStrictSessionEnv", () => {
  it.each([
    ["production build, no flag", { NODE_ENV: "production" }, true],
    [
      "production + dev opt-out",
      { NODE_ENV: "production", ALLOW_INSECURE_SESSION_ENV: "1" },
      false,
    ],
    ["development", { NODE_ENV: "development" }, false],
    ["test", { NODE_ENV: "test" }, false],
  ])("%s", (_name, env, expected) => {
    expect(isStrictSessionEnv(env)).toBe(expected);
  });
});

describe("assertSessionEnv", () => {
  it("accepts a fully valid production env", () => {
    expect(() => assertSessionEnv(PROD_BASE)).not.toThrow();
  });

  it.each([
    ["missing SESSION_SECRET", { SESSION_SECRET: undefined }],
    ["short SESSION_SECRET", { SESSION_SECRET: "too-short" }],
    [
      "dev-default SESSION_SECRET",
      { SESSION_SECRET: "dev-session-secret-not-for-prod" },
    ],
    [
      "portal dev-default SESSION_SECRET",
      { SESSION_SECRET: "dev-portal-session-secret-not-for-prod" },
    ],
    ["SESSION_SECURE unset", { SESSION_SECURE: undefined }],
    ["SESSION_SECURE=0", { SESSION_SECURE: "0" }],
    ["http OIDC_ISSUER", { OIDC_ISSUER: "http://idp.example.com" }],
    [
      "http OIDC_TOKEN_URL",
      { OIDC_TOKEN_URL: "http://api.example.com/connect/token" },
    ],
    [
      "http OIDC_AUTHORIZE_URL",
      { OIDC_AUTHORIZE_URL: "http://idp.example.com/connect/authorize" },
    ],
    [
      "http OIDC_REDIRECT_URI",
      { OIDC_REDIRECT_URI: "http://console.example.com/callback" },
    ],
    ["http API_URL", { API_URL: "http://api.example.com" }],
    ["missing OIDC_ISSUER", { OIDC_ISSUER: undefined }],
    ["missing API_URL", { API_URL: undefined }],
    ["non-URL OIDC_ISSUER", { OIDC_ISSUER: "idp.example.com" }],
  ])("rejects: %s", (_name, overrides) => {
    expect(() =>
      assertSessionEnv({ ...PROD_BASE, ...overrides }),
    ).toThrow(/Production BFF env invalid/);
  });

  it("names the offending keys, not their values", () => {
    try {
      assertSessionEnv({
        ...PROD_BASE,
        OIDC_ISSUER: "http://idp.example.com",
      });
      throw new Error("should have thrown");
    } catch (e) {
      const msg = (e as Error).message;
      expect(msg).toContain("OIDC_ISSUER");
      expect(msg).not.toContain("http://idp.example.com");
    }
  });

  it("relaxes only under the explicit dev opt-out", () => {
    const devEnv = {
      ...PROD_BASE,
      ALLOW_INSECURE_SESSION_ENV: "1",
      SESSION_SECURE: undefined,
      SESSION_SECRET: "dev-session-secret-not-for-prod",
      OIDC_ISSUER: "http://localhost:8200",
      API_URL: "http://api:8200",
    };
    expect(() => assertSessionEnv(devEnv)).not.toThrow();
    expect(() =>
      assertSessionEnv({ ...devEnv, ALLOW_INSECURE_SESSION_ENV: "0" }),
    ).toThrow();
  });
});
