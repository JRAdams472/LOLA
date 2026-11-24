import {
  HANDLE_COOKIE,
  LEGACY_COOKIE,
  clearLegacyCookies,
  isLegacySessionCookie,
  type CookieStore,
} from "@/lib/session-cookies";

/** MPE-290: cookie mechanics — one opaque `__Host-` handle; every
 *  pre-cutover chunk is legacy and unconditionally swept. */

describe("isLegacySessionCookie", () => {
  it.each([
    ["lola_session", true],
    ["lola_session.0", true],
    ["lola_session.12", true],
    ["__Host-lola_session", false],
    ["lola_sessionX", false],
    ["lola_portal", false],
    ["lola_pkce", false],
  ])("%s → %s", (name, expected) => {
    expect(isLegacySessionCookie(name)).toBe(expected);
  });

  it("constants keep the naming contract", () => {
    expect(HANDLE_COOKIE).toBe("__Host-lola_session");
    expect(LEGACY_COOKIE).toBe("lola_session");
  });
});

describe("clearLegacyCookies", () => {
  function jar(entries: Record<string, string>): CookieStore & {
    deleted: string[];
  } {
    const deleted: string[] = [];
    return {
      deleted,
      get: (n) => (n in entries ? { value: entries[n] } : undefined),
      getAll: () =>
        Object.entries(entries).map(([name, value]) => ({ name, value })),
      delete: (n) => deleted.push(n),
    };
  }

  it("deletes every legacy chunk, keeps everything else", () => {
    const store = jar({
      [HANDLE_COOKIE]: "handle",
      "lola_session.0": "a",
      "lola_session.1": "b",
      lola_session: "c",
      lola_pkce: "pkce",
      other: "x",
    });
    clearLegacyCookies(store);
    expect(store.deleted.sort()).toEqual([
      "lola_session",
      "lola_session.0",
      "lola_session.1",
    ]);
  });

  it("is a no-op when no legacy cookies exist", () => {
    const store = jar({ [HANDLE_COOKIE]: "h" });
    clearLegacyCookies(store);
    expect(store.deleted).toEqual([]);
  });
});
